using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace InstalmentFlow.Payments.Mpesa;

/// <summary>
/// The Safaricom M-Pesa (Daraja) adapter (ADR-016; see docs/integrations/mpesa.md for what is verified). It parses C2B
/// confirmations, STK results and Transaction Status results; requests STK pushes and queries them; and reads Pull
/// Transactions as a statement. It never starts reversals or payouts (ADR-010). Amounts are whole shillings: a
/// fractional amount is held for a person, never rounded.
/// </summary>
public sealed class MpesaAdapter : IPaymentProviderAdapter, ICollectionInitiator, ITransactionStatusQuery, IStatementSource
{
    /// <summary>The callback kind of a C2B confirmation.</summary>
    public const string C2BConfirmation = "c2b-confirmation";

    /// <summary>The callback kind of an STK result; pass the shortcode as <see cref="ProviderCallback.ProviderAccount"/>.</summary>
    public const string StkResult = "stk";

    /// <summary>The callback kind of a Transaction Status result.</summary>
    public const string TransactionStatusResult = "transaction-status";

    private const string StillProcessing = "500.001.1001";
    private const int PullPageLimit = 1_000;
    private readonly MpesaClient _client;
    private readonly MpesaOptions _options;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, MpesaAccount> _accounts;

    /// <summary>Creates the adapter.</summary>
    /// <param name="http">The HTTP client for Daraja (its base address is not used).</param>
    /// <param name="credentials">Per-shortcode secrets (<see cref="MpesaCredentialNames"/>).</param>
    /// <param name="options">The environment and the shortcodes served.</param>
    /// <param name="timeProvider">The clock; use the engine's.</param>
    public MpesaAdapter(HttpClient http, IProviderCredentialSource credentials, MpesaOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
        _client = new MpesaClient(http, credentials, options, _time);
        _accounts = options.Accounts.ToDictionary(a => a.ShortCode, StringComparer.Ordinal);
    }

    /// <inheritdoc cref="IPaymentProviderAdapter.Provider" />
    public string Provider => MpesaClient.Provider;

    /// <inheritdoc />
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.CustomerInitiatedCallbacks | ProviderCapabilities.MerchantInitiatedCollection | ProviderCapabilities.StatusQuery |
        ProviderCapabilities.StatementSource | ProviderCapabilities.ValidationHook;

    // ----- Callbacks -----

    /// <inheritdoc />
    public ValueTask<CallbackParseResult> ParseCallbackAsync(ProviderCallback callback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (callback.Body.Length > _options.MaxCallbackBytes)
        {
            return Result(CallbackParseResult.Reject($"The callback is larger than {_options.MaxCallbackBytes} bytes."));
        }

        try
        {
            using var json = JsonDocument.Parse(callback.Body, new JsonDocumentOptions { MaxDepth = 16 });
            return Result(callback.Kind switch
            {
                C2BConfirmation => Confirmation(json.RootElement, callback.ProviderAccount),
                StkResult => Stk(json.RootElement, callback.ProviderAccount),
                TransactionStatusResult => Status(json.RootElement, callback.ProviderAccount),
                _ => CallbackParseResult.Reject($"Unknown M-Pesa callback kind '{callback.Kind}'."),
            });
        }
        catch (JsonException ex)
        {
            return Result(CallbackParseResult.Reject($"The callback is not JSON: {ex.Message}"));
        }
        catch (Exception ex) when (ex is ArgumentException or InstalmentFlowException or OverflowException)
        {
            return Result(CallbackParseResult.Reject($"The callback has an invalid value: {ex.Message}"));
        }
    }

    // ----- Collections (STK push) -----

    /// <inheritdoc />
    public async Task<InitiationResult> InitiateAsync(CollectionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_accounts.TryGetValue(request.ProviderAccount, out var account) || account.StkCallbackUrl is null)
        {
            return InitiationResult.Rejected($"Shortcode {request.ProviderAccount} is not configured for STK push.");
        }

