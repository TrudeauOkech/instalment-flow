namespace InstalmentFlow;

/// <summary>
/// A caller-supplied key that makes a command safe to retry: the same key with the same request returns the
/// original result, and the same key with a different request is rejected (<c>IF1002</c>). Keys are unique per
/// tenant and compared ordinally.
/// </summary>
public sealed record IdempotencyKey
{
    /// <summary>Maximum key length.</summary>
    public const int MaxLength = 128;

    /// <summary>Creates a key.</summary>
    /// <exception cref="ArgumentException">The key is empty, too long, padded with whitespace or contains control characters.</exception>
    public IdempotencyKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > MaxLength)
        {
            throw new ArgumentException($"Idempotency keys must be at most {MaxLength} characters.", nameof(value));
        }

        if (value.Trim().Length != value.Length || value.Any(char.IsControl))
        {
            throw new ArgumentException("Idempotency keys must not have surrounding whitespace or control characters.", nameof(value));
        }

        Value = value;
    }

    /// <summary>The key.</summary>
    public string Value { get; }

    /// <summary>Returns the key.</summary>
    public override string ToString() => Value;
}
