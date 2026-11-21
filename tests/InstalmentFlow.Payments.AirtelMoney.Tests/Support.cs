using System.Net;
using System.Text;
using System.Text.Json;

namespace InstalmentFlow.Payments.AirtelMoney.Tests;

internal static class Fixture
{
    public static string Text(string name, string reference = "ref-1") =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)).Replace("{ref}", reference, StringComparison.Ordinal);

    public static byte[] Bytes(string name, string reference = "ref-1") => Encoding.UTF8.GetBytes(Text(name, reference));
}

internal sealed record AirtelRequest(HttpMethod Method, string Path, IReadOnlyDictionary<string, string> Headers, JsonElement Body);

/// <summary>
/// An in-process Airtel Open API: answers each path prefix with its queued responses (the last repeats), issues a
/// numbered token per OAuth call, and records every request with its headers.
/// </summary>
internal sealed class FakeAirtel : HttpMessageHandler
{
    private readonly Dictionary<string, Queue<(HttpStatusCode Status, string Body)>> _routes = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private int _tokens;

    public string ExpiresIn { get; set; } = "180";

    public List<AirtelRequest> Requests { get; } = [];

    public int TokensIssued => _tokens;

    public Func<HttpRequestMessage, Exception?>? Fail { get; set; }

    public FakeAirtel On(string path, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
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

    public IReadOnlyList<AirtelRequest> To(string path) => [.. Requests.Where(r => r.Path.StartsWith(path, StringComparison.Ordinal))];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var text = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var path = request.RequestUri!.AbsolutePath;
        (HttpStatusCode Status, string Body) answer;
        lock (_lock)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            Requests.Add(new AirtelRequest(request.Method, path, headers, string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone()));
            if (Fail?.Invoke(request) is { } failure)
            {
                throw failure;
            }

            if (path == "/auth/oauth2/token")
            {
                var n = Interlocked.Increment(ref _tokens);
                answer = (HttpStatusCode.OK, $$"""{"access_token":"token-{{n}}","expires_in":{{ExpiresIn}},"token_type":"bearer"}""");
            }
            else
            {
                var key = _routes.Keys.Where(k => path.StartsWith(k, StringComparison.Ordinal)).MaxBy(k => k.Length)
                    ?? throw new InvalidOperationException($"FakeAirtel has no answer for {path}.");
                var queue = _routes[key];
                answer = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            }
        }

        return new HttpResponseMessage(answer.Status) { Content = new StringContent(answer.Body, Encoding.UTF8, "application/json") };
    }
}

internal sealed class TestCredentials : IProviderCredentialSource
{
    public const string ClientId = "client-id";
    public const string ClientSecret = "client-secret";

    public ValueTask<ProviderCredentials> GetAsync(string provider, string providerAccount, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ProviderCredentials(new Dictionary<string, string>
        {
            [AirtelCredentialNames.ClientId] = ClientId, [AirtelCredentialNames.ClientSecret] = ClientSecret,
        }));
}

internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>An adapter for account <c>acme-ke-app</c> (Kenya, KES) on a fake Open API.</summary>
internal sealed class AirtelHarness : IDisposable
{
    public const string Account = "acme-ke-app";
    public const string Collection = "/merchant/v2/payments/";
    public const string Enquiry = "/standard/v1/payments/";

    private readonly HttpClient _http;

    public AirtelHarness(Action<AirtelOptions>? configure = null)
    {
        Api = new FakeAirtel();
        _http = new HttpClient(Api);
        Clock = new ManualClock(new DateTimeOffset(2026, 3, 2, 6, 0, 0, TimeSpan.Zero));
        var options = new AirtelOptions { BaseAddress = new Uri("https://airtel.test") };
        options.Accounts.Add(new AirtelAccount { Account = Account });
        configure?.Invoke(options);
        Adapter = new AirtelAdapter(_http, new TestCredentials(), options, Clock);
    }

    public FakeAirtel Api { get; }

    public ManualClock Clock { get; }

    public AirtelAdapter Adapter { get; }

    public static CollectionRequest Push(decimal amount = 400m, string payer = "0733123456", string reference = "ref-1") => new()
    {
        ProviderAccount = Account, Payer = payer, Amount = Money.Kes(amount), AccountReference = "LN-1", Description = "Loan repayment", MerchantReference = reference,
    };

    public static ProviderCallback Callback(string fixture, string reference = "ref-1", string? account = Account) =>
        new("airtel", AirtelAdapter.CollectionResult, Fixture.Bytes(fixture, reference), providerAccount: account);

    public void Dispose()
    {
        _http.Dispose();
        Api.Dispose();
    }
}
