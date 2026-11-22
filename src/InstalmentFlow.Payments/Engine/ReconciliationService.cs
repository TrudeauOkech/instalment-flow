using InstalmentFlow.Payments.Storage;

namespace InstalmentFlow.Payments.Engine;

/// <summary>
/// Compares a provider statement with the engine (doc 03 §7.3). Lines the engine lacks go through the inbox, so a
/// payment is recorded once however many paths report it. Nothing the statement does not show is ever changed
/// automatically: those become discrepancies for a person.
/// </summary>
internal sealed class ReconciliationService(PaymentsContext context) : IReconciliation
{
    public async Task<ReconciliationReport> RunAsync(ReconciliationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProviderAccount);
        _ = context.Settings.Providers.Adapter(request.Provider);
        var statement = request.Statement ?? context.Settings.Providers.Statement(request.Provider)
            ?? throw new RuleViolationException(PaymentsErrorCodes.CapabilityNotSupported, $"Provider '{request.Provider}' has no statement source; pass one in the request.");
        if (statement.Provider != request.Provider)
        {
            throw new ArgumentException($"The statement is for provider '{statement.Provider}', not '{request.Provider}'.", nameof(request));
        }

        var options = context.Settings.Reconciliation;
        var started = context.Now;
        var to = request.To ?? started;
        var from = request.From
            ?? (await context.Store.LastRunAsync(request.Provider, request.ProviderAccount, cancellationToken).ConfigureAwait(false) is { } last
                ? last.To - options.Overlap
                : to - options.DefaultWindow);
        if (from >= to)
        {
            throw new ArgumentException("The reconciliation window is empty.", nameof(request));
        }

        using var activity = PaymentsTelemetry.Start("reconciliation.run", request.Provider);
        var run = new Run(context, Guid.CreateVersion7(started), request.Provider, request.ProviderAccount);
        await foreach (var line in statement.ReadAsync(request.ProviderAccount, from, to, cancellationToken).ConfigureAwait(false))
        {
            if (line.Provider == request.Provider && line.ProviderAccount == request.ProviderAccount && line.ValueAt >= from && line.ValueAt < to)
            {
                await run.ClassifyAsync(line, cancellationToken).ConfigureAwait(false);
            }
        }

        await run.FindMissingAtProviderAsync(from, to, cancellationToken).ConfigureAwait(false);
        var report = run.Report(from, to);
        foreach (var line in report.Lines.Concat(report.MissingAtProvider))
        {
            PaymentsTelemetry.Reconciled(request.Provider, line.Classification);
        }

        foreach (var discrepancy in report.NewDiscrepancies)
        {
            PaymentsTelemetry.DiscrepancyRaised(request.Provider, discrepancy.Kind);
        }

