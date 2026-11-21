using System.Collections.Immutable;
using InstalmentFlow.Domain;
using InstalmentFlow.EntityFrameworkCore.Schema;
using InstalmentFlow.Ledger;
using InstalmentFlow.Storage;

namespace InstalmentFlow.EntityFrameworkCore.Persistence;

/// <summary>The projection rows of one customer account aggregate.</summary>
internal sealed record AccountRows(
    AccountRow Account,
    List<ObligationRow> Obligations,
    List<OriginatedComponentRow> Originated,
    List<PlanVersionRow> Plans,
    List<InstalmentRow> Instalments,
    List<InstalmentComponentRow> Components,
    List<FundsLotRow> Lots);

/// <summary>Converts between the domain model and rows. Pure.</summary>
internal static class Mapping
{
    public static Money Money(decimal amount, string currency) => new(amount, Currency.FromCode(currency));

    public static (short Type, string Code) Key(ComponentKey key) => ((short)key.Type, key.ChargeCode);

    public static ComponentKey Key(short type, string code) => new((ComponentType)type, code.Length == 0 ? null : code);

    // ----- Account aggregate -----

    public static AccountRows ToRows(CustomerAccount account)
    {
        var tenant = account.TenantId.Value;
        var rows = new AccountRows(
            new AccountRow { TenantId = tenant, AccountId = account.Id.Value, ExternalId = account.ExternalId, Version = account.Version, OpenedAt = account.OpenedAt },
            [], [], [], [], [], []);
        foreach (var o in account.Obligations)
        {
            rows.Obligations.Add(new ObligationRow
            {
                TenantId = tenant, ObligationId = o.Id.Value, AccountId = account.Id.Value, ExternalId = o.ExternalId, PaymentReference = o.PaymentReference,
                Currency = o.Currency.Code, Status = (short)o.Status, Description = o.Description, OpenedAt = o.OpenedAt,
            });
            rows.Originated.AddRange(o.Originated.Select(c => new OriginatedComponentRow
            {
                TenantId = tenant, ObligationId = o.Id.Value, Component = (short)c.Key.Type, ChargeCode = c.Key.ChargeCode, Amount = c.Amount.Amount,
            }));
            foreach (var p in o.Plans)
            {
                rows.Plans.Add(new PlanVersionRow
                {
                    TenantId = tenant, ObligationId = o.Id.Value, Version = p.Version, Status = (short)p.Status, GraceDays = p.Grace.Days, Generator = p.Generator,
                    CreatedAt = p.CreatedAt, EndedAt = p.EndedAt, EndReason = p.EndReason,
                });
                foreach (var i in p.Instalments)
                {
                    rows.Instalments.Add(new InstalmentRow
                    {
                        TenantId = tenant, ObligationId = o.Id.Value, PlanVersion = p.Version, Sequence = i.Sequence, DueDate = i.DueDate, SettledAt = i.SettledAt,
                    });
                    rows.Components.AddRange(i.Components.Select(c => new InstalmentComponentRow
                    {
                        TenantId = tenant, ObligationId = o.Id.Value, PlanVersion = p.Version, Sequence = i.Sequence, Component = (short)c.Key.Type,
                        ChargeCode = c.Key.ChargeCode, Currency = c.Due.Currency.Code, Due = c.Due.Amount, Allocated = c.Allocated.Amount,
                        Waived = c.Waived.Amount, Charged = c.Charged.Amount,
                    }));
                }
            }
        }

        rows.Lots.AddRange(account.Lots.Select(l => ToRow(tenant, l, account.Id, null)));
        return rows;
    }

    public static FundsLotRow ToRow(string tenant, FundsLot lot, AccountId? account, string? pool) => new()
    {
        TenantId = tenant,
        PaymentId = lot.PaymentId.Value,
        AccountId = account?.Value,
        PoolSource = pool,
        Currency = lot.Original.Currency.Code,
        Original = lot.Original.Amount,
        Remaining = lot.Remaining.Amount,
        Reversed = lot.Reversed.Amount,
        Refunded = lot.Refunded.Amount,
        Deficit = lot.Deficit.Amount,
        Directive = lot.Directive?.Value,
        ValueAt = lot.ValueAt,
    };

    public static FundsLot ToLot(FundsLotRow row) => new(
        new PaymentId(row.PaymentId),
        Money(row.Original, row.Currency),
        Money(row.Remaining, row.Currency),
        row.Directive is { } d ? new ObligationId(d) : null,
        row.ValueAt,
        Money(row.Reversed, row.Currency),
        Money(row.Refunded, row.Currency),
        Money(row.Deficit, row.Currency));

