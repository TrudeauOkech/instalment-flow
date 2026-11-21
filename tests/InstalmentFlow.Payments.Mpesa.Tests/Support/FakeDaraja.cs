using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace InstalmentFlow.Payments.Mpesa.Tests;

/// <summary>Contract fixtures (see Fixtures/README.md for where each shape comes from).</summary>
internal static class Fixture
{
    public static string Text(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static byte[] Bytes(string name) => Encoding.UTF8.GetBytes(Text(name));
}

/// <summary>A request the fake received.</summary>
internal sealed record DarajaRequest(HttpMethod Method, string PathAndQuery, string? Authorization, JsonElement Body);

/// <summary>
/// An in-process Daraja: answers each path with the responses queued for it (the last one repeats), and records every
/// request. A token is issued per OAuth call, numbered, so tests can tell a cached token from a fresh one.
/// </summary>
internal sealed class FakeDaraja : HttpMessageHandler
{
    private readonly Dictionary<string, Queue<(HttpStatusCode Status, string Body)>> _routes = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private int _tokens;

    public List<DarajaRequest> Requests { get; } = [];

    public int TokensIssued => _tokens;

    public FakeDaraja On(string path, string fixtureOrJson, HttpStatusCode status = HttpStatusCode.OK)
    {
        var body = fixtureOrJson.EndsWith(".json", StringComparison.Ordinal) ? Fixture.Text(fixtureOrJson) : fixtureOrJson;
        lock (_lock)
        {
            if (!_routes.TryGetValue(path, out var queue))
            {
                _routes[path] = queue = new Queue<(HttpStatusCode, string)>();
            }

            queue.Enqueue((status, body));
        }

        return this;
    }

    public IReadOnlyList<DarajaRequest> To(string path) => [.. Requests.Where(r => r.PathAndQuery.StartsWith(path, StringComparison.Ordinal))];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var text = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var path = request.RequestUri!.PathAndQuery;
        (HttpStatusCode Status, string Body) answer;
        lock (_lock)
        {
            Requests.Add(new DarajaRequest(request.Method, path, request.Headers.Authorization?.ToString(),
                string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone()));
            if (path.StartsWith("/oauth/v1/generate", StringComparison.Ordinal) && !_routes.ContainsKey("/oauth/v1/generate"))
            {
                var n = Interlocked.Increment(ref _tokens);
                answer = (HttpStatusCode.OK, $$"""{"access_token":"token-{{n}}","expires_in":"3599"}""");
            }
            else
            {
                var key = _routes.Keys.Where(k => path.StartsWith(k, StringComparison.Ordinal)).MaxBy(k => k.Length)
                    ?? throw new InvalidOperationException($"FakeDaraja has no answer for {path}.");
                var queue = _routes[key];
                answer = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            }
        }

        return new HttpResponseMessage(answer.Status) { Content = new StringContent(answer.Body, Encoding.UTF8, "application/json") };
    }
}

/// <summary>Secrets for every shortcode, with a self-signed certificate standing in for Safaricom's.</summary>
internal sealed class TestCredentials : IProviderCredentialSource, IDisposable
{
    public const string ConsumerKey = "consumer-key";
    public const string ConsumerSecret = "consumer-secret";
    public const string Passkey = "bfb279f9aa9bdbcf158e97dd71a467cd2e0c893059b10f78e6b72ada1ed2c919";
    public const string InitiatorPassword = "Safaricom999!*!";

    public TestCredentials()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Test Daraja", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Certificate = request.CreateSelfSigned(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddYears(100));
    }

    public X509Certificate2 Certificate { get; }

    public int Lookups { get; private set; }

    public ValueTask<ProviderCredentials> GetAsync(string provider, string providerAccount, CancellationToken cancellationToken)
    {
        Lookups++;
        return ValueTask.FromResult(new ProviderCredentials(new Dictionary<string, string>
        {
            [MpesaCredentialNames.ConsumerKey] = ConsumerKey,
            [MpesaCredentialNames.ConsumerSecret] = ConsumerSecret,
            [MpesaCredentialNames.Passkey] = Passkey,
            [MpesaCredentialNames.InitiatorName] = "testapi",
            [MpesaCredentialNames.InitiatorPassword] = InitiatorPassword,
            [MpesaCredentialNames.Certificate] = Certificate.ExportCertificatePem(),
        }));
    }

    public void Dispose() => Certificate.Dispose();
}

internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>An adapter wired to a fake Daraja, for shortcode 600123.</summary>
internal sealed class DarajaHarness : IDisposable
{
    public const string ShortCode = "600123";

    private readonly HttpClient _http;

    public DarajaHarness(Action<MpesaOptions>? configure = null)
    {
        Daraja = new FakeDaraja();
        _http = new HttpClient(Daraja);
        Clock = new ManualClock(new DateTimeOffset(2026, 3, 2, 6, 0, 0, TimeSpan.Zero));
        Credentials = new TestCredentials();
        var options = new MpesaOptions { BaseAddress = new Uri("https://daraja.test") };
        options.Accounts.Add(new MpesaAccount
        {
            ShortCode = ShortCode,
            StkCallbackUrl = new Uri("https://pay.example.test/hooks/s3cret/mpesa/stk/600123"),
            ResultUrl = new Uri("https://pay.example.test/hooks/s3cret/mpesa/transaction-status/600123"),
            QueueTimeoutUrl = new Uri("https://pay.example.test/hooks/s3cret/mpesa/transaction-status-timeout/600123"),
        });
        configure?.Invoke(options);
        Adapter = new MpesaAdapter(_http, Credentials, options, Clock);
    }

    public FakeDaraja Daraja { get; }

    public ManualClock Clock { get; }

    public TestCredentials Credentials { get; }

    public MpesaAdapter Adapter { get; }

    public static CollectionRequest Collection(decimal amount = 400m, string payer = "0708374149", string reference = "LN-1") => new()
    {
        ProviderAccount = ShortCode, Payer = payer, Amount = Money.Kes(amount), AccountReference = reference, Description = "Loan repayment", MerchantReference = "ignored",
    };

    public void Dispose()
    {
        _http.Dispose();
        Daraja.Dispose();
        Credentials.Dispose();
    }
}
