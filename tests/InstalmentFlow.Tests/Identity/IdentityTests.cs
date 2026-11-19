namespace InstalmentFlow.Tests;

public sealed class IdentityTests
{
    [Fact]
    public void External_references_compare_ordinally_on_source_and_value()
    {
        var a = new ExternalReference("mpesa:600123", "TJ3K9XYZ");

        Assert.Equal(a, new ExternalReference("mpesa:600123", "TJ3K9XYZ"));
        Assert.NotEqual(a, new ExternalReference("mpesa:600123", "tj3k9xyz"));
        Assert.NotEqual(a, new ExternalReference("mpesa:600999", "TJ3K9XYZ"));
        Assert.Equal("mpesa:600123/TJ3K9XYZ", a.ToString());
    }

    [Theory]
    [InlineData("", "X")]
    [InlineData("mpesa", "")]
    [InlineData(" mpesa", "X")]
    [InlineData("mpesa", "X ")]
    [InlineData("mpesa", "X\nY")]
    public void External_references_reject_ambiguous_input(string source, string value)
    {
        Assert.ThrowsAny<ArgumentException>(() => new ExternalReference(source, value));
    }

    [Fact]
    public void External_reference_length_limits()
    {
        Assert.ThrowsAny<ArgumentException>(() => new ExternalReference(new string('s', 65), "X"));
        Assert.ThrowsAny<ArgumentException>(() => new ExternalReference("mpesa", new string('v', 129)));
        _ = new ExternalReference(new string('s', 64), new string('v', 128));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(" key")]
    [InlineData("key\t")]
    public void Idempotency_keys_reject_blank_or_padded_values(string value)
    {
        Assert.ThrowsAny<ArgumentException>(() => new IdempotencyKey(value));
    }

    [Fact]
    public void Idempotency_keys_are_value_objects_with_a_length_limit()
    {
        Assert.Equal(new IdempotencyKey("mpesa:600123:TJ3K9XYZ"), new IdempotencyKey("mpesa:600123:TJ3K9XYZ"));
        Assert.NotEqual(new IdempotencyKey("a"), new IdempotencyKey("A"));
        Assert.ThrowsAny<ArgumentException>(() => new IdempotencyKey(new string('k', 129)));
        Assert.Equal("abc", new IdempotencyKey("abc").ToString());
    }
}
