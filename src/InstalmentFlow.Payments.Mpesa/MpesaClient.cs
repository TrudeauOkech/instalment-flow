using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace InstalmentFlow.Payments.Mpesa;

/// <summary>A Daraja response: its status and JSON body (empty when the body was not JSON).</summary>
internal sealed record MpesaResponse(HttpStatusCode Status, JsonElement Body)
{
    public bool IsSuccess => (int)Status is >= 200 and < 300;

    public string? ErrorCode => MpesaJson.Text(Body, "errorCode");

    public string? ErrorMessage => MpesaJson.Text(Body, "errorMessage");

    public MpesaApiException Error() => new((int)Status, ErrorCode, ErrorMessage ?? MpesaJson.Text(Body, "ResponseDescription") ?? Status.ToString());
}

/// <summary>
/// Calls Daraja with an OAuth token per consumer key, cached until shortly before it expires. A request refused for
/// an expired or invalid token is retried once with a fresh token.
/// </summary>
internal sealed class MpesaClient(HttpClient http, IProviderCredentialSource credentials, MpesaOptions options, TimeProvider time)
{
    public const string Provider = "mpesa";
    private const string ExpiredTokenCode = "404.001.03";
    private static readonly TimeSpan RefreshEarly = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, Token> _tokens = new(StringComparer.Ordinal);

    public async Task<ProviderCredentials> CredentialsAsync(string shortCode, CancellationToken ct) =>
        await credentials.GetAsync(Provider, shortCode, ct).ConfigureAwait(false);

    public async Task<MpesaResponse> PostAsync(string shortCode, string path, object body, CancellationToken ct)
    {
        var secrets = await CredentialsAsync(shortCode, ct).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(body);
        for (var attempt = 0; ; attempt++)
        {
            var token = await TokenAsync(secrets, attempt > 0, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.Address, path))
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await SendAsync(request, ct).ConfigureAwait(false);
            var expired = response.Status == HttpStatusCode.Unauthorized ||
                          (response.Status == HttpStatusCode.NotFound && response.ErrorCode == ExpiredTokenCode);
            if (!expired || attempt > 0)
            {
                return response;
            }
        }
    }

    private async Task<string> TokenAsync(ProviderCredentials secrets, bool refresh, CancellationToken ct)
    {
        var key = secrets.Require(MpesaCredentialNames.ConsumerKey);
        if (!refresh && _tokens.TryGetValue(key, out var cached) && cached.ValidUntil > time.GetUtcNow())
        {
            return cached.Value;
        }

        // No lock: two callers refreshing at once simply fetch two tokens, both valid.
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{key}:{secrets.Require(MpesaCredentialNames.ConsumerSecret)}"));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(options.Address, "/oauth/v1/generate?grant_type=client_credentials"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        var response = await SendAsync(request, ct).ConfigureAwait(false);
        var token = response.IsSuccess ? MpesaJson.Text(response.Body, "access_token") : null;
        if (string.IsNullOrEmpty(token))
        {
            throw response.IsSuccess ? new MpesaApiException((int)response.Status, null, "The token response has no access_token.") : response.Error();
        }

        var seconds = MpesaJson.Decimal(MpesaJson.Text(response.Body, "expires_in")) ?? 3599m;
        var lifetime = TimeSpan.FromSeconds((long)seconds);
        _tokens[key] = new Token(token, time.GetUtcNow() + (lifetime > RefreshEarly * 2 ? lifetime - RefreshEarly : lifetime / 2));
        return token;
    }

    private async Task<MpesaResponse> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonElement body;
        try
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                body = default;
            }
            else
            {
                using var document = JsonDocument.Parse(text);
                body = document.RootElement.Clone();
            }
        }
        catch (JsonException)
        {
            body = default;
        }

        return new MpesaResponse(response.StatusCode, body);
    }

    private sealed record Token(string Value, DateTimeOffset ValidUntil);
}
