using InstalmentFlow.Payments.Storage;

namespace InstalmentFlow.Payments.Engine;

internal sealed class InboxService(PaymentsContext context) : IProviderInbox
{
    private const int MetadataLimit = 32;

    public async Task<InboxReceipt> ReceiveAsync(ProviderCallback callback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var adapter = context.Settings.Providers.Adapter(callback.Provider);
        var parsed = await adapter.ParseCallbackAsync(callback, cancellationToken).ConfigureAwait(false);
        if (!parsed.Accepted)
        {
            return new InboxReceipt(InboxReceiptOutcome.Rejected, 0, 0, parsed.RejectionReason);
        }

        var stored = 0;
        foreach (var paymentEvent in parsed.Events)
        {
            if (paymentEvent.Provider != callback.Provider)
            {
                throw new InvalidOperationException($"Adapter '{adapter.Provider}' produced an event for provider '{paymentEvent.Provider}'.");
            }

            if (await EnqueueAsync(paymentEvent, InboxOrigin.Callback, callback.Body, cancellationToken).ConfigureAwait(false))
            {
                stored++;
            }
        }

        var duplicates = parsed.Events.Count - stored;
        return new InboxReceipt(stored == 0 && duplicates > 0 ? InboxReceiptOutcome.Duplicate : InboxReceiptOutcome.Accepted, stored, duplicates, null);
    }

