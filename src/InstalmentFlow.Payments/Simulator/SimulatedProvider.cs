using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace InstalmentFlow.Payments.Simulation;

/// <summary>Faults the simulator injects into one transaction's callbacks or statement (ADR-016).</summary>
[Flags]
public enum SimulatedFaults
{
    /// <summary>Callbacks are delivered once, in order.</summary>
    None = 0,

    /// <summary>The callback is delivered twice.</summary>
    Duplicate = 1,

    /// <summary>The callback is never delivered; the transaction is still on the statement (for a reversal: a statement-only reversal).</summary>
    Drop = 2,

    /// <summary>The callback is held back and delivered after the next callback.</summary>
    Reorder = 4,

    /// <summary>A push request times out for the caller although the provider accepted it and the customer pays.</summary>
    TimeoutThenSuccess = 8,

    /// <summary>The statement shows a different amount than the callback (by <see cref="SimulatedPayment.StatementAmount"/>, or one more major unit).</summary>
    AmountMismatch = 16,
}

/// <summary>How the simulated customer answers a push request.</summary>
public enum SimulatedPushOutcome
{
    /// <summary>The customer pays.</summary>
    Approve = 1,

    /// <summary>The customer declines or cancels; a failure callback follows.</summary>
    Decline = 2,

    /// <summary>The customer never answers: no callback, and the status stays pending.</summary>
    NoResponse = 3,

    /// <summary>The provider refuses the request outright.</summary>
    Reject = 4,
}

/// <summary>A customer-initiated payment, such as a paybill payment.</summary>
public sealed record SimulatedPayment
{
    /// <summary>The merchant account paid into.</summary>
    public required string ProviderAccount { get; init; }

    /// <summary>The amount paid.</summary>
    public required Money Amount { get; init; }

    /// <summary>The account reference the customer typed.</summary>
    public string? AccountReference { get; init; }

    /// <summary>The payer.</summary>
    public string Payer { get; init; } = "2547****0000";

    /// <summary>A receipt to use instead of a generated one.</summary>
    public string? TransactionId { get; init; }

    /// <summary>Faults to inject.</summary>
    public SimulatedFaults Faults { get; init; }

    /// <summary>How long after the payment its callback arrives.</summary>
    public TimeSpan CallbackDelay { get; init; }

    /// <summary>The amount the statement shows, with <see cref="SimulatedFaults.AmountMismatch"/>.</summary>
    public Money? StatementAmount { get; init; }
}

/// <summary>How the simulator answers the next push request.</summary>
public sealed record SimulatedPush
{
    /// <summary>What the customer does.</summary>
    public SimulatedPushOutcome Outcome { get; init; } = SimulatedPushOutcome.Approve;

    /// <summary>Faults to inject.</summary>
    public SimulatedFaults Faults { get; init; }

    /// <summary>How long the customer takes to answer.</summary>
    public TimeSpan AnswerAfter { get; init; }

    /// <summary>How long after the answer its callback arrives.</summary>
    public TimeSpan CallbackDelay { get; init; }

    /// <summary>The amount the customer actually pays; by default the amount requested.</summary>
    public Money? PaidAmount { get; init; }
}

/// <summary>What a simulated transaction records.</summary>
public enum SimulatedTransactionKind
{
    /// <summary>A customer-initiated payment.</summary>
    Payment = 1,

    /// <summary>A push (merchant-initiated) collection.</summary>
    Collection = 2,

    /// <summary>A reversal of an earlier payment or collection.</summary>
    Reversal = 3,
}

/// <summary>A transaction at the simulated provider.</summary>
/// <param name="TransactionId">The provider's receipt (for a collection, once paid).</param>
/// <param name="Kind">What it records.</param>
/// <param name="ProviderAccount">The merchant account.</param>
/// <param name="Amount">The amount in callbacks and status queries.</param>
/// <param name="StatementAmount">The amount on the statement.</param>
/// <param name="ValueAt">When the money moves, or moved.</param>
/// <param name="AccountReference">The account reference.</param>
/// <param name="Payer">The payer.</param>
/// <param name="CorrelationId">The provider's ID for a collection.</param>
/// <param name="MerchantReference">The merchant's reference for a collection.</param>
/// <param name="Outcome">How the customer answers a collection.</param>
/// <param name="ReversedTransactionId">The transaction a reversal reverses.</param>
public sealed record SimulatedTransaction(
    string TransactionId,
    SimulatedTransactionKind Kind,
    string ProviderAccount,
    Money Amount,
    Money StatementAmount,
    DateTimeOffset ValueAt,
    string? AccountReference,
    string? Payer,
    string? CorrelationId,
    string? MerchantReference,
    SimulatedPushOutcome Outcome,
    string? ReversedTransactionId);