    public static CustomerAccount ToAccount(AccountRows rows)
    {
        var obligations = rows.Obligations
            .OrderBy(o => o.OpenedAt).ThenBy(o => o.ObligationId)
            .Select(o =>
            {
                var currency = Currency.FromCode(o.Currency);
                var plans = rows.Plans.Where(p => p.ObligationId == o.ObligationId).OrderBy(p => p.Version).Select(p => new PlanVersion(
                    p.Version,
                    (PlanVersionStatus)p.Status,
                    GracePeriod.OfDays(p.GraceDays),
                    p.Generator,
                    [.. rows.Instalments.Where(i => i.ObligationId == o.ObligationId && i.PlanVersion == p.Version).OrderBy(i => i.Sequence).Select(i => new Instalment(
                        i.Sequence,
                        i.DueDate,
                        [.. rows.Components.Where(c => c.ObligationId == o.ObligationId && c.PlanVersion == p.Version && c.Sequence == i.Sequence)
                            .Select(c => new InstalmentComponent(Key(c.Component, c.ChargeCode), new Money(c.Due, currency), new Money(c.Allocated, currency),
                                new Money(c.Waived, currency), new Money(c.Charged, currency)))
                            .OrderBy(c => c.Key)],
                        i.SettledAt))],
                    p.CreatedAt,
                    p.EndedAt,
                    p.EndReason)).ToImmutableArray();
                return new Obligation(
                    new ObligationId(o.ObligationId), new AccountId(o.AccountId), o.ExternalId, o.PaymentReference, currency, (ObligationStatus)o.Status,
                    [.. rows.Originated.Where(c => c.ObligationId == o.ObligationId).Select(c => new Component(Key(c.Component, c.ChargeCode), new Money(c.Amount, currency))).OrderBy(c => c.Key)],
                    plans, o.Description, o.OpenedAt);
            })
            .ToImmutableArray();
        var account = rows.Account;
        return new CustomerAccount(account.TenantId, new AccountId(account.AccountId), account.ExternalId, account.Version, account.OpenedAt,
            obligations, [.. rows.Lots.OrderBy(l => l.Ordinal).Select(ToLot)]);
    }

    // ----- Facts -----

    public static PaymentRow ToRow(string tenant, PaymentRecord p) => new()
    {
        TenantId = tenant, PaymentId = p.Id.Value, Source = p.Source.Source, ExternalRef = p.Source.Value, Amount = p.Amount.Amount, Currency = p.Amount.Currency.Code,
        ValueAt = p.ValueAt, RecordedAt = p.RecordedAt, TargetKind = (short)p.TargetKind, RawReference = p.RawReference, AccountId = p.AccountId?.Value,
        IdentifiedObligation = p.IdentifiedObligation?.Value, ExceptionId = p.ExceptionId?.Value, JournalTxId = p.JournalTxId.Value, Metadata = Json.Serialize(p.Metadata),
    };

    public static PaymentRecord ToPayment(PaymentRow r) => new(
        new PaymentId(r.PaymentId), new ExternalReference(r.Source, r.ExternalRef), Money(r.Amount, r.Currency), r.ValueAt, r.RecordedAt,
        (PaymentTargetKind)r.TargetKind, r.RawReference, r.AccountId is { } a ? new AccountId(a) : null,
        r.IdentifiedObligation is { } o ? new ObligationId(o) : null, r.ExceptionId is { } e ? new PaymentExceptionId(e) : null, new JournalTxId(r.JournalTxId),
        Json.Deserialize<Dictionary<string, string>>(r.Metadata).ToImmutableSortedDictionary(StringComparer.Ordinal));

    public static AssignmentRow ToRow(string tenant, PaymentAssignment a) => new()
    {
        TenantId = tenant, PaymentId = a.PaymentId.Value, AccountId = a.AccountId.Value, Amount = a.Amount.Amount, Currency = a.Amount.Currency.Code,
        JournalTxId = a.JournalTxId.Value, At = a.At, ExceptionId = a.ExceptionId.Value,
    };

    public static PaymentAssignment ToAssignment(AssignmentRow r) => new(
        new PaymentId(r.PaymentId), new AccountId(r.AccountId), Money(r.Amount, r.Currency), new JournalTxId(r.JournalTxId), r.At, new PaymentExceptionId(r.ExceptionId));

