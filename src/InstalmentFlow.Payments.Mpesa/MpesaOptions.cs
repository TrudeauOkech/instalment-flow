using System.Globalization;
using System.Text.Json;

namespace InstalmentFlow.Payments.Mpesa;

/// <summary>Which Daraja environment to call.</summary>
public enum MpesaEnvironment
{
    /// <summary><c>https://sandbox.safaricom.co.ke</c>.</summary>
    Sandbox = 0,

    /// <summary><c>https://api.safaricom.co.ke</c>.</summary>
    Production = 1,
}

/// <summary>How customers pay a shortcode, which sets the STK transaction type.</summary>
public enum MpesaCollectionType
{
    /// <summary>A paybill: <c>CustomerPayBillOnline</c>.</summary>
    PayBill = 0,

    /// <summary>A till (Buy Goods): <c>CustomerBuyGoodsOnline</c>.</summary>
    BuyGoods = 1,
}

/// <summary>One shortcode the adapter serves.</summary>
public sealed record MpesaAccount
{
    /// <summary>The shortcode (paybill or store number), for example <c>600123</c>. The payment source is <c>mpesa:{ShortCode}</c>.</summary>
    public required string ShortCode { get; init; }

    /// <summary>Paybill or till.</summary>
    public MpesaCollectionType CollectionType { get; init; } = MpesaCollectionType.PayBill;

    /// <summary>The STK <c>PartyB</c>, for a till whose number differs from its store number; by default the shortcode.</summary>
    public string? PartyB { get; init; }

    /// <summary>Where STK results are sent. Include the shortcode in the path: STK results do not name it.</summary>
    public Uri? StkCallbackUrl { get; init; }

    /// <summary>Where Transaction Status results are sent.</summary>
    public Uri? ResultUrl { get; init; }

    /// <summary>Where Transaction Status time-outs are sent.</summary>
    public Uri? QueueTimeoutUrl { get; init; }
}

/// <summary>Settings for the M-Pesa adapter.</summary>
public sealed class MpesaOptions
{
    /// <summary>Sandbox or production.</summary>
    public MpesaEnvironment Environment { get; set; } = MpesaEnvironment.Sandbox;

    /// <summary>Overrides the environment's address, for tests or a proxy.</summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>The shortcodes the adapter serves.</summary>
    public IList<MpesaAccount> Accounts { get; } = [];

    /// <summary>The C2B registration path; v2 is current, and the sandbox may still need <c>/mpesa/c2b/v1/registerurl</c>.</summary>
    public string C2BRegisterPath { get; set; } = "/mpesa/c2b/v2/registerurl";

    /// <summary>The longest STK account reference Daraja accepts; longer ones are refused, never truncated. Default 12.</summary>
    public int AccountReferenceMaxLength { get; set; } = 12;

    /// <summary>Callbacks larger than this are rejected unread. Default 64 KiB.</summary>
    public int MaxCallbackBytes { get; set; } = 64 * 1024;

    internal Uri Address => BaseAddress ?? new Uri(Environment == MpesaEnvironment.Production ? "https://api.safaricom.co.ke" : "https://sandbox.safaricom.co.ke");
}

/// <summary>The secret names the adapter asks <see cref="IProviderCredentialSource"/> for, per shortcode.</summary>
public static class MpesaCredentialNames
{
    /// <summary>The Daraja app's consumer key.</summary>
    public const string ConsumerKey = "ConsumerKey";

    /// <summary>The Daraja app's consumer secret.</summary>
    public const string ConsumerSecret = "ConsumerSecret";

    /// <summary>The Lipa na M-Pesa Online passkey, for STK.</summary>
    public const string Passkey = "Passkey";

    /// <summary>The initiator (API operator) name, for Transaction Status.</summary>
    public const string InitiatorName = "InitiatorName";

    /// <summary>The initiator's password, encrypted into the security credential.</summary>
    public const string InitiatorPassword = "InitiatorPassword";

    /// <summary>Safaricom's public certificate (PEM) for the environment, used to encrypt the initiator password.</summary>
    public const string Certificate = "Certificate";
}

/// <summary>Daraja refused a request.</summary>
public sealed class MpesaApiException : Exception
{
    /// <summary>Creates the exception.</summary>
    public MpesaApiException(int statusCode, string? errorCode, string message)
        : base($"Daraja answered {statusCode}{(errorCode is null ? string.Empty : $" ({errorCode})")}: {message}")
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    /// <summary>The HTTP status.</summary>
    public int StatusCode { get; }

    /// <summary>Daraja's <c>errorCode</c>, for example <c>400.002.02</c>.</summary>
    public string? ErrorCode { get; }
}

/// <summary>Kenya's time (East Africa Time, UTC+3 all year) in Daraja's formats.</summary>
internal static class MpesaTime
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(3);

    /// <summary><c>yyyyMMddHHmmss</c> in Nairobi time, as Daraja sends <c>TransTime</c> and <c>TransactionDate</c>.</summary>
    public static DateTimeOffset? ParseCompact(string? value) =>
        value is not null && DateTimeOffset.TryParseExact(value.Trim(), "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? new DateTimeOffset(parsed.Ticks, Offset)
            : null;

    public static string FormatCompact(DateTimeOffset instant) => instant.ToOffset(Offset).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    public static string FormatSpaced(DateTimeOffset instant) => instant.ToOffset(Offset).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Pull Transactions dates: with an offset (<c>+0300</c> or <c>+03:00</c>), or without one in Nairobi time.</summary>
    public static DateTimeOffset? ParseFlexible(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (text.Length >= 5 && (text[^5] == '+' || text[^5] == '-') && char.IsDigit(text[^1]) && !text.EndsWith(":00", StringComparison.Ordinal))
        {
            text = text[..^2] + ":" + text[^2..];
        }

        string[] withOffset = ["yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd HH:mm:sszzz"];
        if (DateTimeOffset.TryParseExact(text, withOffset, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
        {
            return exact;
        }

        string[] local = ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss", "yyyyMMddHHmmss"];
        return DateTimeOffset.TryParseExact(text, local, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var wall)
            ? new DateTimeOffset(wall.Ticks, Offset)
            : null;
    }
}

/// <summary>Reads Daraja JSON, which sends the same field as a string in one API and a number in another.</summary>
internal static class MpesaJson
{
    public static string? Text(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(property.Name.Replace(" ", string.Empty, StringComparison.Ordinal), name, StringComparison.OrdinalIgnoreCase))
            {
                return Scalar(property.Value);
            }
        }

        return null;
    }

    public static string? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    public static decimal? Decimal(string? text) =>
        text is not null && decimal.TryParse(text.Trim(), NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var value) ? value : null;

    public static JsonElement? Child(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) is { Value.ValueKind: not JsonValueKind.Undefined } property
            ? property.Value
            : null;

    /// <summary>Daraja's <c>{Key, Value}</c> or <c>{Name, Value}</c> lists, sent as an array or, for one item, as an object.</summary>
    public static Dictionary<string, string?> Pairs(JsonElement? container)
    {
        var pairs = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (container is not { } c)
        {
            return pairs;
        }

        IEnumerable<JsonElement> items = c.ValueKind == JsonValueKind.Array ? c.EnumerateArray() : [c];
        foreach (var item in items)
        {
            var key = Text(item, "Key") ?? Text(item, "Name");
            if (key is not null && Child(item, "Value") is { } value)
            {
                pairs[key] = Scalar(value);
            }
        }

        return pairs;
    }
}
