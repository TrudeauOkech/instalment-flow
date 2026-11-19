using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace InstalmentFlow;

/// <summary>How a raw payment reference (for example a paybill account number) is cleaned before matching.</summary>
public enum ReferenceNormalisation
{
    /// <summary>Use the reference exactly as received.</summary>
    Exact = 0,

    /// <summary>Remove leading and trailing whitespace.</summary>
    Trim = 1,

    /// <summary>Trim and convert to upper case (invariant culture).</summary>
    UpperTrim = 2,

    /// <summary>Trim, convert to upper case and remove all internal whitespace, so <c>"ln 100 001"</c> becomes <c>"LN100001"</c>.</summary>
    UpperTrimRemoveSpaces = 3,
}

/// <summary>
/// The format of payment references in a tenant (ADR-009): how raw input is normalised, and the regular
/// expression a normalised reference must match. Matching uses a timeout, so a pathological pattern cannot
/// stall payment processing.
/// </summary>
public sealed class ReferenceFormat : IEquatable<ReferenceFormat>
{
    /// <summary>Maximum time a single match may take.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private readonly Regex _regex;

    /// <summary>Creates a format.</summary>
    /// <param name="pattern">A .NET regular expression the normalised reference must match in full, for example <c>^LN-\d{6}$</c>.</param>
    /// <param name="normalisation">How raw references are normalised before matching.</param>
    /// <exception cref="ArgumentException">The pattern is not a valid regular expression.</exception>
    public ReferenceFormat(string pattern, ReferenceNormalisation normalisation = ReferenceNormalisation.UpperTrimRemoveSpaces)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        if (!Enum.IsDefined(normalisation))
        {
            throw new ArgumentOutOfRangeException(nameof(normalisation));
        }

        _regex = new Regex(pattern, RegexOptions.CultureInvariant, MatchTimeout);
        Pattern = pattern;
        Normalisation = normalisation;
    }

    /// <summary>
    /// The default: 2-32 upper-case letters, digits and dashes, starting with a letter or digit
    /// (for example <c>LN-100001</c>), with <see cref="ReferenceNormalisation.UpperTrimRemoveSpaces"/>.
    /// </summary>
    public static ReferenceFormat Default { get; } = new("^[A-Z0-9][A-Z0-9-]{1,31}$");

    /// <summary>The regular expression a normalised reference must match.</summary>
    public string Pattern { get; }

    /// <summary>The normalisation applied before matching.</summary>
    public ReferenceNormalisation Normalisation { get; }

    /// <summary>Normalises a raw reference without validating it.</summary>
    public string Normalise(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return Normalisation switch
        {
            ReferenceNormalisation.Exact => raw,
            ReferenceNormalisation.Trim => raw.Trim(),
            ReferenceNormalisation.UpperTrim => raw.Trim().ToUpperInvariant(),
            ReferenceNormalisation.UpperTrimRemoveSpaces => string.Concat(raw.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant(),
            _ => throw new InvalidOperationException($"Unknown normalisation {Normalisation}."),
        };
    }

    /// <summary>Normalises <paramref name="raw"/> and reports whether the result is a valid reference.</summary>
    public bool TryNormalise(string? raw, [NotNullWhen(true)] out string? reference)
    {
        reference = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var candidate = Normalise(raw);
        try
        {
            if (!_regex.IsMatch(candidate))
            {
                return false;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }

        reference = candidate;
        return true;
    }

    /// <inheritdoc />
    public bool Equals(ReferenceFormat? other) =>
        other is not null && string.Equals(Pattern, other.Pattern, StringComparison.Ordinal) && Normalisation == other.Normalisation;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ReferenceFormat);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(StringComparer.Ordinal.GetHashCode(Pattern), Normalisation);

    /// <inheritdoc />
    public override string ToString() => $"{Pattern} ({Normalisation})";
}
