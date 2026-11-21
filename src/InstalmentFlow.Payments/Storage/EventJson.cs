using System.Globalization;
using System.Text.Json;

namespace InstalmentFlow.Payments.Storage;

/// <summary>A stable JSON form of <see cref="NormalisedPaymentEvent"/> for stores. Amounts are exact decimal strings, never numbers.</summary>
internal static class EventJson
{
    public static string Serialize(NormalisedPaymentEvent e)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("kind", (int)e.Kind);
            json.WriteString("provider", e.Provider);
            json.WriteString("providerAccount", e.ProviderAccount);
            Optional(json, "transactionId", e.TransactionId);
            if (e.Amount is { } amount)
            {
                json.WriteString("amount", amount.Amount.ToString(CultureInfo.InvariantCulture));
                json.WriteString("currency", amount.Currency.Code);
            }

            Optional(json, "valueAt", e.ValueAt?.ToString("O", CultureInfo.InvariantCulture));
            Optional(json, "accountReference", e.AccountReference);
            Optional(json, "payer", e.Payer);
            Optional(json, "correlationId", e.CorrelationId);
            Optional(json, "merchantReference", e.MerchantReference);
            Optional(json, "reversedTransactionId", e.ReversedTransactionId);
            Optional(json, "reason", e.Reason);
            json.WriteStartObject("metadata");
            foreach (var (key, value) in e.Metadata.OrderBy(m => m.Key, StringComparer.Ordinal))
            {
                json.WriteString(key, value);
            }

            json.WriteEndObject();
            json.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static NormalisedPaymentEvent Deserialize(string text)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var amount = Text("amount") is { } a ? Money.Of(decimal.Parse(a, NumberStyles.Number, CultureInfo.InvariantCulture), Text("currency")!) : null;
        var valueAt = Text("valueAt") is { } v ? DateTimeOffset.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : (DateTimeOffset?)null;
        var metadata = root.TryGetProperty("metadata", out var m)
            ? m.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty, StringComparer.Ordinal)
            : null;
        return NormalisedPaymentEvent.Restore(
            (PaymentEventKind)root.GetProperty("kind").GetInt32(), Text("provider")!, Text("providerAccount")!, Text("transactionId"), amount, valueAt,
            Text("accountReference"), Text("payer"), Text("correlationId"), Text("merchantReference"), Text("reversedTransactionId"), Text("reason"), metadata);
    }

    private static void Optional(Utf8JsonWriter json, string name, string? value)
    {
        if (value is not null)
        {
            json.WriteString(name, value);
        }
    }
}