    /// <summary>Stores an event once per deduplication key; false if it was already stored.</summary>
    internal async Task<bool> EnqueueAsync(NormalisedPaymentEvent paymentEvent, InboxOrigin origin, ReadOnlyMemory<byte> raw, CancellationToken cancellationToken)
    {
        var payload = context.Settings.Protector is { } protector ? protector.Protect(raw.Span) : raw.ToArray();
        var now = context.Now;
        var entry = new InboxEntry(
            Guid.CreateVersion7(now), paymentEvent.Provider, paymentEvent.DedupeKey, origin, paymentEvent, payload, now,
            InboxStatus.Pending, 0, now, null, null, null, null, null);
        return await context.Store.TryAddInboxAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    public async Task<InboxProcessingReport> ProcessPendingAsync(CancellationToken cancellationToken = default)
    {
        var options = context.Settings.Inbox;
        int processed = 0, retrying = 0, failed = 0;
        var seen = new HashSet<Guid>();
        while (true)
        {
            var now = context.Now;
            var batch = await context.Store.ClaimInboxAsync(now, now + options.Lease, options.BatchSize, cancellationToken).ConfigureAwait(false);
            var fresh = batch.Where(e => seen.Add(e.Id)).ToList();
            if (fresh.Count == 0)
            {
                foreach (var entry in batch)
                {
                    await context.Store.UpdateInboxAsync(entry, cancellationToken).ConfigureAwait(false);   // release the lease
                }

                return new InboxProcessingReport(processed, retrying, failed);
            }

            foreach (var entry in fresh)
            {
                var outcome = await ProcessAsync(entry, cancellationToken).ConfigureAwait(false);
                await context.Store.UpdateInboxAsync(outcome, cancellationToken).ConfigureAwait(false);
                switch (outcome.Status)
                {
                    case InboxStatus.Processed: processed++; break;
                    case InboxStatus.Failed: failed++; break;
                    default: retrying++; break;
                }
            }
        }
    }

    public async Task<IReadOnlyList<InboxEntryView>> ListAsync(InboxStatus status, int max = 100, CancellationToken cancellationToken = default) =>
        [.. (await context.Store.ListInboxAsync(status, max, cancellationToken).ConfigureAwait(false)).Select(ToView)];

    public async Task<bool> RequeueAsync(Guid entryId, CancellationToken cancellationToken = default)
    {
        var entry = await context.Store.GetInboxAsync(entryId, cancellationToken).ConfigureAwait(false);
        if (entry is not { Status: InboxStatus.Failed })
        {
            return false;
        }

        await context.Store.UpdateInboxAsync(entry with { Status = InboxStatus.Pending, Attempts = 0, NextAttemptAt = context.Now, Error = null }, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<InboxEntry> ProcessAsync(InboxEntry entry, CancellationToken cancellationToken)
    {
        var attempt = entry.Attempts + 1;
        try
        {
            var tenant = await context.Router.RouteAsync(entry.Event, cancellationToken).ConfigureAwait(false);
            if (tenant is null)
            {
                return Retry(entry, attempt, $"No tenant is mapped to provider account {entry.Event.Source}.");
            }

            var paymentId = await HandleAsync(tenant, entry, cancellationToken).ConfigureAwait(false);
            return entry with
            {
                Status = InboxStatus.Processed, Attempts = attempt, ProcessedAt = context.Now, Error = null, Tenant = tenant, PaymentId = paymentId,
            };
        }
        catch (RuleViolationException ex)
        {
            // A rule refused the event; retrying gives the same answer. It waits for a person.
            return entry with { Status = InboxStatus.Failed, Attempts = attempt, Error = $"{ex.ErrorCode}: {ex.Message}" };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Anything else (a database outage, a conflict that outlasted its retries) is retried later.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return Retry(entry, attempt, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private InboxEntry Retry(InboxEntry entry, int attempt, string error)
    {
        var options = context.Settings.Inbox;
        return attempt >= options.MaxAttempts
            ? entry with { Status = InboxStatus.Failed, Attempts = attempt, Error = error }
            : entry with { Attempts = attempt, Error = error, NextAttemptAt = context.Now + PaymentsContext.Backoff(options.RetryDelay, options.MaxRetryDelay, attempt - 1) };
    }

    private async Task<PaymentId?> HandleAsync(TenantId tenant, InboxEntry entry, CancellationToken cancellationToken)
    {
        var paymentEvent = entry.Event;
        var flow = context.Flows.ForTenant(tenant);
        var key = $"inbox:{entry.Provider}:{entry.DedupeKey}";
        switch (paymentEvent.Kind)
        {
            case PaymentEventKind.PaymentReceived:
            {
                var target = string.IsNullOrWhiteSpace(paymentEvent.AccountReference) ? PaymentTarget.Unknown : PaymentTarget.ForReference(paymentEvent.AccountReference);
                return (await flow.Payments.RecordAsync(Record(paymentEvent, key, target), cancellationToken).ConfigureAwait(false)).PaymentId;
            }

            case PaymentEventKind.CollectionSucceeded:
            {
                var intent = await FindIntentAsync(tenant, paymentEvent, cancellationToken).ConfigureAwait(false);
                var target = intent?.Target
                    ?? (string.IsNullOrWhiteSpace(paymentEvent.AccountReference) ? PaymentTarget.Unknown : PaymentTarget.ForReference(paymentEvent.AccountReference));
                var recorded = await flow.Payments.RecordAsync(Record(paymentEvent, key, target), cancellationToken).ConfigureAwait(false);
                if (intent is not null)
                {
                    await IntentService.MoveAsync(context, intent, i => i with
                    {
                        Status = PaymentIntentStatus.Confirmed, CorrelationId = i.CorrelationId ?? paymentEvent.CorrelationId, TransactionId = paymentEvent.TransactionId,
                        PaymentId = recorded.PaymentId, NextCheckAt = null, FailureReason = null,
                    }, cancellationToken).ConfigureAwait(false);
                }

                return recorded.PaymentId;
            }

            case PaymentEventKind.CollectionFailed:
            {
                if (await FindIntentAsync(tenant, paymentEvent, cancellationToken).ConfigureAwait(false) is { } intent)
                {
                    await IntentService.MoveAsync(context, intent, i => i with
                    {
                        Status = PaymentIntentStatus.Failed, CorrelationId = i.CorrelationId ?? paymentEvent.CorrelationId, FailureReason = paymentEvent.Reason, NextCheckAt = null,
                    }, cancellationToken).ConfigureAwait(false);
                }

                return null;
            }

            default:
            {
                var reversal = await flow.Payments.ReverseAsync(new ReversePaymentRequest
                {
                    IdempotencyKey = key,
                    Payment = PaymentRef.BySource(new ExternalReference(paymentEvent.Source, paymentEvent.ReversedTransactionId!)),
                    ReversalReference = new ExternalReference(paymentEvent.Source, paymentEvent.TransactionId!),
                    Amount = paymentEvent.Amount,
                    Reason = paymentEvent.Reason ?? "Reversed at the provider.",
                }, cancellationToken).ConfigureAwait(false);
                return reversal.PaymentId;
            }
        }
    }

    private async Task<IntentRecord?> FindIntentAsync(TenantId tenant, NormalisedPaymentEvent paymentEvent, CancellationToken cancellationToken)
    {
        if (paymentEvent.CorrelationId is { } correlation &&
            await context.Store.FindIntentByCorrelationAsync(tenant, paymentEvent.Provider, correlation, cancellationToken).ConfigureAwait(false) is { } byCorrelation)
        {
            return byCorrelation;
        }

        return Guid.TryParseExact(paymentEvent.MerchantReference, "N", out var intentId) &&
               await context.Store.GetIntentAsync(tenant, intentId, cancellationToken).ConfigureAwait(false) is { } byReference &&
               byReference.Provider == paymentEvent.Provider
            ? byReference
            : null;
    }

    private static RecordPaymentRequest Record(NormalisedPaymentEvent paymentEvent, string key, PaymentTarget target)
    {
        var metadata = new Dictionary<string, string>(paymentEvent.Metadata, StringComparer.Ordinal);
        if (paymentEvent.Payer is { } payer && metadata.Count < MetadataLimit)
        {
            metadata.TryAdd("payer", payer);
        }

        return new RecordPaymentRequest
        {
            IdempotencyKey = key,
            Source = new ExternalReference(paymentEvent.Source, paymentEvent.TransactionId!),
            Amount = paymentEvent.Amount!,
            ValueAt = paymentEvent.ValueAt!.Value,
            Target = target,
            Metadata = metadata,
        };
    }

    private static InboxEntryView ToView(InboxEntry e) =>
        new(e.Id, e.Provider, e.DedupeKey, e.Event, e.ReceivedAt, e.Status, e.Attempts, e.Error, e.Tenant, e.PaymentId);
}
