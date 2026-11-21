// Sample 06: the Airtel Money adapter against Airtel's sandbox (UAT). It requests a USSD push through a payment intent,
// then asks the enquiry for its status. With AIRTEL_RECORD_DIR set, it saves every response (tokens redacted) as
// candidate contract fixtures (docs/integrations/airtel.md, "Before release").
//
// Without sandbox credentials it explains what to set and exits successfully, so CI can run it.
using System.Text.RegularExpressions;
using InstalmentFlow;
using InstalmentFlow.Payments;
using InstalmentFlow.Payments.AirtelMoney;

var clientId = Environment.GetEnvironmentVariable("AIRTEL_CLIENT_ID");
var clientSecret = Environment.GetEnvironmentVariable("AIRTEL_CLIENT_SECRET");
if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
{
    Console.WriteLine("""
        Sample 06 needs an Airtel Open API application in UAT with collections enabled.
        Set, in your shell (never in code or chat):
          AIRTEL_CLIENT_ID, AIRTEL_CLIENT_SECRET   the application's keys
          AIRTEL_PHONE                             a UAT subscriber number
          AIRTEL_COLLECTION_PATH (optional)        /merchant/v1/payments/ to try v1 (default v2)
          AIRTEL_BASE_URL        (optional)        another host, for example a country-specific one
          AIRTEL_RECORD_DIR      (optional)        where to save responses as fixtures
        Skipping: no credentials.
        """);
    return 0;
}

var options = new AirtelOptions { Environment = AirtelEnvironment.Sandbox };
options.Accounts.Add(new AirtelAccount { Account = "sandbox-app" });
if (Environment.GetEnvironmentVariable("AIRTEL_COLLECTION_PATH") is { Length: > 0 } path)
{
    options.CollectionPath = path;
}

if (Environment.GetEnvironmentVariable("AIRTEL_BASE_URL") is { Length: > 0 } baseUrl)
{
    options.BaseAddress = new Uri(baseUrl);
}

var recordDir = Environment.GetEnvironmentVariable("AIRTEL_RECORD_DIR");
using var http = new HttpClient(recordDir is null ? new HttpClientHandler() : new Recorder(recordDir, new HttpClientHandler()));
var airtel = new AirtelAdapter(http, new EnvironmentCredentials(clientId, clientSecret), options);

var flows = InstalmentFlowFactory.CreateInMemory(b => b.AddTenant("sandbox", Currency.KES, "Africa/Nairobi"));
var payments = InstalmentFlowPayments.CreateInMemory(flows, p => p.AddProvider(airtel));
await payments.Routing.MapAsync("airtel", "sandbox-app", "sandbox");
var flow = flows.ForTenant("sandbox");
var customer = await flow.Accounts.OpenAsync(new OpenAccountRequest { IdempotencyKey = "acct", ExternalId = "C-1" });
await flow.Obligations.OpenAsync(new OpenObligationRequest
{
    IdempotencyKey = "loan", Account = customer.AccountId, ExternalId = "L-1", PaymentReference = "LN1",
    Components = [Component.Principal(Money.Kes(100m))], Plan = PlanSpec.Equal(Frequency.Monthly, 1, DateOnly.FromDateTime(DateTime.UtcNow)),
});

var intent = await payments.ForTenant("sandbox").Intents.InitiateAsync(new InitiateCollectionRequest
{
    IdempotencyKey = $"sandbox-{Guid.NewGuid():N}", Provider = "airtel", ProviderAccount = "sandbox-app",
    Payer = Environment.GetEnvironmentVariable("AIRTEL_PHONE") ?? "733123456", Amount = Money.Kes(1m),
    Target = PaymentTarget.ForObligation(ObligationRef.ByReference("LN1")),
});
Console.WriteLine($"USSD push: {intent.Status} {intent.FailureReason}");

for (var i = 0; i < 6; i++)
{
    await Task.Delay(TimeSpan.FromSeconds(10));
    try
    {
        var status = await airtel.QueryAsync(new ProviderTransactionRef("sandbox-app", null, intent.IntentId.ToString("N"), intent.Amount), CancellationToken.None);
        Console.WriteLine($"Enquiry: {status.Status} {status.TransactionId} {status.Reason}");
        if (status.Status is ProviderTransactionStatus.Succeeded or ProviderTransactionStatus.Failed)
        {
            break;
        }
    }
    catch (AirtelApiException ex)
    {
        Console.WriteLine($"Enquiry: {ex.Message}");
    }
}

Console.WriteLine(recordDir is null ? "Set AIRTEL_RECORD_DIR to save these responses." : $"Responses saved in {recordDir}.");
return 0;

/// <summary>Sandbox secrets from environment variables.</summary>
internal sealed class EnvironmentCredentials(string clientId, string clientSecret) : IProviderCredentialSource
{
    public ValueTask<ProviderCredentials> GetAsync(string provider, string providerAccount, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ProviderCredentials(new Dictionary<string, string>
        {
            [AirtelCredentialNames.ClientId] = clientId, [AirtelCredentialNames.ClientSecret] = clientSecret,
        }));
}

/// <summary>Saves each response body, with access tokens redacted, as a numbered fixture file.</summary>
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
