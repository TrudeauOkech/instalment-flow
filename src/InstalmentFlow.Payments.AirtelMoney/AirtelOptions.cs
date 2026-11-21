using System.Globalization;
using System.Text.Json;

namespace InstalmentFlow.Payments.AirtelMoney;

/// <summary>Which Airtel Open API environment to call.</summary>
public enum AirtelEnvironment
{
    /// <summary><c>https://openapiuat.airtel.africa</c>.</summary>
    Sandbox = 0,

    /// <summary><c>https://openapi.airtel.africa</c>.</summary>
    Production = 1,
}

/// <summary>How Airtel expects the subscriber's number. Sources disagree; confirm against the sandbox for your country.</summary>
public enum AirtelMsisdnFormat
{
    /// <summary>Without the country code or a leading zero, for example <c>733123456</c> (the most common in sources for Kenya).</summary>
    National = 0,

    /// <summary>With a leading zero, for example <c>0733123456</c>.</summary>
    NationalWithZero = 1,

    /// <summary>With the country code, for example <c>254733123456</c>.</summary>
    International = 2,
}

/// <summary>One Airtel merchant application the adapter collects into.</summary>
public sealed record AirtelAccount
{
    /// <summary>
    /// The name of the merchant account in InstalmentFlow (the payment source is <c>airtel:{Account}</c>), for example the
    /// Airtel application or merchant code. Airtel's callbacks do not name it, so the callback URL carries it.
    /// </summary>
    public required string Account { get; init; }

    /// <summary>The ISO country code sent as <c>X-Country</c>, for example <c>KE</c>.</summary>
    public string Country { get; init; } = "KE";

    /// <summary>The currency sent as <c>X-Currency</c>.</summary>
    public Currency Currency { get; init; } = Currency.KES;
}

/// <summary>Settings for the Airtel Money adapter.</summary>
public sealed class AirtelOptions
{
    /// <summary>Sandbox or production.</summary>
    public AirtelEnvironment Environment { get; set; } = AirtelEnvironment.Sandbox;

    /// <summary>Overrides the environment's address, for tests, a proxy or a country-specific host.</summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>The merchant accounts the adapter serves.</summary>
    public IList<AirtelAccount> Accounts { get; } = [];

    /// <summary>The USSD push path. Sources disagree between v1 and v2; default v2.</summary>
    public string CollectionPath { get; set; } = "/merchant/v2/payments/";

    /// <summary>The transaction enquiry path; the merchant reference is appended.</summary>
    public string EnquiryPath { get; set; } = "/standard/v1/payments/";

    /// <summary>How the subscriber's number is sent.</summary>
    public AirtelMsisdnFormat MsisdnFormat { get; set; } = AirtelMsisdnFormat.National;

    /// <summary>Whether amounts with minor units may be requested. Unknown per country; off, so only whole units are sent.</summary>
    public bool AllowFractionalAmounts { get; set; }

    /// <summary>Callbacks larger than this are rejected unread. Default 16 KiB.</summary>
    public int MaxCallbackBytes { get; set; } = 16 * 1024;

    internal Uri Address => BaseAddress ?? new Uri(Environment == AirtelEnvironment.Production ? "https://openapi.airtel.africa" : "https://openapiuat.airtel.africa");
}

/// <summary>The secret names the adapter asks <see cref="IProviderCredentialSource"/> for, per merchant account.</summary>
public static class AirtelCredentialNames
{
    /// <summary>The Airtel application's client ID.</summary>
    public const string ClientId = "ClientId";

    /// <summary>The Airtel application's client secret.</summary>
    public const string ClientSecret = "ClientSecret";
}

/// <summary>The Airtel Open API refused a request.</summary>
public sealed class AirtelApiException : Exception
{
    /// <summary>Creates the exception.</summary>
    public AirtelApiException(int statusCode, string? resultCode, string message)
        : base($"Airtel answered {statusCode}{(resultCode is null ? string.Empty : $" ({resultCode})")}: {message}")
    {
        StatusCode = statusCode;
        ResultCode = resultCode;
    }

    /// <summary>The HTTP status.</summary>
    public int StatusCode { get; }

    /// <summary>Airtel's <c>result_code</c> or <c>response_code</c>, for example <c>DP00800001001</c>.</summary>
    public string? ResultCode { get; }
}

/// <summary>Reads Airtel JSON tolerantly: strings or numbers, wrapped in <c>data</c> or not.</summary>
internal static class AirtelJson
{
    public static JsonElement? Child(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    public static string? Text(JsonElement? element, string name) =>
        element is { } e && Child(e, name) is { } value
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            }
            : null;

    /// <summary>The <c>transaction</c> object, at <c>data.transaction</c> or at the top level.</summary>
    public static JsonElement? Transaction(JsonElement root) =>
        (Child(root, "data") is { } data ? Child(data, "transaction") : null) ?? Child(root, "transaction");

    public static decimal? Decimal(string? text) =>
        text is not null && decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;
}

/// <summary>Subscriber numbers in the form Airtel expects.</summary>
internal static class AirtelMsisdn
{
    private static readonly Dictionary<string, string> DialCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["KE"] = "254", ["UG"] = "256", ["TZ"] = "255", ["RW"] = "250", ["ZM"] = "260", ["MW"] = "265", ["CD"] = "243", ["CG"] = "242",
        ["MG"] = "261", ["NE"] = "227", ["TD"] = "235", ["GA"] = "241", ["NG"] = "234", ["SC"] = "248",
    };

    public static string? Format(string number, string country, AirtelMsisdnFormat format)
    {
        var digits = new string([.. number.Where(char.IsDigit)]);
        if (!DialCodes.TryGetValue(country, out var code))
        {
            return digits.Length >= 7 ? digits : null;
        }

        var national = digits.StartsWith(code, StringComparison.Ordinal) && digits.Length > code.Length + 7 ? digits[code.Length..] : digits;
        national = national.TrimStart('0');
        if (national.Length is < 7 or > 10)
        {
            return null;
        }

        return format switch
        {
            AirtelMsisdnFormat.NationalWithZero => "0" + national,
            AirtelMsisdnFormat.International => code + national,
            _ => national,
        };
    }
}