        if (request.Amount.Currency != Currency.KES || request.Amount.Amount % 1 != 0)
        {
            return InitiationResult.Rejected($"M-Pesa collects whole Kenyan shillings; {request.Amount} was requested.");
        }

        if (request.AccountReference.Length > _options.AccountReferenceMaxLength)
        {
            return InitiationResult.Rejected($"The account reference '{request.AccountReference}' is longer than {_options.AccountReferenceMaxLength} characters.");
        }

        if (MpesaPhone.Normalise(request.Payer) is not { } phone)
        {
            return InitiationResult.Rejected("The payer is not a Kenyan mobile number.");
        }

        var secrets = await _client.CredentialsAsync(account.ShortCode, cancellationToken).ConfigureAwait(false);
        var timestamp = MpesaTime.FormatCompact(_time.GetUtcNow());
        var description = request.Description ?? "Payment";
        var response = await _client.PostAsync(account.ShortCode, "/mpesa/stkpush/v1/processrequest", new Dictionary<string, object>
        {
            ["BusinessShortCode"] = account.ShortCode,
            ["Password"] = Password(account.ShortCode, secrets.Require(MpesaCredentialNames.Passkey), timestamp),
            ["Timestamp"] = timestamp,
            ["TransactionType"] = account.CollectionType == MpesaCollectionType.BuyGoods ? "CustomerBuyGoodsOnline" : "CustomerPayBillOnline",
            ["Amount"] = (long)request.Amount.Amount,
            ["PartyA"] = phone,
            ["PartyB"] = account.PartyB ?? account.ShortCode,
            ["PhoneNumber"] = phone,
            ["CallBackURL"] = account.StkCallbackUrl.ToString(),
            ["AccountReference"] = request.AccountReference,
            ["TransactionDesc"] = description.Length > 13 ? description[..13] : description,
        }, cancellationToken).ConfigureAwait(false);

        if (response.IsSuccess && MpesaJson.Text(response.Body, "ResponseCode") == "0" && MpesaJson.Text(response.Body, "CheckoutRequestID") is { } checkout)
        {
            return InitiationResult.Accepted(checkout);
        }