    public static AllocationRow ToRow(string tenant, AllocationRecord a, int position) => new()
    {
        TenantId = tenant, AllocationId = a.Id.Value, PaymentId = a.PaymentId.Value, AccountId = a.AccountId.Value, ObligationId = a.ObligationId.Value,
        PlanVersion = a.PlanVersion, Sequence = a.InstalmentSequence, DueDate = a.DueDate, Component = (short)a.Component.Type, ChargeCode = a.Component.ChargeCode,
        Amount = a.Amount.Amount, Currency = a.Amount.Currency.Code, StrategyName = a.Strategy.Name, StrategyVersion = a.Strategy.Version,
        PolicyVersion = a.PolicyVersion, JournalTxId = a.JournalTxId.Value, CreatedAt = a.CreatedAt, Position = position,
    };

    public static AllocationRecord ToAllocation(AllocationRow r) => new(
        new AllocationId(r.AllocationId), new PaymentId(r.PaymentId), new AccountId(r.AccountId), new ObligationId(r.ObligationId), r.PlanVersion, r.Sequence,
        r.DueDate, Key(r.Component, r.ChargeCode), Money(r.Amount, r.Currency), new StrategyRef(r.StrategyName, r.StrategyVersion), r.PolicyVersion,
        new JournalTxId(r.JournalTxId), r.CreatedAt);

    public static AllocationReversalRow ToRow(string tenant, AllocationReversalRecord u, int position) => new()
    {
        TenantId = tenant, Id = u.Id, AllocationId = u.AllocationId.Value, PaymentId = u.PaymentId.Value, AccountId = u.AccountId.Value,
        ObligationId = u.ObligationId.Value, PlanVersion = u.PlanVersion, Sequence = u.InstalmentSequence, DueDate = u.DueDate, Component = (short)u.Component.Type,
        ChargeCode = u.Component.ChargeCode, Amount = u.Amount.Amount, Currency = u.Amount.Currency.Code, Cause = (short)u.Cause, CauseId = u.CauseId,
        JournalTxId = u.JournalTxId.Value, At = u.At, Position = position,
    };

    public static AllocationReversalRecord ToUnwind(AllocationReversalRow r) => new(
        r.Id, new AllocationId(r.AllocationId), new PaymentId(r.PaymentId), new AccountId(r.AccountId), new ObligationId(r.ObligationId), r.PlanVersion, r.Sequence,
        r.DueDate, Key(r.Component, r.ChargeCode), Money(r.Amount, r.Currency), (UnwindCause)r.Cause, r.CauseId, new JournalTxId(r.JournalTxId), r.At);

    public static PaymentReversalRow ToRow(string tenant, PaymentReversalRecord v, int position) => new()
    {
        TenantId = tenant, ReversalId = v.Id.Value, PaymentId = v.PaymentId.Value, AccountId = v.AccountId?.Value, UnidentifiedSource = v.UnidentifiedSource,
        ReferenceSource = v.Reference?.Source, ReferenceValue = v.Reference?.Value, Amount = v.Amount.Amount, FromFunds = v.FromFunds.Amount,
        Unwound = v.Unwound.Amount, Deficit = v.Deficit.Amount, Currency = v.Amount.Currency.Code, Reason = v.Reason, JournalTxId = v.JournalTxId.Value, At = v.At,
        Position = position,
    };

    public static PaymentReversalRecord ToReversal(PaymentReversalRow r) => new(
        new ReversalId(r.ReversalId), new PaymentId(r.PaymentId), r.AccountId is { } a ? new AccountId(a) : null, r.UnidentifiedSource,
        Reference(r.ReferenceSource, r.ReferenceValue), Money(r.Amount, r.Currency), Money(r.FromFunds, r.Currency), Money(r.Unwound, r.Currency),
        Money(r.Deficit, r.Currency), r.Reason, new JournalTxId(r.JournalTxId), r.At);

    public static PendingReversalRow ToRow(string tenant, PendingReversal p) => new()
    {
        TenantId = tenant, ReversalId = p.Id.Value, PaymentSource = p.PaymentSource.Source, PaymentRef = p.PaymentSource.Value,
        ReferenceSource = p.Reference?.Source, ReferenceValue = p.Reference?.Value, Amount = p.Amount?.Amount, Currency = p.Amount?.Currency.Code,
        Reason = p.Reason, At = p.At,
    };

    public static PendingReversal ToPending(PendingReversalRow r) => new(
        new ReversalId(r.ReversalId), new ExternalReference(r.PaymentSource, r.PaymentRef), Reference(r.ReferenceSource, r.ReferenceValue),
        r.Amount is { } amount ? Money(amount, r.Currency!) : null, r.Reason, r.At);