/// <summary>
/// A payment provider that runs in memory (ADR-016), the first adapter and the basis of every payments test. It
/// supports paybill-style and push-style flows, status queries and a statement, signs its callbacks with HMAC-SHA256,
/// and injects faults per transaction: duplicates, delays, drops, reordering, timeout-then-success, amount mismatches
/// and statement-only reversals. Callbacks go to the inbox connected with <see cref="ConnectTo"/> when
/// <see cref="DeliverDueAsync"/> runs, so tests control when each one arrives. Register it only explicitly.
/// </summary>
public sealed class SimulatedProvider : IPaymentProviderAdapter, ICollectionInitiator, ITransactionStatusQuery, IStatementSource
{
    /// <summary>The header carrying the callback signature.</summary>
    public const string SignatureHeader = "X-Simulator-Signature";

    private readonly Lock _lock = new();
    private readonly byte[] _secret;
    private readonly TimeProvider _time;
    private readonly List<SimulatedTransaction> _transactions = [];
    private readonly List<Delivery> _deliveries = [];
    private readonly Queue<SimulatedPush> _pushes = new();
    private IProviderInbox? _inbox;
    private long _sequence;
    private int _receipts;

    /// <summary>Creates a simulator.</summary>
    /// <param name="secret">The callback signing secret.</param>
    /// <param name="timeProvider">The clock; use the engine's.</param>
    /// <param name="provider">The provider name. Default <c>sim</c>.</param>
    public SimulatedProvider(string secret, TimeProvider? timeProvider = null, string provider = "sim")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        _secret = Encoding.UTF8.GetBytes(secret);
        _time = timeProvider ?? TimeProvider.System;
        Provider = provider;
    }

    /// <inheritdoc />
    public string Provider { get; }

    /// <inheritdoc />
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.CustomerInitiatedCallbacks | ProviderCapabilities.MerchantInitiatedCollection | ProviderCapabilities.StatusQuery |
        ProviderCapabilities.StatementSource | ProviderCapabilities.AuthenticatedCallbacks;

    /// <summary>Every transaction so far.</summary>
    public IReadOnlyList<SimulatedTransaction> Transactions
    {
        get
        {
            lock (_lock)
            {
                return [.. _transactions];
            }
        }
    }

    /// <summary>How many push requests were made.</summary>
    public int PushRequests { get; private set; }

    /// <summary>Callbacks waiting to be delivered (held ones included).</summary>
    public int PendingCallbacks
    {
        get
        {
            lock (_lock)
            {
                return _deliveries.Count;
            }
        }
    }

    /// <summary>Delivers callbacks to <paramref name="inbox"/>.</summary>
    public SimulatedProvider ConnectTo(IProviderInbox inbox)
    {
        _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
        return this;
    }

    /// <summary>Sets how the simulator answers the next push request; without one, the customer pays at once.</summary>
    public SimulatedProvider NextPush(SimulatedPush push)
    {
        ArgumentNullException.ThrowIfNull(push);
        lock (_lock)
        {
            _pushes.Enqueue(push);
        }

        return this;
    }

    /// <summary>A customer pays into a merchant account (paybill or till).</summary>
    public SimulatedTransaction Pay(SimulatedPayment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            var transaction = new SimulatedTransaction(
                payment.TransactionId ?? NextReceipt(), SimulatedTransactionKind.Payment, payment.ProviderAccount, payment.Amount,
                StatementAmount(payment.Amount, payment.Faults, payment.StatementAmount), now, payment.AccountReference, payment.Payer, null, null,
                SimulatedPushOutcome.Approve, null);
            _transactions.Add(transaction);
            Schedule(Callback("payment", transaction, null), now + payment.CallbackDelay, payment.Faults);
            return transaction;
        }
    }

    /// <summary>The provider reverses an earlier transaction; <paramref name="amount"/> null reverses all of it.</summary>
    public SimulatedTransaction Reverse(string transactionId, Money? amount = null, SimulatedFaults faults = SimulatedFaults.None, TimeSpan callbackDelay = default)
    {
        lock (_lock)
        {
            var original = _transactions.SingleOrDefault(t => t.TransactionId == transactionId && t.Kind != SimulatedTransactionKind.Reversal)
                ?? throw new ArgumentException($"No transaction '{transactionId}'.", nameof(transactionId));
            var now = _time.GetUtcNow();
            var value = amount ?? original.Amount;
            var reversal = new SimulatedTransaction(
                NextReceipt(), SimulatedTransactionKind.Reversal, original.ProviderAccount, value, value, now, null, original.Payer, null, null,
                SimulatedPushOutcome.Approve, original.TransactionId);
            _transactions.Add(reversal);
            Schedule(Callback("reversal", reversal, null), now + callbackDelay, faults);
            return reversal;
        }
    }

    /// <summary>Delivers every callback whose time has come, in order, to the connected inbox. Returns how many were delivered.</summary>
    public Task<int> DeliverDueAsync(CancellationToken cancellationToken = default) => DeliverAsync(all: false, cancellationToken);

    /// <summary>Delivers every waiting callback now, delayed and held ones included.</summary>
    public Task<int> DeliverAllAsync(CancellationToken cancellationToken = default) => DeliverAsync(all: true, cancellationToken);

    /// <inheritdoc />
    public ValueTask<CallbackParseResult> ParseCallbackAsync(ProviderCallback callback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (!callback.Headers.TryGetValue(SignatureHeader, out var signature) || !Verify(callback.Body.Span, signature))
        {
            return ValueTask.FromResult(CallbackParseResult.Reject("The callback signature is missing or wrong."));
        }

        try
        {
            using var json = JsonDocument.Parse(callback.Body);
            var root = json.RootElement;
            string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var account = Text("account") ?? throw new FormatException("account");
            Money Amount() => Money.Of(decimal.Parse(Text("amount")!, NumberStyles.Number, CultureInfo.InvariantCulture), Text("currency")!);
            DateTimeOffset Time() => DateTimeOffset.Parse(Text("time")!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var paymentEvent = Text("type") switch
            {
                "payment" => NormalisedPaymentEvent.PaymentReceived(Provider, account, Text("transactionId")!, Amount(), Time(), Text("reference"), Text("payer")),
                "reversal" => NormalisedPaymentEvent.PaymentReversed(Provider, account, Text("transactionId")!, Text("reversedTransactionId")!, Amount(), Time(), "Reversed by the provider."),
                "collection" when Text("status") == "succeeded" => NormalisedPaymentEvent.CollectionSucceeded(
                    Provider, account, Text("checkoutId"), Text("merchantReference"), Text("transactionId")!, Amount(), Time(), Text("reference"), Text("payer")),
                "collection" => NormalisedPaymentEvent.CollectionFailed(Provider, account, Text("checkoutId"), Text("merchantReference"), Text("reason") ?? "Declined."),
                var other => throw new FormatException($"type '{other}'"),
            };
            return ValueTask.FromResult(CallbackParseResult.Accept(paymentEvent));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or NullReferenceException or InstalmentFlowException)
        {
            return ValueTask.FromResult(CallbackParseResult.Reject($"Malformed callback: {ex.Message}"));
        }
    }

    /// <inheritdoc />
    public Task<InitiationResult> InitiateAsync(CollectionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_lock)
        {
            PushRequests++;
            var push = _pushes.TryDequeue(out var next) ? next : new SimulatedPush();
            if (push.Outcome == SimulatedPushOutcome.Reject)
            {
                return Task.FromResult(InitiationResult.Rejected("The provider refused the request."));
            }

            var correlation = $"ws_CO_{++_sequence:D6}";
            var answeredAt = _time.GetUtcNow() + push.AnswerAfter;
            var paid = push.PaidAmount ?? request.Amount;
            var transaction = new SimulatedTransaction(
                NextReceipt(), SimulatedTransactionKind.Collection, request.ProviderAccount, paid, StatementAmount(paid, push.Faults, null), answeredAt,
                request.AccountReference, request.Payer, correlation, request.MerchantReference, push.Outcome, null);
            _transactions.Add(transaction);
            if (push.Outcome != SimulatedPushOutcome.NoResponse)
            {
                var status = push.Outcome == SimulatedPushOutcome.Approve ? "succeeded" : "failed";
                Schedule(Callback("collection", transaction, status), answeredAt + push.CallbackDelay, push.Faults);
            }

            return push.Faults.HasFlag(SimulatedFaults.TimeoutThenSuccess)
                ? Task.FromException<InitiationResult>(new TimeoutException("The simulated provider did not answer in time."))
                : Task.FromResult(InitiationResult.Accepted(correlation));
        }
    }

    /// <inheritdoc />
    public Task<StatusQueryResult> QueryAsync(ProviderTransactionRef reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        lock (_lock)
        {
            var transaction = _transactions.SingleOrDefault(t => t.Kind == SimulatedTransactionKind.Collection && t.ProviderAccount == reference.ProviderAccount &&
                ((reference.CorrelationId is not null && t.CorrelationId == reference.CorrelationId) || t.MerchantReference == reference.MerchantReference));
            if (transaction is null)
            {
                return Task.FromResult(StatusQueryResult.NotFound);
            }

            return Task.FromResult(transaction.Outcome switch
            {
                _ when transaction.ValueAt > _time.GetUtcNow() || transaction.Outcome == SimulatedPushOutcome.NoResponse => StatusQueryResult.Pending,
                SimulatedPushOutcome.Approve => StatusQueryResult.Succeeded(transaction.TransactionId, transaction.Amount, transaction.ValueAt),
                _ => StatusQueryResult.Failed("The customer declined."),
            });
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ProviderStatementLine> ReadAsync(string providerAccount, DateTimeOffset windowStart, DateTimeOffset windowEnd, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        List<SimulatedTransaction> lines;
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            lines = [.. _transactions.Where(t => t.ProviderAccount == providerAccount && t.ValueAt >= windowStart && t.ValueAt < windowEnd && t.ValueAt <= now &&
                                                 t.Outcome == SimulatedPushOutcome.Approve)];
        }

        foreach (var t in lines)
        {
            yield return new ProviderStatementLine
            {
                Provider = Provider, ProviderAccount = t.ProviderAccount, TransactionId = t.TransactionId,
                Kind = t.Kind == SimulatedTransactionKind.Reversal ? StatementLineKind.Reversal : StatementLineKind.Credit,
                Amount = t.StatementAmount, ValueAt = t.ValueAt, AccountReference = t.AccountReference, Payer = t.Payer, ReversedTransactionId = t.ReversedTransactionId,
            };
        }
    }

    /// <summary>Signs a body as the simulator does, for tests that forge or replay callbacks.</summary>
    public string Sign(ReadOnlySpan<byte> body) => Convert.ToHexString(HMACSHA256.HashData(_secret, body));

    private async Task<int> DeliverAsync(bool all, CancellationToken cancellationToken)
    {
        var inbox = _inbox ?? throw new InvalidOperationException("Connect the simulator to an inbox first (ConnectTo).");
        List<Delivery> due;
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            due = [.. _deliveries.Where(d => all || (!d.Held && d.DueAt <= now)).OrderBy(d => all ? DateTimeOffset.MinValue : d.DueAt).ThenBy(d => d.Order)];
            foreach (var delivery in due)
            {
                _deliveries.Remove(delivery);
            }
        }

        foreach (var delivery in due)
        {
            await inbox.ReceiveAsync(delivery.Callback, cancellationToken).ConfigureAwait(false);
        }

        return due.Count;
    }

    private void Schedule(ProviderCallback callback, DateTimeOffset dueAt, SimulatedFaults faults)
    {
        if (faults.HasFlag(SimulatedFaults.Drop))
        {
            return;
        }

        var held = _deliveries.Where(d => d.Held).OrderBy(d => d.Order).ToList();
        var hold = faults.HasFlag(SimulatedFaults.Reorder);
        _deliveries.Add(new Delivery(++_sequence, dueAt, callback, hold));
        if (faults.HasFlag(SimulatedFaults.Duplicate))
        {
            _deliveries.Add(new Delivery(++_sequence, dueAt, callback, hold));
        }

        foreach (var delivery in held)
        {
            // Held callbacks go out right after this one.
            _deliveries.Remove(delivery);
            _deliveries.Add(delivery with { Held = false, DueAt = delivery.DueAt > dueAt ? delivery.DueAt : dueAt, Order = ++_sequence });
        }
    }

    private ProviderCallback Callback(string type, SimulatedTransaction t, string? status)
    {
        var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("type", type);
            json.WriteString("account", t.ProviderAccount);
            json.WriteString("transactionId", t.TransactionId);
            json.WriteString("amount", t.Amount.Amount.ToString(CultureInfo.InvariantCulture));
            json.WriteString("currency", t.Amount.Currency.Code);
            json.WriteString("time", t.ValueAt.ToString("O", CultureInfo.InvariantCulture));
            Optional(json, "reference", t.AccountReference);
            Optional(json, "payer", t.Payer);
            Optional(json, "checkoutId", t.CorrelationId);
            Optional(json, "merchantReference", t.MerchantReference);
            Optional(json, "reversedTransactionId", t.ReversedTransactionId);
            Optional(json, "status", status);
            if (status == "failed")
            {
                json.WriteString("reason", "The customer declined.");
            }

            json.WriteEndObject();
        }

        var body = buffer.ToArray();
        return new ProviderCallback(Provider, type, body, new Dictionary<string, string> { [SignatureHeader] = Sign(body) });
    }

    private bool Verify(ReadOnlySpan<byte> body, string signature)
    {
        Span<byte> expected = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(_secret, body, expected);
        try
        {
            return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(signature));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void Optional(Utf8JsonWriter json, string name, string? value)
    {
        if (value is not null)
        {
            json.WriteString(name, value);
        }
    }

    private static Money StatementAmount(Money amount, SimulatedFaults faults, Money? statementAmount) =>
        faults.HasFlag(SimulatedFaults.AmountMismatch) ? statementAmount ?? amount.Add(new Money(1m, amount.Currency)) : amount;

    private string NextReceipt() => $"SIM{++_receipts:D7}";

    private sealed record Delivery(long Order, DateTimeOffset DueAt, ProviderCallback Callback, bool Held);
}