        // A client error means Daraja refused the request; anything else leaves the outcome unknown.
        var reason = $"{response.ErrorCode ?? MpesaJson.Text(response.Body, "ResponseCode")}: {response.ErrorMessage ?? MpesaJson.Text(response.Body, "ResponseDescription") ?? response.Status.ToString()}";
        return (int)response.Status is >= 400 and < 500 ? InitiationResult.Rejected(reason) : InitiationResult.Unknown(reason);
    }

    /// <inheritdoc />
    public async Task<StatusQueryResult> QueryAsync(ProviderTransactionRef reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.CorrelationId is null || !_accounts.ContainsKey(reference.ProviderAccount))
        {
            return StatusQueryResult.NotFound;   // STK has no merchant reference: without the CheckoutRequestID it cannot be queried
        }

        var secrets = await _client.CredentialsAsync(reference.ProviderAccount, cancellationToken).ConfigureAwait(false);
        var timestamp = MpesaTime.FormatCompact(_time.GetUtcNow());
        var response = await _client.PostAsync(reference.ProviderAccount, "/mpesa/stkpushquery/v1/query", new Dictionary<string, object>
        {
            ["BusinessShortCode"] = reference.ProviderAccount,
            ["Password"] = Password(reference.ProviderAccount, secrets.Require(MpesaCredentialNames.Passkey), timestamp),
            ["Timestamp"] = timestamp,
            ["CheckoutRequestID"] = reference.CorrelationId,
        }, cancellationToken).ConfigureAwait(false);

        if (response.ErrorCode == StillProcessing)
        {
            return StatusQueryResult.Pending;
        }

        if (!response.IsSuccess)
        {
            throw response.Error();
        }

        // The query confirms payment but names no receipt or amount, so the payment itself comes from the callback or
        // Pull Transactions; until then the intent stays open.
        return MpesaJson.Text(response.Body, "ResultCode") switch
        {
            null => StatusQueryResult.Pending,
            "0" => StatusQueryResult.Pending,
            var code => StatusQueryResult.Failed($"{code}: {MpesaJson.Text(response.Body, "ResultDesc")}"),
        };
    }

    // ----- Statement (Pull Transactions) -----

    /// <inheritdoc />
    public async IAsyncEnumerable<ProviderStatementLine> ReadAsync(string providerAccount, DateTimeOffset windowStart, DateTimeOffset windowEnd, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerAccount);
        var offset = 0;
        for (var page = 0; page < PullPageLimit; page++)
        {
            var response = await _client.PostAsync(providerAccount, "/pulltransactions/v1/query", new Dictionary<string, object>
            {
                ["ShortCode"] = providerAccount,
                ["StartDate"] = MpesaTime.FormatSpaced(windowStart),
                ["EndDate"] = MpesaTime.FormatSpaced(windowEnd),
                ["OffSetValue"] = offset.ToString(CultureInfo.InvariantCulture),
            }, cancellationToken).ConfigureAwait(false);
            var code = MpesaJson.Text(response.Body, "ResponseCode");
            if (code == "1001")
            {
                yield break;   // no (more) transactions; Daraja may send this with HTTP 500
            }

            if (!response.IsSuccess || code != "1000")
            {
                throw response.Error();
            }

            var items = PullItems(response.Body).ToList();
            foreach (var item in items)
            {
                if (PullLine(providerAccount, item) is { } line && line.ValueAt >= windowStart && line.ValueAt < windowEnd)
                {
                    yield return line;
                }
            }

            if (items.Count == 0)
            {
                yield break;
            }

            offset += items.Count;
        }
    }

    // ----- Management -----

    /// <summary>Registers the C2B confirmation and validation URLs of a shortcode.</summary>
    /// <param name="shortCode">The shortcode.</param>
    /// <param name="confirmationUrl">Where confirmations are sent.</param>
    /// <param name="validationUrl">Where validations are sent, if external validation is enabled for the shortcode.</param>
    /// <param name="acceptWhenValidationFails">What M-Pesa does when validation does not answer in time: accept (recommended) or cancel.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<string> RegisterC2BUrlsAsync(string shortCode, Uri confirmationUrl, Uri validationUrl, bool acceptWhenValidationFails = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirmationUrl);
        ArgumentNullException.ThrowIfNull(validationUrl);
        var response = await _client.PostAsync(shortCode, _options.C2BRegisterPath, new Dictionary<string, object>
        {
            ["ShortCode"] = shortCode,
            ["ResponseType"] = acceptWhenValidationFails ? "Completed" : "Cancelled",
            ["ConfirmationURL"] = confirmationUrl.ToString(),
            ["ValidationURL"] = validationUrl.ToString(),
        }, cancellationToken).ConfigureAwait(false);
        return response.IsSuccess ? MpesaJson.Text(response.Body, "ResponseDescription") ?? "Registered" : throw response.Error();
    }

    /// <summary>Registers a shortcode for Pull Transactions.</summary>
    public async Task<string> RegisterPullAsync(string shortCode, string nominatedNumber, Uri callbackUrl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callbackUrl);
        var response = await _client.PostAsync(shortCode, "/pulltransactions/v1/register", new Dictionary<string, object>
        {
            ["ShortCode"] = shortCode,
            ["RequestType"] = "Pull",
            ["NominatedNumber"] = nominatedNumber,
            ["CallBackURL"] = callbackUrl.ToString(),
        }, cancellationToken).ConfigureAwait(false);
        var code = MpesaJson.Text(response.Body, "ResponseStatus");
        return code is "1000" or "1001" ? MpesaJson.Text(response.Body, "ResponseDescription") ?? code : throw response.Error();
    }

    /// <summary>
    /// Asks for the status of a receipt, for example to verify a high-value payment or recover a missed one. The answer
    /// arrives later at the shortcode's <see cref="MpesaAccount.ResultUrl"/>, as a <see cref="TransactionStatusResult"/> callback.
    /// Returns Daraja's conversation ID.
    /// </summary>
    public async Task<string> RequestTransactionStatusAsync(string shortCode, string receipt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receipt);
        if (!_accounts.TryGetValue(shortCode, out var account) || account.ResultUrl is null || account.QueueTimeoutUrl is null)
        {
            throw new InvalidOperationException($"Shortcode {shortCode} needs a ResultUrl and a QueueTimeoutUrl for Transaction Status.");
        }

        var secrets = await _client.CredentialsAsync(shortCode, cancellationToken).ConfigureAwait(false);
        var response = await _client.PostAsync(shortCode, "/mpesa/transactionstatus/v1/query", new Dictionary<string, object>
        {
            ["Initiator"] = secrets.Require(MpesaCredentialNames.InitiatorName),
            ["SecurityCredential"] = MpesaSecurityCredential.Encrypt(secrets.Require(MpesaCredentialNames.InitiatorPassword), secrets.Require(MpesaCredentialNames.Certificate)),
            ["CommandID"] = "TransactionStatusQuery",
            ["TransactionID"] = receipt,
            ["PartyA"] = shortCode,
            ["IdentifierType"] = "4",
            ["ResultURL"] = account.ResultUrl.ToString(),
            ["QueueTimeOutURL"] = account.QueueTimeoutUrl.ToString(),
            ["Remarks"] = "Reconciliation",
            ["Occasion"] = "Reconciliation",
        }, cancellationToken).ConfigureAwait(false);
        return response.IsSuccess && MpesaJson.Text(response.Body, "ConversationID") is { } conversation ? conversation : throw response.Error();
    }

    /// <summary>Sandbox only: simulates a customer paying a shortcode (C2B).</summary>
    public async Task<string> SimulateC2BAsync(string shortCode, Money amount, string msisdn, string billReference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(amount);
        if (_options.Environment != MpesaEnvironment.Sandbox)
        {
            throw new InvalidOperationException("C2B simulation exists only in the sandbox.");
        }

        var response = await _client.PostAsync(shortCode, "/mpesa/c2b/v1/simulate", new Dictionary<string, object>
        {
            ["ShortCode"] = shortCode,
            ["CommandID"] = "CustomerPayBillOnline",
            ["Amount"] = (long)amount.Amount,
            ["Msisdn"] = msisdn,
            ["BillRefNumber"] = billReference,
        }, cancellationToken).ConfigureAwait(false);
        return response.IsSuccess ? MpesaJson.Text(response.Body, "ResponseDescription") ?? "Accepted" : throw response.Error();
    }

    // ----- Parsing -----

    private CallbackParseResult Confirmation(JsonElement root, string? endpointAccount)
    {
        var receipt = MpesaJson.Text(root, "TransID");
        var shortCode = MpesaJson.Text(root, "BusinessShortCode") ?? endpointAccount;
        var amount = MpesaJson.Decimal(MpesaJson.Text(root, "TransAmount"));
        var time = MpesaTime.ParseCompact(MpesaJson.Text(root, "TransTime"));
        if (string.IsNullOrWhiteSpace(receipt) || string.IsNullOrWhiteSpace(shortCode) || amount is null || time is null)
        {
            return CallbackParseResult.Reject("The C2B confirmation lacks TransID, BusinessShortCode, TransAmount or a valid TransTime.");
        }

        if (endpointAccount is not null && endpointAccount != shortCode)
        {
            return CallbackParseResult.Reject($"The confirmation is for shortcode {shortCode}, not {endpointAccount}.");
        }

        if (amount <= 0)
        {
            return CallbackParseResult.Reject("The C2B confirmation amount is not positive.");
        }

        var metadata = Metadata(("transactionType", MpesaJson.Text(root, "TransactionType")), ("invoiceNumber", MpesaJson.Text(root, "InvoiceNumber")),
            ("thirdPartyTransId", MpesaJson.Text(root, "ThirdPartyTransID")));
        var reference = MpesaJson.Text(root, "BillRefNumber")?.Trim();
        var paid = NormalisedPaymentEvent.PaymentReceived(Provider, shortCode.Trim(), receipt.Trim(), new Money(amount.Value, Currency.KES), time.Value,
            string.IsNullOrEmpty(reference) ? null : reference, MpesaJson.Text(root, "MSISDN"), metadata);
        return WholeShillings(amount.Value, paid);
    }

    private CallbackParseResult Stk(JsonElement root, string? endpointAccount)
    {
        var callback = MpesaJson.Child(root, "Body") is { } body ? MpesaJson.Child(body, "stkCallback") : null;
        if (callback is not { } stk || MpesaJson.Text(stk, "CheckoutRequestID") is not { } checkout || MpesaJson.Text(stk, "ResultCode") is not { } code)
        {
            return CallbackParseResult.Reject("The STK result lacks Body.stkCallback, CheckoutRequestID or ResultCode.");
        }

        if (string.IsNullOrWhiteSpace(endpointAccount))
        {
            return CallbackParseResult.Reject("STK results do not name their shortcode; the endpoint must pass it.");
        }

        var metadata = Metadata(("merchantRequestId", MpesaJson.Text(stk, "MerchantRequestID")));
        if (code != "0")
        {
            return CallbackParseResult.Accept(NormalisedPaymentEvent.CollectionFailed(Provider, endpointAccount, checkout, null,
                $"{code}: {MpesaJson.Text(stk, "ResultDesc") ?? "Failed"}", metadata));
        }

        var items = MpesaJson.Pairs(MpesaJson.Child(stk, "CallbackMetadata") is { } meta ? MpesaJson.Child(meta, "Item") : null);
        var receipt = items.GetValueOrDefault("MpesaReceiptNumber");
        var amount = MpesaJson.Decimal(items.GetValueOrDefault("Amount"));
        var time = MpesaTime.ParseCompact(items.GetValueOrDefault("TransactionDate"));
        if (string.IsNullOrWhiteSpace(receipt) || amount is not > 0 || time is null)
        {
            return CallbackParseResult.Reject("A successful STK result lacks MpesaReceiptNumber, a positive Amount or a valid TransactionDate.");
        }

        var paid = NormalisedPaymentEvent.CollectionSucceeded(Provider, endpointAccount, checkout, null, receipt, new Money(amount.Value, Currency.KES), time.Value,
            payer: items.GetValueOrDefault("PhoneNumber"), metadata: metadata);
        return WholeShillings(amount.Value, paid);
    }

    private CallbackParseResult Status(JsonElement root, string? endpointAccount)
    {
        if (MpesaJson.Child(root, "Result") is not { } result || MpesaJson.Text(result, "ResultCode") is not { } code)
        {
            return CallbackParseResult.Reject("The Transaction Status result lacks Result.ResultCode.");
        }

        if (code != "0")
        {
            return CallbackParseResult.Accept();   // the query failed (for example a locked initiator); nothing is learnt
        }

        var parameters = MpesaJson.Pairs(MpesaJson.Child(result, "ResultParameters") is { } rp ? MpesaJson.Child(rp, "ResultParameter") : null);
        if (!string.Equals(parameters.GetValueOrDefault("TransactionStatus"), "Completed", StringComparison.OrdinalIgnoreCase))
        {
            return CallbackParseResult.Accept();
        }

        var receipt = parameters.GetValueOrDefault("ReceiptNo");
        var amount = MpesaJson.Decimal(parameters.GetValueOrDefault("Amount"));
        var time = MpesaTime.ParseCompact(parameters.GetValueOrDefault("FinalisedTime")) ?? MpesaTime.ParseCompact(parameters.GetValueOrDefault("InitiatedTime"));
        var shortCode = endpointAccount ?? PartyId(parameters.GetValueOrDefault("CreditPartyName"));
        if (string.IsNullOrWhiteSpace(receipt) || amount is not > 0 || time is null || string.IsNullOrWhiteSpace(shortCode))
        {
            return CallbackParseResult.Reject("A completed Transaction Status result lacks ReceiptNo, a positive Amount, FinalisedTime or the shortcode.");
        }

        var paid = NormalisedPaymentEvent.PaymentReceived(Provider, shortCode, receipt, new Money(amount.Value, Currency.KES), time.Value,
            payer: PartyId(parameters.GetValueOrDefault("DebitPartyName")), metadata: Metadata(("source", "transaction-status")));
        return WholeShillings(amount.Value, paid);
    }

    private static IEnumerable<JsonElement> PullItems(JsonElement body)
    {
        var response = MpesaJson.Child(body, "Response");
        if (response is not { ValueKind: JsonValueKind.Array } array)
        {
            yield break;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Array)
            {
                foreach (var inner in item.EnumerateArray())
                {
                    yield return inner;
                }
            }
            else
            {
                yield return item;
            }
        }
    }

    private static ProviderStatementLine? PullLine(string shortCode, JsonElement item)
    {
        var receipt = MpesaJson.Text(item, "transactionId");
        var amount = MpesaJson.Decimal(MpesaJson.Text(item, "amount"));
        var time = MpesaTime.ParseFlexible(MpesaJson.Text(item, "trxDate"));
        if (string.IsNullOrWhiteSpace(receipt) || amount is null || amount == 0 || time is null)
        {
            return null;
        }

        var type = MpesaJson.Text(item, "transactiontype") ?? string.Empty;
        var reversed = MpesaJson.Text(item, "originalTransactionId") ?? MpesaJson.Text(item, "linkedTransactionId");
        var isReversal = amount < 0 || type.Contains("revers", StringComparison.OrdinalIgnoreCase);
        if (isReversal && string.IsNullOrWhiteSpace(reversed))
        {
            return null;   // a reversal that does not name what it reverses cannot be applied (docs/integrations/mpesa.md)
        }

        return new ProviderStatementLine
        {
            Provider = MpesaClient.Provider, ProviderAccount = shortCode, TransactionId = receipt, Kind = isReversal ? StatementLineKind.Reversal : StatementLineKind.Credit,
            Amount = new Money(Math.Abs(amount.Value), Currency.KES), ValueAt = time.Value,
            AccountReference = isReversal ? null : NullIfEmpty(MpesaJson.Text(item, "billreference")), Payer = NullIfEmpty(MpesaJson.Text(item, "msisdn")),
            ReversedTransactionId = isReversal ? reversed : null,
        };
    }

    private static CallbackParseResult WholeShillings(decimal amount, NormalisedPaymentEvent paid) =>
        amount % 1 == 0 ? CallbackParseResult.Accept(paid) : CallbackParseResult.Hold($"M-Pesa amounts are whole shillings; {amount.ToString(CultureInfo.InvariantCulture)} was received.", paid);

    private static string Password(string shortCode, string passkey, string timestamp) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(shortCode + passkey + timestamp));

    /// <summary>The identifier in Daraja's <c>"600123 - Org Name"</c> party fields; names are personal data and are dropped.</summary>
    private static string? PartyId(string? party) => party is null ? null : NullIfEmpty(party.Split(" - ", 2)[0].Trim());

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Dictionary<string, string> Metadata(params (string Key, string? Value)[] pairs) =>
        pairs.Where(p => !string.IsNullOrWhiteSpace(p.Value)).ToDictionary(p => p.Key, p => p.Value!.Trim(), StringComparer.Ordinal);

    private static ValueTask<CallbackParseResult> Result(CallbackParseResult result) => ValueTask.FromResult(result);
}

/// <summary>Kenyan mobile numbers in the form Daraja expects (<c>2547…</c> or <c>2541…</c>).</summary>
internal static class MpesaPhone
{
    public static string? Normalise(string number)
    {
        var digits = new string([.. number.Where(char.IsDigit)]);
        if (digits.StartsWith('0') && digits.Length == 10)
        {
            digits = "254" + digits[1..];
        }
        else if (digits.Length == 9 && digits[0] is '7' or '1')
        {
            digits = "254" + digits;
        }

        return digits.Length == 12 && digits.StartsWith("254", StringComparison.Ordinal) && digits[3] is '7' or '1' ? digits : null;
    }
}