    public static ChargeRow ToRow(string tenant, ChargeRecord c) => new()
    {
        TenantId = tenant, ChargeId = c.Id.Value, AccountId = c.AccountId.Value, ObligationId = c.ObligationId.Value, PlanVersion = c.PlanVersion,
        Sequence = c.InstalmentSequence, DueDate = c.DueDate, Component = (short)c.Component.Type, ChargeCode = c.Component.ChargeCode, Amount = c.Amount.Amount,
        Currency = c.Amount.Currency.Code, Description = c.Description, JournalTxId = c.JournalTxId.Value, At = c.At,
    };

    public static ChargeRecord ToCharge(ChargeRow r) => new(
        new ChargeId(r.ChargeId), new AccountId(r.AccountId), new ObligationId(r.ObligationId), r.PlanVersion, r.Sequence, r.DueDate,
        Key(r.Component, r.ChargeCode), Money(r.Amount, r.Currency), r.Description, new JournalTxId(r.JournalTxId), r.At);

    public static ChargeReversalRow ToRow(string tenant, ChargeReversalRecord c) => new()
    {
        TenantId = tenant, ChargeId = c.ChargeId.Value, Reason = c.Reason, JournalTxId = c.JournalTxId.Value, At = c.At,
    };

    public static ChargeReversalRecord ToChargeReversal(ChargeReversalRow r) => new(new ChargeId(r.ChargeId), r.Reason, new JournalTxId(r.JournalTxId), r.At);

    public static WaiverRow ToRow(string tenant, WaiverRecord w, int line) => new()
    {
        TenantId = tenant, WaiverId = w.Id.Value, Line = line, AccountId = w.AccountId.Value, ObligationId = w.ObligationId.Value, PlanVersion = w.PlanVersion,
        Sequence = w.InstalmentSequence, DueDate = w.DueDate, Component = (short)w.Component.Type, ChargeCode = w.Component.ChargeCode, Amount = w.Amount.Amount,
        Currency = w.Amount.Currency.Code, Kind = (short)w.Kind, Reason = w.Reason, JournalTxId = w.JournalTxId.Value, At = w.At,
    };

    public static WaiverRecord ToWaiver(WaiverRow r) => new(
        new WaiverId(r.WaiverId), new AccountId(r.AccountId), new ObligationId(r.ObligationId), r.PlanVersion, r.Sequence, r.DueDate,
        Key(r.Component, r.ChargeCode), Money(r.Amount, r.Currency), (WaiverKind)r.Kind, r.Reason, new JournalTxId(r.JournalTxId), r.At);

    public static RefundRow ToRow(string tenant, RefundRecord f) => new()
    {
        TenantId = tenant, RefundId = f.Id.Value, AccountId = f.AccountId?.Value, UnidentifiedSource = f.UnidentifiedSource, Amount = f.Amount.Amount,
        Currency = f.Amount.Currency.Code, Portions = Json.Serialize(f.Portions.Select(p => new StoredPortion(p.PaymentId.Value, p.Source, p.Amount.Amount)).ToList()),
        Status = (short)f.Status, Reason = f.Reason, RequestedBy = f.RequestedBy, RequestedAt = f.RequestedAt, ExceptionId = f.ExceptionId?.Value,
        CompletedAt = f.CompletedAt, ProviderReferenceSource = f.ProviderReference?.Source, ProviderReferenceValue = f.ProviderReference?.Value,
        FailureReason = f.FailureReason,
    };

    public static RefundRecord ToRefund(RefundRow r) => new(
        new RefundId(r.RefundId), r.AccountId is { } a ? new AccountId(a) : null, r.UnidentifiedSource, Money(r.Amount, r.Currency),
        [.. Json.Deserialize<List<StoredPortion>>(r.Portions).Select(p => new RefundPortion(new PaymentId(p.PaymentId), p.Source, Money(p.Amount, r.Currency)))],
        (RefundStatus)r.Status, r.Reason, r.RequestedBy, r.RequestedAt, r.ExceptionId is { } e ? new PaymentExceptionId(e) : null, r.CompletedAt,
        Reference(r.ProviderReferenceSource, r.ProviderReferenceValue), r.FailureReason);

    /// <summary>A refund portion as stored in jsonb; flat, so it can be searched with <c>@&gt;</c>.</summary>
    public sealed record StoredPortion(Guid PaymentId, string Source, decimal Amount);

    public static PaymentExceptionRow ToRow(string tenant, PaymentExceptionRecord e) => new()
    {
        TenantId = tenant, ExceptionId = e.Id.Value, PaymentId = e.PaymentId.Value, Reason = (short)e.Reason, Detail = e.Detail, RawReference = e.RawReference,
        Candidates = Json.Serialize(e.Candidates.ToList()), OpenedAt = e.OpenedAt,
    };

