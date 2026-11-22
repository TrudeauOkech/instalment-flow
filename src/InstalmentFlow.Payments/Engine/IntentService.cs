using System.Globalization;
using InstalmentFlow.Payments.Storage;

namespace InstalmentFlow.Payments.Engine;

internal sealed class IntentService(PaymentsContext context, TenantId tenant) : IPaymentIntents
{
    private const int UpdateAttempts = 10;

    public async Task<PaymentIntentView> InitiateAsync(InitiateCollectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IdempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProviderAccount);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Payer);
        ArgumentNullException.ThrowIfNull(request.Amount);
        ArgumentNullException.ThrowIfNull(request.Target);
        if (!request.Amount.IsPositive)
        {
            throw new RuleViolationException(ErrorCodes.InvalidAmount, "A collection amount must be positive.");
        }

        var initiator = context.Settings.Providers.Initiator(request.Provider);
        var fingerprint = Fingerprint(request);
        if (await context.Store.FindIntentByKeyAsync(tenant, request.IdempotencyKey, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return Existing(existing, fingerprint);
        }

        var (kind, obligation, account, reference) = await ResolveTargetAsync(request, cancellationToken).ConfigureAwait(false);
        var now = context.Now;
        var options = context.Settings.Intents;
        var intent = new IntentRecord(
            tenant, Guid.CreateVersion7(now), request.IdempotencyKey, fingerprint, request.Provider, request.ProviderAccount, request.Payer,
            request.Amount, kind, obligation, account, reference, request.Description, PaymentIntentStatus.Created,
            null, null, null, null, now, now, now + options.TimeToLive,
            now + options.InitiationTimeout + options.FirstCheck,   // checked even if this process stops before asking the provider
            0, 1);
        if (!await context.Store.TryAddIntentAsync(intent, cancellationToken).ConfigureAwait(false))
        {
            var winner = await context.Store.FindIntentByKeyAsync(tenant, request.IdempotencyKey, cancellationToken).ConfigureAwait(false);
            return Existing(winner!, fingerprint);
        }

        var result = await AskProviderAsync(initiator, intent, cancellationToken).ConfigureAwait(false);
        var after = context.Now;
        var moved = await MoveAsync(context, intent, i => result.Outcome switch
        {
            InitiationOutcome.Accepted => i with { Status = PaymentIntentStatus.Submitted, CorrelationId = result.CorrelationId, NextCheckAt = after + options.FirstCheck },
            InitiationOutcome.Rejected => i with { Status = PaymentIntentStatus.Failed, FailureReason = result.Reason, NextCheckAt = null },
            _ => i with { Status = PaymentIntentStatus.Ambiguous, FailureReason = result.Reason, NextCheckAt = after + options.FirstCheck },
        }, CancellationToken.None).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        return ToView(moved);
    }

    public async Task<PaymentIntentView> GetAsync(Guid intentId, CancellationToken cancellationToken = default) =>
        ToView(await context.Store.GetIntentAsync(tenant, intentId, cancellationToken).ConfigureAwait(false)
               ?? throw new NotFoundException("Payment intent", intentId.ToString()));

    public async Task<PaymentIntentView?> FindAsync(string idempotencyKey, CancellationToken cancellationToken = default) =>
        await context.Store.FindIntentByKeyAsync(tenant, idempotencyKey, cancellationToken).ConfigureAwait(false) is { } found ? ToView(found) : null;

    /// <summary>
    /// Applies <paramref name="change"/> to the latest version of the intent, if the state machine allows the move.
    /// Returns the intent as stored; a change that is no longer allowed (another path got there first) is skipped.
    /// </summary>
    internal static async Task<IntentRecord> MoveAsync(PaymentsContext context, IntentRecord intent, Func<IntentRecord, IntentRecord> change, CancellationToken cancellationToken)
    {
        var current = intent;
        for (var attempt = 0; attempt < UpdateAttempts; attempt++)
        {
            var next = change(current);
            if (next.Status != current.Status && !IntentRecord.CanMove(current.Status, next.Status))
            {
                return current;
            }

            next = next with { UpdatedAt = context.Now };
            if (await context.Store.UpdateIntentAsync(next, current.Version, cancellationToken).ConfigureAwait(false))
            {
                if (next.Status != current.Status)
                {
                    PaymentsTelemetry.IntentChanged(next.Provider, next.Status);
                }

                return next with { Version = current.Version + 1 };
            }

            current = await context.Store.GetIntentAsync(current.Tenant, current.Id, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Intent {intent.Id} disappeared.");
        }

        throw new ConcurrencyConflictException(default, current.Version);
    }

    internal static PaymentIntentView ToView(IntentRecord i) => new(
        i.Id, i.IdempotencyKey, i.Status, i.Provider, i.ProviderAccount, i.Amount, i.TargetKind, i.ObligationId, i.AccountId, i.AccountReference,
        i.CorrelationId, i.TransactionId, i.PaymentId, i.FailureReason, i.CreatedAt, i.UpdatedAt, i.ExpiresAt, i.StatusChecks);

    private async Task<InitiationResult> AskProviderAsync(ICollectionInitiator initiator, IntentRecord intent, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(context.Settings.Intents.InitiationTimeout);
        try
        {
            return await initiator.InitiateAsync(new CollectionRequest
            {
                ProviderAccount = intent.ProviderAccount,
                Payer = intent.Payer,
                Amount = intent.Amount,
                AccountReference = intent.AccountReference,
                Description = intent.Description,
                MerchantReference = intent.Id.ToString("N"),
            }, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return InitiationResult.Unknown("The collection request timed out.");
        }
#pragma warning disable CA1031 // Any failure after the request may have left: the provider might have started the collection.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return InitiationResult.Unknown($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<(PaymentTargetKind Kind, ObligationId? Obligation, AccountId? Account, string Reference)> ResolveTargetAsync(
        InitiateCollectionRequest request, CancellationToken cancellationToken)
    {
        var queries = context.Flows.ForTenant(tenant).Queries;
        switch (request.Target.Kind)
        {
            case PaymentTargetKind.Obligation:
            {
                var obligation = await queries.GetObligationAsync(request.Target.Obligation!, cancellationToken: cancellationToken).ConfigureAwait(false);
                return (PaymentTargetKind.Obligation, obligation.ObligationId, obligation.AccountId, request.AccountReference ?? obligation.PaymentReference);
            }

            case PaymentTargetKind.Customer:
            {
                var account = await queries.GetAccountAsync(request.Target.Account!, cancellationToken: cancellationToken).ConfigureAwait(false);
                return (PaymentTargetKind.Customer, null, account.AccountId, request.AccountReference ?? account.ExternalId);
            }

            case PaymentTargetKind.Reference when !string.IsNullOrWhiteSpace(request.Target.RawReference):
                return (PaymentTargetKind.Reference, null, null, request.AccountReference ?? request.Target.RawReference!);

            default:
                throw new RuleViolationException(PaymentsErrorCodes.InvalidIntentTarget, "A collection needs an obligation, a customer or a payment reference as its target.");
        }
    }

    private static PaymentIntentView Existing(IntentRecord existing, string fingerprint) =>
        existing.Fingerprint == fingerprint
            ? ToView(existing)
            : throw new RuleViolationException(PaymentsErrorCodes.IntentKeyReused, $"Idempotency key '{existing.IdempotencyKey}' was used for a different collection.");

    private static string Fingerprint(InitiateCollectionRequest r) => string.Join(
        '|',
        r.Provider, r.ProviderAccount, r.Payer, r.Amount.Currency.Code, r.Amount.Amount.ToString(CultureInfo.InvariantCulture), r.Target.Kind,
        r.Target.Obligation?.ToString(), r.Target.Account?.ToString(), r.Target.RawReference, r.AccountReference, r.Description);
}

internal sealed class IntentResolverService(PaymentsContext context) : IIntentResolver
{
    private const int Batch = 100;

    public async Task<IntentResolutionReport> ResolveDueAsync(CancellationToken cancellationToken = default)
    {
        int checkedCount = 0, confirmed = 0, failed = 0, expired = 0;
        var seen = new HashSet<Guid>();
        while (true)
        {
            var due = (await context.Store.ListDueIntentsAsync(context.Now, Batch, cancellationToken).ConfigureAwait(false)).Where(d => seen.Add(d.IntentId)).ToList();
            if (due.Count == 0)
            {
                return new IntentResolutionReport(checkedCount, confirmed, failed, expired);
            }

            foreach (var (tenant, intentId) in due)
            {
                var intent = await context.Store.GetIntentAsync(tenant, intentId, cancellationToken).ConfigureAwait(false);
                if (intent is null || intent.IsTerminal)
                {
                    continue;
                }

                var outcome = await ResolveAsync(intent, cancellationToken).ConfigureAwait(false);
                checkedCount += outcome == Outcome.NotQueried ? 0 : 1;
                confirmed += outcome == Outcome.Confirmed ? 1 : 0;
                failed += outcome == Outcome.Failed ? 1 : 0;
                expired += outcome == Outcome.Expired ? 1 : 0;
            }
        }
    }

    private enum Outcome
    {
        NotQueried,
        Pending,
        Confirmed,
        Failed,
        Expired,
    }

    private async Task<Outcome> ResolveAsync(IntentRecord intent, CancellationToken cancellationToken)
    {
        var now = context.Now;
        var options = context.Settings.Intents;
        var query = context.Settings.Providers.StatusQuery(intent.Provider);
        if (query is null)
        {
            // Without status queries an intent waits for a callback or reconciliation until it expires.
            if (now >= intent.ExpiresAt)
            {
                await IntentService.MoveAsync(context, intent, i => i with { Status = PaymentIntentStatus.Expired, NextCheckAt = null }, cancellationToken).ConfigureAwait(false);
                return Outcome.Expired;
            }

            await IntentService.MoveAsync(context, intent, i => i with { NextCheckAt = i.ExpiresAt }, cancellationToken).ConfigureAwait(false);
            return Outcome.NotQueried;
        }

        StatusQueryResult status;
        using var activity = PaymentsTelemetry.Start("provider.status_query", intent.Provider);
        try
        {
            status = await query.QueryAsync(new ProviderTransactionRef(intent.ProviderAccount, intent.CorrelationId, intent.Id.ToString("N"), intent.Amount), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A failed status query tells us nothing; the intent is checked again later.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.GetType().Name);
            status = StatusQueryResult.Pending;
        }

        activity?.SetTag("instalmentflow.status", status.Status.ToString());
        PaymentsTelemetry.StatusQueried(intent.Provider, status.Status.ToString());

        switch (status.Status)
        {
            case ProviderTransactionStatus.Succeeded when status.TransactionId is not null:
            {
                // The payment goes through the inbox like any other, which records it once and confirms the intent.
                var paid = NormalisedPaymentEvent.CollectionSucceeded(
                    intent.Provider, intent.ProviderAccount, intent.CorrelationId, intent.Id.ToString("N"), status.TransactionId, status.Amount ?? intent.Amount,
                    status.ValueAt ?? now, intent.AccountReference);
                await context.Inbox.EnqueueAsync(paid, InboxOrigin.StatusQuery, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
                await IntentService.MoveAsync(context, intent, i => i with { StatusChecks = i.StatusChecks + 1, NextCheckAt = now + options.MaxCheckInterval }, cancellationToken)
                    .ConfigureAwait(false);
                return Outcome.Confirmed;
            }

            case ProviderTransactionStatus.Failed:
                await IntentService.MoveAsync(context, intent, i => i with
                {
                    Status = PaymentIntentStatus.Failed, FailureReason = status.Reason, StatusChecks = i.StatusChecks + 1, NextCheckAt = null,
                }, cancellationToken).ConfigureAwait(false);
                return Outcome.Failed;

            case ProviderTransactionStatus.Ambiguous when now < intent.ExpiresAt:
            {
                var later = now + PaymentsContext.Backoff(options.FirstCheck, options.MaxCheckInterval, intent.StatusChecks + 1);
                await IntentService.MoveAsync(context, intent, i => i with
                {
                    Status = PaymentIntentStatus.Ambiguous, FailureReason = status.Reason ?? "The provider does not know the outcome.", StatusChecks = i.StatusChecks + 1,
                    NextCheckAt = later < i.ExpiresAt ? later : i.ExpiresAt,
                }, cancellationToken).ConfigureAwait(false);
                return Outcome.Pending;
            }

            default:
                if (now >= intent.ExpiresAt)
                {
                    await IntentService.MoveAsync(context, intent, i => i with
                    {
                        Status = PaymentIntentStatus.Expired, StatusChecks = i.StatusChecks + 1, NextCheckAt = null,
                    }, cancellationToken).ConfigureAwait(false);
                    return Outcome.Expired;
                }

                var next = now + PaymentsContext.Backoff(options.FirstCheck, options.MaxCheckInterval, intent.StatusChecks + 1);
                await IntentService.MoveAsync(context, intent, i => i with
                {
                    StatusChecks = i.StatusChecks + 1, NextCheckAt = next < i.ExpiresAt ? next : i.ExpiresAt,
                }, cancellationToken).ConfigureAwait(false);
                return Outcome.Pending;
        }
    }
}
