using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InstalmentFlow.EntityFrameworkCore.Persistence;

/// <summary>
/// JSON for jsonb columns: tenant policies, stored command results (idempotency replay), metadata, refund portions
/// and outbox payloads. Money is an exact decimal string plus a currency code; enums are names, so stored data
/// survives renumbering mistakes.
/// </summary>
internal static class Json
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidOperationException($"Stored JSON for {typeof(T).Name} is empty.");

    public static object Deserialize(string json, Type type) =>
        JsonSerializer.Deserialize(json, type, Options) ?? throw new InvalidOperationException($"Stored JSON for {type.Name} is empty.");

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.General)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.Strict,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new MoneyConverter());
        options.Converters.Add(new StringConverter<Currency>(c => c.Code, Currency.FromCode));
        options.Converters.Add(new StringConverter<TenantId>(t => t.Value, s => new TenantId(s)));
        options.Converters.Add(new StringConverter<StrategyRef>(s => s.ToString(), ParseStrategy));
        options.Converters.Add(new StringConverter<ComponentKey>(Format, ParseComponent));
        options.Converters.Add(new GracePeriodConverter());
        options.Converters.Add(new ChargeDueConverter());
        options.Converters.Add(new ReferenceFormatConverter());
        options.Converters.Add(new ExternalReferenceConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private static string Format(ComponentKey key) => key.ChargeCode.Length == 0 ? key.Type.ToString() : $"{key.Type}:{key.ChargeCode}";

    private static ComponentKey ParseComponent(string text)
    {
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        return colon < 0
            ? new ComponentKey(Enum.Parse<ComponentType>(text))
            : new ComponentKey(Enum.Parse<ComponentType>(text[..colon]), text[(colon + 1)..]);
    }

    private static StrategyRef ParseStrategy(string text)
    {
        var at = text.LastIndexOf('@');
        return new StrategyRef(text[..at], int.Parse(text[(at + 1)..], NumberStyles.None, CultureInfo.InvariantCulture));
    }

    private sealed class StringConverter<T>(Func<T, string> write, Func<string, T> read) : JsonConverter<T>
        where T : class
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            read(reader.GetString() ?? throw new JsonException($"A {typeof(T).Name} cannot be null here."));

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(write(value));
    }

    private sealed class MoneyConverter : JsonConverter<Money>
    {
        public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var text = reader.GetString() ?? throw new JsonException("Money cannot be null here.");
            var space = text.IndexOf(' ', StringComparison.Ordinal);
            return new Money(decimal.Parse(text[(space + 1)..], NumberStyles.Number, CultureInfo.InvariantCulture), Currency.FromCode(text[..space]));
        }

        // "KES 1500.00": currency code and the exact amount, never a floating-point number.
        public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options) =>
            writer.WriteStringValue($"{value.Currency.Code} {value.Amount.ToString(CultureInfo.InvariantCulture)}");
    }

    private sealed class GracePeriodConverter : JsonConverter<GracePeriod>
    {
        public override GracePeriod Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => GracePeriod.OfDays(reader.GetInt32());

        public override void Write(Utf8JsonWriter writer, GracePeriod value, JsonSerializerOptions options) => writer.WriteNumberValue(value.Days);
    }

    private sealed class ChargeDueConverter : JsonConverter<ChargeDue>
    {
        public override ChargeDue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var text = reader.GetString() ?? throw new JsonException("A charge due policy cannot be null here.");
            return text switch
            {
                nameof(ChargeDueKind.PerTenantPolicy) => ChargeDue.PerTenantPolicy,
                nameof(ChargeDueKind.NextUnpaidInstalment) => ChargeDue.NextUnpaidInstalment,
                nameof(ChargeDueKind.Immediately) => ChargeDue.Immediately,
                _ when text.StartsWith(nameof(ChargeDueKind.OnInstalment) + ":", StringComparison.Ordinal) =>
                    ChargeDue.OnInstalment(int.Parse(text[(nameof(ChargeDueKind.OnInstalment).Length + 1)..], NumberStyles.None, CultureInfo.InvariantCulture)),
                _ => throw new JsonException($"Unknown charge due policy '{text}'."),
            };
        }

        public override void Write(Utf8JsonWriter writer, ChargeDue value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Sequence is { } s ? $"{value.Kind}:{s.ToString(CultureInfo.InvariantCulture)}" : value.Kind.ToString());
    }

    private sealed class ReferenceFormatConverter : JsonConverter<ReferenceFormat>
    {
        public override ReferenceFormat Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = JsonSerializer.Deserialize<Stored>(ref reader, options) ?? throw new JsonException("A reference format cannot be null here.");
            return new ReferenceFormat(value.Pattern, value.Normalisation);
        }

        public override void Write(Utf8JsonWriter writer, ReferenceFormat value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, new Stored(value.Pattern, value.Normalisation), options);

        private sealed record Stored(string Pattern, ReferenceNormalisation Normalisation);
    }

    private sealed class ExternalReferenceConverter : JsonConverter<ExternalReference>
    {
        public override ExternalReference Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = JsonSerializer.Deserialize<Stored>(ref reader, options) ?? throw new JsonException("An external reference cannot be null here.");
            return new ExternalReference(value.Source, value.Value);
        }

        public override void Write(Utf8JsonWriter writer, ExternalReference value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, new Stored(value.Source, value.Value), options);

        private sealed record Stored(string Source, string Value);
    }
}
