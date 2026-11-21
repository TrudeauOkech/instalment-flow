using InstalmentFlow.EntityFrameworkCore.Schema;
using InstalmentFlow.Storage;
using Npgsql;

namespace InstalmentFlow.EntityFrameworkCore.Persistence;

/// <summary>
/// Verifies a tenant's invariants in SQL (doc 02 §7), so verification scales with the database rather than with
/// memory: each query folds facts or ledger lines with aggregates and returns only the rows that disagree. Runs in
/// the session's tenant-scoped transaction, so row-level security limits it to one tenant.
/// </summary>
internal static class SqlVerifier
{
    private static readonly string S = InstalmentFlowDbContext.Schema;

    /// <summary>The ledger balance (debits less credits) of every account, per currency.</summary>
    private static readonly string Balances = $"""
        balances as (select ledger_account, currency, sum(debit - credit) as balance from {S}.ledger_entries group by ledger_account, currency)
        """;

    /// <summary>Active (not unwound) allocations per allocation.</summary>
    private static readonly string Active = $"""
        active as (select a.*, a.amount - coalesce((select sum(u.amount) from {S}.allocation_reversals u where u.tenant_id = a.tenant_id and u.allocation_id = a.allocation_id), 0) as active
                   from {S}.allocations a)
        """;

    private const string ComponentCode = """
        (case component when 1 then 'principal' when 2 then 'interest' when 3 then 'fee' else 'penalty' end) || (case when charge_code = '' then '' else '.' || charge_code end)
        """;

