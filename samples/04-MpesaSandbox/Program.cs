// Sample 04: the M-Pesa adapter against the Daraja sandbox. It pushes an STK request to the sandbox test phone through
// a payment intent, checks its status, and simulates a C2B paybill payment. With MPESA_RECORD_DIR set, it saves every
// Daraja response (tokens redacted) as candidate contract fixtures (docs/integrations/mpesa.md, "Before release").
//
// Callbacks need a public HTTPS endpoint: point MPESA_CALLBACK_BASE at a host running MapInstalmentFlowCallbacks
// (sample 05, through a tunnel) or at any endpoint you can read, to capture them.
//
// Without sandbox credentials it explains what to set and exits successfully, so CI can run it.
using System.Text.RegularExpressions;
using InstalmentFlow;
using InstalmentFlow.Payments;
using InstalmentFlow.Payments.Mpesa;

var key = Environment.GetEnvironmentVariable("MPESA_CONSUMER_KEY");
var secret = Environment.GetEnvironmentVariable("MPESA_CONSUMER_SECRET");
if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(secret))
{
    Console.WriteLine("""
        Sample 04 needs a Daraja sandbox app (https://developer.safaricom.co.ke) with M-Pesa Express and C2B enabled.
        Set, in your shell (never in code or chat):
          MPESA_CONSUMER_KEY, MPESA_CONSUMER_SECRET   the app's keys
          MPESA_PASSKEY                               the Lipa na M-Pesa Online test passkey
          MPESA_SHORTCODE    (default 174379)         the sandbox shortcode
          MPESA_PHONE        (default 254708374149)   the sandbox test phone
          MPESA_CALLBACK_BASE                         a public https base URL that receives callbacks
          MPESA_RECORD_DIR   (optional)               where to save Daraja responses as fixtures
        Skipping: no credentials.
        """);
    return 0;
}

var shortCode = Environment.GetEnvironmentVariable("MPESA_SHORTCODE") ?? "174379";
var phone = Environment.GetEnvironmentVariable("MPESA_PHONE") ?? "254708374149";
var callbackBase = new Uri((Environment.GetEnvironmentVariable("MPESA_CALLBACK_BASE") ?? "https://example.com/hooks").TrimEnd('/') + "/");
var recordDir = Environment.GetEnvironmentVariable("MPESA_RECORD_DIR");

var options = new MpesaOptions { Environment = MpesaEnvironment.Sandbox };
options.Accounts.Add(new MpesaAccount
{
    ShortCode = shortCode,
    StkCallbackUrl = new Uri(callbackBase, $"mpesa/stk/{shortCode}"),
    ResultUrl = new Uri(callbackBase, $"mpesa/transaction-status/{shortCode}"),
    QueueTimeoutUrl = new Uri(callbackBase, $"mpesa/transaction-status-timeout/{shortCode}"),
});
using var http = new HttpClient(recordDir is null ? new HttpClientHandler() : new Recorder(recordDir, new HttpClientHandler()));
var mpesa = new MpesaAdapter(http, new EnvironmentCredentials(key, secret, Environment.GetEnvironmentVariable("MPESA_PASSKEY") ?? string.Empty), options);

var flows = InstalmentFlowFactory.CreateInMemory(b => b.AddTenant("sandbox", Currency.KES, "Africa/Nairobi"));
var payments = InstalmentFlowPayments.CreateInMemory(flows, p => p.AddProvider(mpesa));
await payments.Routing.MapAsync("mpesa", shortCode, "sandbox");
var flow = flows.ForTenant("sandbox");
var customer = await flow.Accounts.OpenAsync(new OpenAccountRequest { IdempotencyKey = "acct", ExternalId = "C-1" });
await flow.Obligations.OpenAsync(new OpenObligationRequest
{
    IdempotencyKey = "loan", Account = customer.AccountId, ExternalId = "L-1", PaymentReference = "LN1",
    Components = [Component.Principal(Money.Kes(100m))], Plan = PlanSpec.Equal(Frequency.Monthly, 1, DateOnly.FromDateTime(DateTime.UtcNow)),
});

var intent = await payments.ForTenant("sandbox").Intents.InitiateAsync(new InitiateCollectionRequest
{
    IdempotencyKey = $"sandbox-{Guid.NewGuid():N}", Provider = "mpesa", ProviderAccount = shortCode, Payer = phone, Amount = Money.Kes(1m),
    Target = PaymentTarget.ForObligation(ObligationRef.ByReference("LN1")),
});
Console.WriteLine($"STK push: {intent.Status} {intent.CorrelationId} {intent.FailureReason}");

if (intent.CorrelationId is { } checkout)
{
    for (var i = 0; i < 6; i++)
    {
        await Task.Delay(TimeSpan.FromSeconds(10));
        try
        {
            var status = await mpesa.QueryAsync(new ProviderTransactionRef(shortCode, checkout, intent.IntentId.ToString("N")), CancellationToken.None);
            Console.WriteLine($"STK query: {status.Status} {status.Reason}");
            if (status.Status == ProviderTransactionStatus.Failed)
            {
                break;
            }
        }
        catch (MpesaApiException ex)
        {
            Console.WriteLine($"STK query: {ex.Message}");
        }
    }
}

try
{
    Console.WriteLine($"C2B register: {await mpesa.RegisterC2BUrlsAsync(shortCode, new Uri(callbackBase, "mpesa/c2b-confirmation"), new Uri(callbackBase, "mpesa/validate"))}");
    Console.WriteLine($"C2B simulate: {await mpesa.SimulateC2BAsync(shortCode, Money.Kes(1m), phone, "LN1")}");
}
catch (MpesaApiException ex)
{
    Console.WriteLine($"C2B: {ex.Message}");
}

Console.WriteLine(recordDir is null ? "Set MPESA_RECORD_DIR to save these responses." : $"Responses saved in {recordDir}.");
return 0;

/// <summary>Sandbox secrets from environment variables.</summary>
internal sealed class EnvironmentCredentials(string key, string secret, string passkey) : IProviderCredentialSource
{
    public ValueTask<ProviderCredentials> GetAsync(string provider, string providerAccount, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ProviderCredentials(new Dictionary<string, string>
        {
            [MpesaCredentialNames.ConsumerKey] = key,
            [MpesaCredentialNames.ConsumerSecret] = secret,
            [MpesaCredentialNames.Passkey] = passkey,
        }));
}

/// <summary>Saves each Daraja response body, with access tokens redacted, as a numbered fixture file.</summary>
internal sealed partial class Recorder(string directory, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private int _count;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Directory.CreateDirectory(directory);
        var name = $"{Interlocked.Increment(ref _count):D2}-{request.RequestUri!.AbsolutePath.Trim('/').Replace('/', '-')}-{(int)response.StatusCode}.json";
        await File.WriteAllTextAsync(Path.Combine(directory, name), Token().Replace(body, "\"access_token\":\"REDACTED\""), cancellationToken);
        return response;
    }

    [GeneratedRegex("\"access_token\"\\s*:\\s*\"[^\"]*\"")]
    private static partial Regex Token();
}