        activity?.SetTag("instalmentflow.lines", report.Lines.Count);
        activity?.SetTag("instalmentflow.discrepancies", report.NewDiscrepancies.Count);
        await context.Store.AddRunAsync(new ReconciliationRunRecord(
            report.RunId, report.Provider, report.ProviderAccount, from, to, started, context.Now, report.Lines.Count,
            report.Count(ReconciliationClassification.Matched), report.Count(ReconciliationClassification.MissingInEngine),
            report.Count(ReconciliationClassification.MissingAtProvider), report.Count(ReconciliationClassification.AmountMismatch),
            report.Count(ReconciliationClassification.ReversedAtProvider), report.Count(ReconciliationClassification.Unroutable)), cancellationToken).ConfigureAwait(false);
        return report;
    }

    private sealed class Run(PaymentsContext context, Guid id, string provider, string providerAccount)
    {
        private readonly List<ReconciliationLine> _lines = [];
        private readonly List<ReconciliationLine> _missingAtProvider = [];
        private readonly List<DiscrepancyView> _discrepancies = [];
        private readonly HashSet<string> _credits = new(StringComparer.Ordinal);
        private readonly HashSet<TenantId> _tenants = [];
        private readonly Dictionary<PaymentId, Money> _reversedOnStatement = [];

        private string Source => $"{provider}:{providerAccount}";

        public async Task ClassifyAsync(ProviderStatementLine line, CancellationToken cancellationToken)
        {
            var paymentEvent = line.Kind == StatementLineKind.Credit
                ? NormalisedPaymentEvent.PaymentReceived(provider, providerAccount, line.TransactionId, line.Amount, line.ValueAt, line.AccountReference, line.Payer)
                : NormalisedPaymentEvent.PaymentReversed(provider, providerAccount, line.TransactionId,
                    line.ReversedTransactionId ?? throw new ArgumentException($"Reversal line {line.TransactionId} does not name the payment it reverses."),
                    line.Amount, line.ValueAt, "Reversed at the provider (statement).");
            if (line.Kind == StatementLineKind.Credit)
            {
                _credits.Add(line.TransactionId);
            }

            var tenant = await context.Router.RouteAsync(paymentEvent, cancellationToken).ConfigureAwait(false);
            if (tenant is null)
            {
                _lines.Add(new ReconciliationLine(line.TransactionId, ReconciliationClassification.Unroutable, null, null));
                return;
            }

            _tenants.Add(tenant);
            if (line.Kind == StatementLineKind.Credit)
            {
                await CreditAsync(tenant, line, paymentEvent, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ReversalAsync(tenant, line, paymentEvent, cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task FindMissingAtProviderAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
        {
            if (await context.Store.GetMappingAsync(provider, providerAccount, cancellationToken).ConfigureAwait(false) is { } mapping)
            {
                _tenants.Add(mapping.Tenant);
            }

            foreach (var tenant in _tenants)
            {
                var payments = context.Flows.ForTenant(tenant).Queries.ListPaymentsAsync(new PaymentQuery { Source = Source, From = from, To = to }, cancellationToken);
                await foreach (var payment in payments.ConfigureAwait(false))
                {
                    if (!_credits.Contains(payment.Source.Value))
                    {
                        _missingAtProvider.Add(new ReconciliationLine(payment.Source.Value, ReconciliationClassification.MissingAtProvider, tenant, payment.PaymentId));
                        await RaiseAsync(tenant, DiscrepancyKind.MissingAtProvider, payment.Source.Value, null, payment.Amount, payment.PaymentId, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        public ReconciliationReport Report(DateTimeOffset from, DateTimeOffset to) =>
            new(id, provider, providerAccount, from, to, [.. _lines], [.. _missingAtProvider], [.. _discrepancies]);

        private async Task CreditAsync(TenantId tenant, ProviderStatementLine line, NormalisedPaymentEvent paymentEvent, CancellationToken cancellationToken)
        {
            var payment = await FindPaymentAsync(tenant, line.TransactionId, cancellationToken).ConfigureAwait(false);
            if (payment is null)
            {
                await context.Inbox.EnqueueAsync(paymentEvent, InboxOrigin.Statement, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
                _lines.Add(new ReconciliationLine(line.TransactionId, ReconciliationClassification.MissingInEngine, tenant, null));
            }
            else if (payment.Amount != line.Amount)
            {
                _lines.Add(new ReconciliationLine(line.TransactionId, ReconciliationClassification.AmountMismatch, tenant, payment.PaymentId));
                await RaiseAsync(tenant, DiscrepancyKind.AmountMismatch, line.TransactionId, line.Amount, payment.Amount, payment.PaymentId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _lines.Add(new ReconciliationLine(line.TransactionId, ReconciliationClassification.Matched, tenant, payment.PaymentId));
            }
        }

        private async Task ReversalAsync(TenantId tenant, ProviderStatementLine line, NormalisedPaymentEvent paymentEvent, CancellationToken cancellationToken)
        {
            var payment = await FindPaymentAsync(tenant, line.ReversedTransactionId!, cancellationToken).ConfigureAwait(false);
            if (await context.Store.FindInboxAsync(provider, paymentEvent.DedupeKey, cancellationToken).ConfigureAwait(false) is not null)
            {
                _lines.Add(new ReconciliationLine(line.TransactionId, ReconciliationClassification.Matched, tenant, payment?.PaymentId));   // already reported
                return;
            }

            if (payment is not null)
            {
                // Reversals recorded another way (by the host) are matched by total: the engine has reversed at least
                // as much of the payment as the statement lines seen so far.
                var onStatement = _reversedOnStatement.TryGetValue(payment.PaymentId, out var sum) ? sum.Add(line.Amount) : line.Amount;
                _reversedOnStatement[payment.PaymentId] = onStatement;
                if (payment.Reversed.CompareTo(onStatement) >= 0)
                {
                    _lines.Add(new ReconciliationLine(line.TransactionId, ReconciliationClassification.Matched, tenant, payment.PaymentId));
                    return;
                }
            }

            _lines.Add(new ReconciliationLine(line.TransactionId, ReconciliationClassification.ReversedAtProvider, tenant, payment?.PaymentId));
            var policy = context.Settings.ReversalPolicies.TryGetValue(tenant, out var own) ? own : context.Settings.Reconciliation.ReversedAtProvider;
            if (policy == ReversedAtProviderPolicy.AutoReverse)
            {
                await context.Inbox.EnqueueAsync(paymentEvent, InboxOrigin.Statement, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RaiseAsync(tenant, DiscrepancyKind.ReversedAtProvider, line.TransactionId, line.Amount, payment?.Amount, payment?.PaymentId, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<PaymentView?> FindPaymentAsync(TenantId tenant, string transactionId, CancellationToken cancellationToken)
        {
            try
            {
                return await context.Flows.ForTenant(tenant).Queries
                    .GetPaymentAsync(PaymentRef.BySource(new ExternalReference(Source, transactionId)), cancellationToken).ConfigureAwait(false);
            }
            catch (NotFoundException)
            {
                return null;
            }
        }

        private async Task RaiseAsync(TenantId tenant, DiscrepancyKind kind, string transactionId, Money? statementAmount, Money? engineAmount, PaymentId? paymentId, CancellationToken cancellationToken)
        {
            var now = context.Now;
            var discrepancy = new DiscrepancyRecord(tenant, Guid.CreateVersion7(now), id, provider, providerAccount, kind, transactionId, statementAmount, engineAmount, paymentId, now, null, null);
            if (await context.Store.TryAddDiscrepancyAsync(discrepancy, cancellationToken).ConfigureAwait(false))
            {
                _discrepancies.Add(DiscrepancyService.ToView(discrepancy));
            }
        }
    }
}

internal sealed class DiscrepancyService(PaymentsContext context, TenantId tenant) : IDiscrepancies
{
    public async Task<IReadOnlyList<DiscrepancyView>> ListOpenAsync(CancellationToken cancellationToken = default) =>
        [.. (await context.Store.ListDiscrepanciesAsync(tenant, true, cancellationToken).ConfigureAwait(false)).Select(ToView)];

    public async Task<DiscrepancyView> ResolveAsync(Guid discrepancyId, string resolution, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolution);
        await context.Store.ResolveDiscrepancyAsync(tenant, discrepancyId, context.Now, resolution, cancellationToken).ConfigureAwait(false);
        var all = await context.Store.ListDiscrepanciesAsync(tenant, false, cancellationToken).ConfigureAwait(false);
        return ToView(all.SingleOrDefault(d => d.Id == discrepancyId) ?? throw new NotFoundException("Discrepancy", discrepancyId.ToString()));
    }

    internal static DiscrepancyView ToView(DiscrepancyRecord d) => new(
        d.Id, d.RunId, d.Kind, d.Provider, d.ProviderAccount, d.TransactionId, d.StatementAmount, d.EngineAmount, d.PaymentId, d.DetectedAt, d.ResolvedAt, d.Resolution);
}
