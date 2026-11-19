using System.Text.RegularExpressions;

namespace InstalmentFlow;

/// <summary>
/// Identifies a tenant (a lender or other business whose data is isolated from every other tenant; ADR-007).
/// One to 64 characters: letters, digits, '.', '_' and '-', starting with a letter or digit. Compared ordinally.
/// </summary>
public sealed partial record TenantId
{
    /// <summary>Maximum length of a tenant ID.</summary>
    public const int MaxLength = 64;

    /// <summary>Creates a tenant ID.</summary>
    /// <exception cref="ArgumentException">The value is not a valid tenant ID.</exception>
    public TenantId(string value)
    {
        if (value is null || !Pattern().IsMatch(value))
        {
            throw new ArgumentException(
                "Tenant IDs are 1-64 characters of letters, digits, '.', '_' or '-', starting with a letter or digit.", nameof(value));
        }

        Value = value;
    }

    /// <summary>The tenant ID.</summary>
    public string Value { get; }

    /// <summary>Creates a tenant ID from a string.</summary>
    public static TenantId FromString(string value) => new(value);

    /// <summary>Creates a tenant ID from a string.</summary>
    public static implicit operator TenantId(string value) => new(value);

    /// <summary>Returns the tenant ID.</summary>
    public override string ToString() => Value;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
