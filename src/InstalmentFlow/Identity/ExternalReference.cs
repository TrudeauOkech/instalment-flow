namespace InstalmentFlow;

/// <summary>
/// Identifies a payment in the system it came from, for example <c>("mpesa:600123", "TJ3K9XYZ")</c>.
/// The source includes the provider account (shortcode or till) because receipt numbers are unique per
/// provider, not per business. Comparison is ordinal (case-sensitive).
/// </summary>
public sealed record ExternalReference
{
    /// <summary>Maximum length of <see cref="Source"/>.</summary>
    public const int MaxSourceLength = 64;

    /// <summary>Maximum length of <see cref="Value"/>.</summary>
    public const int MaxValueLength = 128;

    /// <summary>Creates a reference. Leading and trailing whitespace is not allowed.</summary>
    /// <exception cref="ArgumentException">A part is empty, too long, padded with whitespace or contains control characters.</exception>
    public ExternalReference(string source, string value)
    {
        Source = Validate(source, MaxSourceLength, nameof(source));
        Value = Validate(value, MaxValueLength, nameof(value));
    }

    /// <summary>The originating system and account, for example <c>mpesa:600123</c>.</summary>
    public string Source { get; }

    /// <summary>The identifier within that source, for example an M-Pesa receipt number.</summary>
    public string Value { get; }

    /// <summary>Formats as <c>source/value</c>.</summary>
    public override string ToString() => $"{Source}/{Value}";

    private static string Validate(string part, int maxLength, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(part, name);
        if (part.Length > maxLength)
        {
            throw new ArgumentException($"Must be at most {maxLength} characters.", name);
        }

        if (part.Trim().Length != part.Length)
        {
            throw new ArgumentException("Must not start or end with whitespace.", name);
        }

        if (part.Any(char.IsControl))
        {
            throw new ArgumentException("Must not contain control characters.", name);
        }

        return part;
    }
}