    public static ExceptionResolutionRow ToResolutionRow(string tenant, PaymentExceptionId id, ExceptionResolution r) => new()
    {
        TenantId = tenant, ExceptionId = id.Value, Kind = (short)r.Kind, ResolvedBy = r.ResolvedBy, Note = r.Note, At = r.At, JournalTxId = r.JournalTxId?.Value,
    };

    public static PaymentExceptionRecord ToException(PaymentExceptionRow r, ExceptionResolutionRow? resolution) => new(
        new PaymentExceptionId(r.ExceptionId), new PaymentId(r.PaymentId), (PaymentExceptionReason)r.Reason, r.Detail, r.RawReference,
        [.. Json.Deserialize<List<CandidateView>>(r.Candidates)], r.OpenedAt,
        resolution is null ? null : new ExceptionResolution((ExceptionResolutionKind)resolution.Kind, resolution.ResolvedBy, resolution.Note, resolution.At,
            resolution.JournalTxId is { } j ? new JournalTxId(j) : null));

    public static (JournalTransactionRow Transaction, List<LedgerEntryRow> Lines) ToRows(JournalTransaction tx) =>
        (new JournalTransactionRow
        {
            TenantId = tx.Tenant.Value, JournalTxId = tx.Id.Value, Kind = (short)tx.Kind, AccountId = tx.AccountId?.Value, IdempotencyKey = tx.IdempotencyKey,
            EffectiveDate = tx.EffectiveDate, RecordedAt = tx.RecordedAt,
        },
        [.. tx.Lines.Select((l, i) => new LedgerEntryRow
        {
            TenantId = tx.Tenant.Value, JournalTxId = tx.Id.Value, Line = (short)i, LedgerAccount = l.Account, Currency = l.Currency.Code, Debit = l.Debit,
            Credit = l.Credit, SourceType = (short)l.SourceType, SourceId = l.SourceId,
        })]);

    public static JournalTransaction ToJournal(JournalTransactionRow t, IEnumerable<LedgerEntryRow> lines) => new(
        new JournalTxId(t.JournalTxId), t.TenantId, (JournalKind)t.Kind, t.AccountId is { } a ? new AccountId(a) : null, t.IdempotencyKey, t.EffectiveDate,
        t.RecordedAt, [.. lines.OrderBy(l => l.Line).Select(l => new LedgerLine(l.LedgerAccount, Currency.FromCode(l.Currency), l.Debit, l.Credit, (LedgerSourceType)l.SourceType, l.SourceId))]);

    public static IdempotencyRow ToRow(string tenant, IdempotencyRecord r) => new()
    {
        TenantId = tenant, Key = r.Key, CommandType = r.CommandType, PayloadHash = [.. r.PayloadHash], ResultType = r.Result.GetType().FullName!,
        Result = Json.Serialize<object>(r.Result), CreatedAt = r.CreatedAt,
    };

    public static IdempotencyRecord ToIdempotency(IdempotencyRow r) => new(
        r.Key, r.CommandType, [.. r.PayloadHash],
        Json.Deserialize(r.Result, typeof(InstalmentFlowFactory).Assembly.GetType(r.ResultType, throwOnError: true)!), r.CreatedAt);

    public static OutboxRow ToRow(DomainEvent e) => new()
    {
        TenantId = e.Tenant.Value, EventId = e.EventId, AccountId = e.AccountId?.Value, Type = e.Type, Payload = Json.Serialize(e.Data), OccurredAt = e.OccurredAt,
        Status = 0, NextAttemptAt = e.OccurredAt,
    };

    public static DomainEvent ToEvent(OutboxRow r) => new(
        r.EventId, r.Type, r.TenantId, r.AccountId is { } a ? new AccountId(a) : null, r.OccurredAt,
        Json.Deserialize<Dictionary<string, string>>(r.Payload).ToImmutableSortedDictionary(StringComparer.Ordinal));

    public static TenantRecord ToTenant(TenantRow t, IEnumerable<PolicyVersionRow> policies) => new(
        t.TenantId, Currency.FromCode(t.BaseCurrency), t.TimeZone,
        [.. policies.OrderBy(p => p.Version).Select(p => new TenantPolicyVersion(p.Version, Json.Deserialize<TenantPolicy>(p.Policy), p.CreatedAt, p.CreatedBy))]);

    public static PolicyVersionRow ToRow(string tenant, TenantPolicyVersion v) => new()
    {
        TenantId = tenant, Version = v.Version, Policy = Json.Serialize(v.Policy), CreatedAt = v.CreatedAt, CreatedBy = v.CreatedBy,
    };

    private static ExternalReference? Reference(string? source, string? value) => source is null || value is null ? null : new ExternalReference(source, value);
}
