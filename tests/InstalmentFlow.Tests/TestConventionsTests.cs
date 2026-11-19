using System.Reflection;

namespace InstalmentFlow.Tests;

/// <summary>Guards against test data that silently passes through floating point.</summary>
public sealed class TestConventionsTests
{
    [Fact]
    public void Decimal_parameters_are_never_fed_from_attribute_literals()
    {
        // [InlineData(10.01)] supplies a double; converting it to decimal is runtime-dependent
        // (.NET 11 yields 10.009999999999999786837179272). Use TheoryData with decimal literals instead.
        var offenders = typeof(TestConventionsTests).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .Where(m => m.GetCustomAttributes<InlineDataAttribute>().Any())
            .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(decimal) || p.ParameterType == typeof(decimal?)))
            .Select(m => $"{m.DeclaringType?.Name}.{m.Name}")
            .ToList();

        Assert.True(offenders.Count == 0, "Use TheoryData<decimal, ...> instead of [InlineData] for: " + string.Join(", ", offenders));
    }
}