    private static readonly (string Invariant, string Sql)[] s_checks =
    [
        ("I-1", $"""
            select 'journal tx ' || journal_tx_id || ' is unbalanced in ' || currency
            from {S}.ledger_entries group by journal_tx_id, currency having sum(debit) <> sum(credit)
            """),
        ("I-19", $"""
            select 'payment ' || p.source || '/' || p.external_ref || ' has no funds lot'
            from {S}.payments p where not exists (select 1 from {S}.funds_lots l where l.tenant_id = p.tenant_id and l.payment_id = p.payment_id)
            """),
        ("I-5", $"""
            with {Active}
            select 'lot of payment ' || l.payment_id || ': original ' || l.original || ' but active allocations ' || coalesce(sum(a.active), 0)
                   || ' + remaining ' || l.remaining || ' + reversed ' || l.reversed || ' + refunded ' || l.refunded
            from {S}.funds_lots l left join active a on a.tenant_id = l.tenant_id and a.payment_id = l.payment_id
            group by l.tenant_id, l.payment_id, l.original, l.remaining, l.reversed, l.refunded
            having l.original <> coalesce(sum(a.active), 0) + l.remaining + l.reversed + l.refunded
            """),
        ("I-5", $"""
            with portions as (
              select (e->>'paymentId')::uuid as payment_id, sum((e->>'amount')::numeric) as amount
              from {S}.refunds r, jsonb_array_elements(r.portions) e where r.status <> {(short)RefundStatus.Failed} group by 1)
            select 'lot of payment ' || l.payment_id || ' shows refunded ' || l.refunded || ' and deficit ' || l.deficit || ', refunds total ' || coalesce(p.amount, 0)
            from {S}.funds_lots l left join portions p on p.payment_id = l.payment_id
            where l.refunded <> coalesce(p.amount, 0) or l.deficit > l.refunded
            """),
        ("I-8", $"""
            select 'payment ' || p.source || '/' || p.external_ref || ' of ' || p.amount || ' has ' || coalesce(sum(r.amount), 0) || ' of reversals; the lot shows '
                   || l.reversed || ' reversed and ' || l.deficit || ' deficit'
            from {S}.payments p join {S}.funds_lots l on l.tenant_id = p.tenant_id and l.payment_id = p.payment_id
            left join {S}.payment_reversals r on r.tenant_id = p.tenant_id and r.payment_id = p.payment_id
            group by p.payment_id, p.source, p.external_ref, p.amount, l.reversed, l.deficit
            having coalesce(sum(r.amount), 0) > p.amount or l.reversed + l.deficit <> coalesce(sum(r.amount), 0)
            """),
        ("I-10", $"""
            with {Balances}, held as (select account_id, currency, sum(remaining) as remaining, sum(deficit) as deficit from {S}.funds_lots where account_id is not null group by 1, 2)
            select 'account ' || h.account_id || ' unapplied ' || h.currency || ' is ' || h.remaining || ' but the journal says ' || coalesce(-b.balance, 0)
            from held h left join balances b on b.ledger_account = 'acct:' || h.account_id || ':unapplied:' || h.currency and b.currency = h.currency
            where h.remaining <> coalesce(-b.balance, 0)
            """),
        ("I-11", $"""
            with {Balances}, held as (select account_id, currency, sum(deficit) as deficit from {S}.funds_lots where account_id is not null group by 1, 2)
            select 'account ' || h.account_id || ' reversal deficit ' || h.currency || ' is ' || h.deficit || ' but the reversal receivable says ' || coalesce(b.balance, 0)
            from held h left join balances b on b.ledger_account = 'acct:' || h.account_id || ':reversal-receivable:' || h.currency and b.currency = h.currency
            where h.deficit <> coalesce(b.balance, 0)
            """),
        ("I-10", $"""
            with {Balances}, pooled as (select pool_source, currency, sum(remaining) as remaining, sum(deficit) as deficit from {S}.funds_lots where pool_source is not null group by 1, 2)
            select 'unidentified funds ' || p.pool_source || ' ' || p.currency || ' are ' || p.remaining || ' with deficit ' || p.deficit || ' but the journal disagrees'
            from pooled p
            left join balances u on u.ledger_account = 'unidentified:' || p.pool_source and u.currency = p.currency
            left join balances r on r.ledger_account = 'reversal-receivable:unidentified:' || p.pool_source and r.currency = p.currency
            where p.remaining <> coalesce(-u.balance, 0) or p.deficit <> coalesce(r.balance, 0)
            """),
        ("I-10", $"""
            with {Balances}, {Active},
            owed as (
              select tenant_id, obligation_id, component, charge_code, sum(amount) as amount from (
                select tenant_id, obligation_id, component, charge_code, amount from {S}.obligation_originated_components
                union all
                select c.tenant_id, c.obligation_id, c.component, c.charge_code, c.amount from {S}.charges c
                where not exists (select 1 from {S}.charge_reversals r where r.tenant_id = c.tenant_id and r.charge_id = c.charge_id)) x
              group by 1, 2, 3, 4),
            paid as (select obligation_id, component, charge_code, sum(active) as amount from active group by 1, 2, 3),
            waived as (select obligation_id, component, charge_code, sum(amount) as amount from {S}.waivers group by 1, 2, 3)
            select 'obligation ' || o.obligation_id || ' ' || {ComponentCode.Replace("component", "o.component", StringComparison.Ordinal).Replace("charge_code", "o.charge_code", StringComparison.Ordinal)}
                   || ' owes ' || o.amount || ' less ' || coalesce(p.amount, 0) || ' paid and ' || coalesce(w.amount, 0) || ' waived, but the journal says ' || coalesce(b.balance, 0)
            from owed o
            left join paid p on p.obligation_id = o.obligation_id and p.component = o.component and p.charge_code = o.charge_code
            left join waived w on w.obligation_id = o.obligation_id and w.component = o.component and w.charge_code = o.charge_code
            left join balances b on b.ledger_account = 'obl:' || o.obligation_id || ':' || {ComponentCode.Replace("component", "o.component", StringComparison.Ordinal).Replace("charge_code", "o.charge_code", StringComparison.Ordinal)}
            where o.amount - coalesce(p.amount, 0) - coalesce(w.amount, 0) <> coalesce(b.balance, 0)
            """),
        ("I-6", $"""
            with {Active}
            select 'instalment ' || c.obligation_id || ' v' || c.plan_version || ' #' || c.sequence || ' ' || {ComponentCode.Replace("component", "c.component", StringComparison.Ordinal).Replace("charge_code", "c.charge_code", StringComparison.Ordinal)}
                   || ': allocated ' || c.allocated || ', waived ' || c.waived || ', charged ' || c.charged || ' disagree with the facts'
            from {S}.instalment_components c
            where c.allocated <> coalesce((select sum(a.active) from active a where a.tenant_id = c.tenant_id and a.obligation_id = c.obligation_id and a.plan_version = c.plan_version
                                           and a.sequence = c.sequence and a.component = c.component and a.charge_code = c.charge_code), 0)
               or c.waived <> coalesce((select sum(w.amount) from {S}.waivers w where w.tenant_id = c.tenant_id and w.obligation_id = c.obligation_id and w.plan_version = c.plan_version
                                        and w.sequence = c.sequence and w.component = c.component and w.charge_code = c.charge_code), 0)
               or c.charged <> coalesce((select sum(x.amount) from {S}.charges x where x.tenant_id = c.tenant_id and x.obligation_id = c.obligation_id and x.plan_version = c.plan_version
                                         and x.sequence = c.sequence and x.component = c.component and x.charge_code = c.charge_code
                                         and not exists (select 1 from {S}.charge_reversals r where r.tenant_id = x.tenant_id and r.charge_id = x.charge_id)), 0)
            """),
        ("I-12", $"""
            select 'obligation ' || o.payment_reference || ' is ' || (case o.status when 1 then 'Active' else 'Settled' end) || ' with outstanding = ' ||
                   exists (select 1 from {S}.instalment_components c join {S}.plan_versions p on p.tenant_id = c.tenant_id and p.obligation_id = c.obligation_id and p.version = c.plan_version
                           where c.obligation_id = o.obligation_id and p.status = 1 and c.due - c.allocated - c.waived > 0)
            from {S}.obligations o
            where o.status in (1, 2) and (o.status = 2) = exists (
              select 1 from {S}.instalment_components c join {S}.plan_versions p on p.tenant_id = c.tenant_id and p.obligation_id = c.obligation_id and p.version = c.plan_version
              where c.obligation_id = o.obligation_id and p.status = 1 and c.due - c.allocated - c.waived > 0)
            """),
        ("I-22", $"""
            select 'unidentified funds of payment ' || l.payment_id || ' have no open exception'
            from {S}.funds_lots l
            where l.pool_source is not null and l.remaining > 0 and not exists (
              select 1 from {S}.payment_exceptions e where e.tenant_id = l.tenant_id and e.payment_id = l.payment_id
              and not exists (select 1 from {S}.payment_exception_resolutions r where r.tenant_id = e.tenant_id and r.exception_id = e.exception_id))
            """),
        ("I-10", $"""
            with {Balances},
            requested as (select coalesce(account_id::text, 'unidentified:' || unidentified_source) as holder, currency, sum(amount) as amount
                          from {S}.refunds where status = {(short)RefundStatus.Requested} group by 1, 2)
            select 'refund payable ' || r.holder || ' is ' || coalesce(-b.balance, 0) || ' but pending refunds total ' || r.amount
            from requested r left join balances b on b.ledger_account = 'refund-payable:' || r.holder and b.currency = r.currency
            where r.amount <> coalesce(-b.balance, 0)
            """),
        ("I-10", $"""
            with {Balances},
            received as (select source, currency, sum(amount) as amount from {S}.payments group by 1, 2),
            reversed as (select p.source, r.currency, sum(r.amount) as amount from {S}.payment_reversals r join {S}.payments p on p.tenant_id = r.tenant_id and p.payment_id = r.payment_id group by 1, 2),
            refunded as (select e->>'source' as source, r.currency, sum((e->>'amount')::numeric) as amount
                         from {S}.refunds r, jsonb_array_elements(r.portions) e where r.status = {(short)RefundStatus.Confirmed} group by 1, 2)
            select 'clearing ' || x.source || ' ' || x.currency || ' is ' || coalesce(b.balance, 0) || ' but payments less reversals and refunds total '
                   || (x.amount - coalesce(v.amount, 0) - coalesce(f.amount, 0))
            from received x
            left join reversed v on v.source = x.source and v.currency = x.currency
            left join refunded f on f.source = x.source and f.currency = x.currency
            left join balances b on b.ledger_account = 'clearing:' || x.source and b.currency = x.currency
            where x.amount - coalesce(v.amount, 0) - coalesce(f.amount, 0) <> coalesce(b.balance, 0)
            """),
        ("I-17", $"""
            with {Balances}, {Active},
            currencies as (select distinct currency from {S}.payments),
            figures as (
              select c.currency,
                     coalesce((select sum(balance) from balances b where b.currency = c.currency and b.ledger_account like 'clearing:%'), 0) as clearing,
                     coalesce((select sum(remaining) from {S}.funds_lots l where l.currency = c.currency), 0) as held,
                     coalesce((select sum(active) from active a where a.currency = c.currency), 0) as applied,
                     coalesce((select sum(amount) from {S}.refunds r where r.currency = c.currency and r.status = {(short)RefundStatus.Requested}), 0) as payable,
                     coalesce((select sum(deficit) from {S}.funds_lots l where l.currency = c.currency), 0) as deficit
              from currencies c)
            select 'clearing ' || currency || ' is ' || clearing || '; held ' || held || ' + applied ' || applied || ' + refund payable ' || payable || ' - reversal receivable ' || deficit
            from figures where clearing <> held + applied + payable - deficit
            """),
    ];

    public static async Task<VerificationResult> VerifyAsync(Session session, CancellationToken ct)
    {
        var violations = new List<string>();
        foreach (var (invariant, sql) in s_checks)
        {
            var command = new NpgsqlCommand(sql, session.Connection, session.Transaction);
            await using var commandScope = command.ConfigureAwait(false);
            var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using var readerScope = reader.ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                violations.Add($"{invariant}: {reader.GetString(0)}.");
            }
        }

        async Task<int> CountAsync(string table)
        {
            var command = new NpgsqlCommand($"select count(*) from {S}.{table}", session.Connection, session.Transaction);
            await using var commandScope = command.ConfigureAwait(false);
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        }

        return new VerificationResult(violations, await CountAsync("journal_transactions").ConfigureAwait(false),
            await CountAsync("payments").ConfigureAwait(false), await CountAsync("accounts").ConfigureAwait(false));
    }
}
