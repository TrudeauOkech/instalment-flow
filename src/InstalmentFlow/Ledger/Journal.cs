using System.Collections.Immutable;

namespace InstalmentFlow.Ledger;

internal readonly record struct JournalTxId(Guid Value)
{
    public override string ToString() => Value.ToString();

    public static JournalTxId New(TimeProvider time) => new(Guid.CreateVersion7(time.GetUtcNow()));
}

/// <summary>What kind of business event a journal transaction records. Persisted; never renumber.</summary>
internal enum JournalKind
{
    ObligationOpened = 1,
    PaymentReceived = 2,
    PaymentUnidentified = 3,
    UnidentifiedAssigned = 4,
}

/// <summary>The kind of fact a ledger line points back to (invariant I-15). Persisted; never renumber.</summary>
internal enum LedgerSourceType
{
    Obligation = 1,
    Payment = 2,
    Assignment = 3,
}

/// <summary>One debit or credit on one ledger account. Exactly one of debit and credit is positive.</summary>
internal sealed record LedgerLine(string Account, Currency Currency, decimal Debit, decimal Credit, LedgerSourceType SourceType, Guid SourceId);

/// <summary>A balanced, append-only set of ledger lines produced by one command (ADR-004).</summary>
internal sealed record JournalTransaction(
    JournalTxId Id,
    TenantId Tenant,
    JournalKind Kind,
    AccountId? AccountId,
    string IdempotencyKey,
    DateOnly EffectiveDate,
    DateTimeOffset RecordedAt,
    ImmutableArray<LedgerLine> Lines);

/// <summary>
/// The subledger's chart of accounts (doc 02 §5). Codes are hierarchical strings; the host's general ledger maps
/// them to its own accounts.
/// </summary>
internal static class LedgerAccounts
{
    /// <summary>Funds held at the provider or bank, per provider account. Debit balance.</summary>
    public static string Clearing(string source) => $"clearing:{source}";

    /// <summary>Receipts not yet attributed to a customer, per provider account. Credit balance.</summary>
    public static string Unidentified(string source) => $"unidentified:{source}";

    /// <summary>A customer's unapplied funds in one currency. Credit balance.</summary>
    public static string Unapplied(AccountId account, Currency currency) => $"acct:{account}:unapplied:{currency.Code}";

    /// <summary>What is owed on one component of an obligation. Debit balance.</summary>
    public static string Receivable(ObligationId obligation, ComponentKey key) => $"obl:{obligation}:{Code(key)}";

    /// <summary>The counterpart of amounts owed when an obligation is opened or a charge posted.</summary>
    public static string Origination(ComponentKey key) => $"origination:{Code(key)}";

    private static string Code(ComponentKey key)
    {
        var type = key.Type.ToString().ToLowerInvariant();
        return key.ChargeCode.Length == 0 ? type : $"{type}.{key.ChargeCode}";
    }
}

/// <summary>Builds a journal transaction and enforces invariant I-1 (balanced per currency) before it exists.</summary>
internal sealed class JournalBuilder(JournalKind kind)
{
    private readonly List<LedgerLine> _lines = [];

    public JournalBuilder Debit(string account, Money amount, LedgerSourceType sourceType, Guid sourceId) =>
        Add(account, amount, debit: true, sourceType, sourceId);

    public JournalBuilder Credit(string account, Money amount, LedgerSourceType sourceType, Guid sourceId) =>
        Add(account, amount, debit: false, sourceType, sourceId);

    public JournalTransaction Build(TenantId tenant, AccountId? accountId, string idempotencyKey, DateOnly effectiveDate, DateTimeOffset recordedAt, TimeProvider time)
    {
        if (_lines.Count < 2)
        {
            throw new InvalidOperationException("A journal transaction needs at least two lines.");
        }

        foreach (var byCurrency in _lines.GroupBy(l => l.Currency))
        {
            var debits = byCurrency.Sum(l => l.Debit);
            var credits = byCurrency.Sum(l => l.Credit);
            if (debits != credits)
            {
                throw new InvalidOperationException($"Unbalanced journal transaction in {byCurrency.Key}: debits {debits}, credits {credits} (I-1).");
            }
        }

        return new JournalTransaction(JournalTxId.New(time), tenant, kind, accountId, idempotencyKey, effectiveDate, recordedAt, [.. _lines]);
    }

    private JournalBuilder Add(string account, Money amount, bool debit, LedgerSourceType sourceType, Guid sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        if (!amount.IsPositive)
        {
            throw new InvalidOperationException($"Ledger amounts must be positive; got {amount} on {account}.");
        }

        _lines.Add(new LedgerLine(account, amount.Currency, debit ? amount.Amount : 0m, debit ? 0m : amount.Amount, sourceType, sourceId));
        return this;
    }
}
