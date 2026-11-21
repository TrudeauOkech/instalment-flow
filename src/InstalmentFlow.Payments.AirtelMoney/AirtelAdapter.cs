using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace InstalmentFlow.Payments.AirtelMoney;

/// <summary>
/// The Airtel Money (Airtel Africa Open API) adapter (ADR-016; see docs/integrations/airtel.md for what is verified):
/// USSD push collection, transaction enquiry, and collection callbacks. Airtel's callbacks are not known to be signed
/// and do not state the amount, so they are treated as prompts: the inbox confirms each with an enquiry before
/// recording or failing anything, and the amount is the one requested. It never starts refunds or disbursements (ADR-010).
/// </summary>
public sealed class AirtelAdapter : IPaymentProviderAdapter, ICollectionInitiator, ITransactionStatusQuery
{
    /// <summary>The callback kind of a collection result; pass the account as <see cref="ProviderCallback.ProviderAccount"/>.</summary>
    public const string CollectionResult = "collection";

    private static readonly TimeSpan DefaultTokenLifetime = TimeSpan.FromSeconds(180);
    private readonly HttpClient _http;
    private readonly IProviderCredentialSource _credentials;
    private readonly AirtelOptions _options;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, AirtelAccount> _accounts;
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset ValidUntil)> _tokens = new(StringComparer.Ordinal);

    /// <summary>Creates the adapter.</summary>
    /// <param name="http">The HTTP client for the Open API (its base address is not used).</param>
    /// <param name="credentials">Per-account secrets (<see cref="AirtelCredentialNames"/>).</param>
    /// <param name="options">The environment and the accounts served.</param>
    /// <param name="timeProvider">The clock; use the engine's.</param>
    public AirtelAdapter(HttpClient http, IProviderCredentialSource credentials, AirtelOptions options, TimeProvider? timeProvider = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = timeProvider ?? TimeProvider.System;
        _accounts = options.Accounts.ToDictionary(a => a.Account, StringComparer.Ordinal);
    }

    /// <inheritdoc cref="IPaymentProviderAdapter.Provider" />
    public string Provider => "airtel";

    /// <inheritdoc />
    public ProviderCapabilities Capabilities => ProviderCapabilities.MerchantInitiatedCollection | ProviderCapabilities.StatusQuery;

    /// <inheritdoc />
    public ValueTask<CallbackParseResult> ParseCallbackAsync(ProviderCallback callback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (callback.Body.Length > _options.MaxCallbackBytes)
        {
            return Result(CallbackParseResult.Reject($"The callback is larger than {_options.MaxCallbackBytes} bytes."));
        }

        if (callback.Kind != CollectionResult)
        {
            return Result(CallbackParseResult.Reject($"Unknown Airtel callback kind '{callback.Kind}'."));
        }

        if (callback.ProviderAccount is not { Length: > 0 } account || !_accounts.TryGetValue(account, out var configured))
        {
            return Result(CallbackParseResult.Reject("Airtel callbacks do not name their account; the endpoint must pass a configured one."));
        }

        try
        {
            using var json = JsonDocument.Parse(callback.Body, new JsonDocumentOptions { MaxDepth = 16 });
            var transaction = AirtelJson.Transaction(json.RootElement);
            var reference = AirtelJson.Text(transaction, "id");
            var status = AirtelJson.Text(transaction, "status_code") ?? AirtelJson.Text(transaction, "status");
            if (transaction is null || string.IsNullOrWhiteSpace(reference) || string.IsNullOrWhiteSpace(status))
            {
                return Result(CallbackParseResult.Reject("The callback lacks transaction.id or transaction.status_code."));
            }

            var message = AirtelJson.Text(transaction, "message");
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(message))
            {
                metadata["message"] = message;
            }

            switch (status.Trim().ToUpperInvariant())
            {
                case "TS":
                {
                    var receipt = AirtelJson.Text(transaction, "airtel_money_id");
                    if (string.IsNullOrWhiteSpace(receipt))
                    {
                        return Result(CallbackParseResult.Reject("A successful callback lacks airtel_money_id."));
                    }

                    var amount = AirtelJson.Decimal(AirtelJson.Text(transaction, "amount")) is decimal value && value > 0 ? new Money(value, configured.Currency) : null;
                    return Result(CallbackParseResult.Accept(NormalisedPaymentEvent.CollectionSucceeded(
                        Provider, account, null, reference.Trim(), receipt.Trim(), amount, _time.GetUtcNow(), metadata: metadata, requiresVerification: true)));
                }

                case "TF" or "TE":
                    return Result(CallbackParseResult.Accept(NormalisedPaymentEvent.CollectionFailed(
                        Provider, account, null, reference.Trim(), $"{status}: {message ?? "Failed"}", metadata, requiresVerification: true)));

                default:
                    return Result(CallbackParseResult.Accept());   // in progress or ambiguous: the resolver keeps asking
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InstalmentFlowException)
        {
            return Result(CallbackParseResult.Reject($"Malformed callback: {ex.Message}"));
        }
    }

    /// <inheritdoc />
    public async Task<InitiationResult> InitiateAsync(CollectionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_accounts.TryGetValue(request.ProviderAccount, out var account))
        {
            return InitiationResult.Rejected($"Airtel account {request.ProviderAccount} is not configured.");
        }

        if (request.Amount.Currency != account.Currency || (!_options.AllowFractionalAmounts && request.Amount.Amount % 1 != 0))
        {
            return InitiationResult.Rejected($"Airtel account {account.Account} collects whole {account.Currency.Code}; {request.Amount} was requested.");
        }

        if (AirtelMsisdn.Format(request.Payer, account.Country, _options.MsisdnFormat) is not { } msisdn)
        {
            return InitiationResult.Rejected($"The payer is not a valid {account.Country} mobile number.");
        }

        var amount = request.Amount.Amount % 1 == 0 ? (object)(long)request.Amount.Amount : request.Amount.Amount;
        var body = new Dictionary<string, object>
        {
            ["reference"] = request.Description ?? request.AccountReference,
            ["subscriber"] = new Dictionary<string, object> { ["country"] = account.Country, ["currency"] = account.Currency.Code, ["msisdn"] = msisdn },
            ["transaction"] = new Dictionary<string, object>
            {
                ["amount"] = amount, ["country"] = account.Country, ["currency"] = account.Currency.Code, ["id"] = request.MerchantReference,
            },
        };
        var (status, json) = await SendAsync(account, HttpMethod.Post, _options.CollectionPath, body, cancellationToken).ConfigureAwait(false);
        if ((int)status is >= 400 and < 500)
        {
            return InitiationResult.Rejected(Describe(status, json));
        }

        // Only an explicit success is "accepted". Anything else, including a refusal code, is left ambiguous: sources
        // disagree on what Airtel's codes mean, so the enquiry, not a guess, decides whether money moved.
        return (int)status is >= 200 and < 300 && Success(json)
            ? InitiationResult.Accepted(null)
            : InitiationResult.Unknown(Describe(status, json));
    }

    /// <inheritdoc />
    public async Task<StatusQueryResult> QueryAsync(ProviderTransactionRef reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!_accounts.TryGetValue(reference.ProviderAccount, out var account))
        {
            return StatusQueryResult.NotFound;
        }

        var (status, json) = await SendAsync(account, HttpMethod.Get, _options.EnquiryPath + Uri.EscapeDataString(reference.MerchantReference), null, cancellationToken).ConfigureAwait(false);
        var code = AirtelJson.Text(AirtelJson.Child(json, "status"), "code");
        if (status == HttpStatusCode.NotFound || code is "404")
        {
            return StatusQueryResult.NotFound;
        }

        if ((int)status is < 200 or >= 300)
        {
            throw new AirtelApiException((int)status, ResultCode(json), Describe(status, json));
        }

        var transaction = AirtelJson.Transaction(json);
        var message = AirtelJson.Text(transaction, "message");
        return (AirtelJson.Text(transaction, "status") ?? AirtelJson.Text(transaction, "status_code"))?.Trim().ToUpperInvariant() switch
        {
            "TS" when AirtelJson.Text(transaction, "airtel_money_id") is { Length: > 0 } receipt =>
                StatusQueryResult.Succeeded(receipt, AirtelJson.Decimal(AirtelJson.Text(transaction, "amount")) is decimal paid && paid > 0 ? new Money(paid, account.Currency) : null, null),
            var failed when failed is "TF" or "TE" => StatusQueryResult.Failed($"{failed}: {message ?? "Failed"}"),
            "TA" => StatusQueryResult.Ambiguous(message),
            _ => StatusQueryResult.Pending,   // TIP (or TI), a success without a receipt yet, or a code no source documents
        };
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(AirtelAccount account, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var secrets = await _credentials.GetAsync(Provider, account.Account, ct).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            var token = await TokenAsync(secrets, attempt > 0, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, new Uri(_options.Address, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Add("X-Country", account.Country);
            request.Headers.Add("X-Currency", account.Currency.Code);
            if (body is not null)
            {
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            }

            var response = await ReadAsync(request, ct).ConfigureAwait(false);
            if (response.Status != HttpStatusCode.Unauthorized || attempt > 0)
            {
                return response;
            }
        }
    }

    private async Task<string> TokenAsync(ProviderCredentials secrets, bool refresh, CancellationToken ct)
    {
        var clientId = secrets.Require(AirtelCredentialNames.ClientId);
        if (!refresh && _tokens.TryGetValue(clientId, out var cached) && cached.ValidUntil > _time.GetUtcNow())
        {
            return cached.Token;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.Address, "/auth/oauth2/token"))
        {
            Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["client_id"] = clientId, ["client_secret"] = secrets.Require(AirtelCredentialNames.ClientSecret), ["grant_type"] = "client_credentials",
            }), Encoding.UTF8, "application/json"),
        };
        var (status, json) = await ReadAsync(request, ct).ConfigureAwait(false);
        var token = AirtelJson.Text(json, "access_token");
        if ((int)status is < 200 or >= 300 || string.IsNullOrEmpty(token))
        {
            throw new AirtelApiException((int)status, ResultCode(json), "No access token: " + Describe(status, json));
        }

        // Sources report both 180 and 3600 seconds, as a number or a string; refresh at 80% of whatever is stated.
        var lifetime = AirtelJson.Decimal(AirtelJson.Text(json, "expires_in")) is decimal seconds && seconds > 0 ? TimeSpan.FromSeconds((long)seconds) : DefaultTokenLifetime;
        _tokens[clientId] = (token, _time.GetUtcNow() + TimeSpan.FromTicks(lifetime.Ticks / 5 * 4));
        return token;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> ReadAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return (response.StatusCode, default);
            }

            using var document = JsonDocument.Parse(text);
            return (response.StatusCode, document.RootElement.Clone());
        }
        catch (JsonException)
        {
            return (response.StatusCode, default);
        }
    }

    private static bool Success(JsonElement json) =>
        string.Equals(AirtelJson.Text(AirtelJson.Child(json, "status"), "success"), "true", StringComparison.OrdinalIgnoreCase);

    private static string? ResultCode(JsonElement json) =>
        AirtelJson.Text(AirtelJson.Child(json, "status"), "result_code") ?? AirtelJson.Text(AirtelJson.Child(json, "status"), "response_code");

    private static string Describe(HttpStatusCode status, JsonElement json) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)status} {ResultCode(json) ?? "-"}: {AirtelJson.Text(AirtelJson.Child(json, "status"), "message") ?? AirtelJson.Text(json, "error_description") ?? status.ToString()}");

    private static ValueTask<CallbackParseResult> Result(CallbackParseResult result) => ValueTask.FromResult(result);
}
