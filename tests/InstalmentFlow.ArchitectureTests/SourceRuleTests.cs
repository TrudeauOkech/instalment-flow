using System.Text.RegularExpressions;

namespace InstalmentFlow.ArchitectureTests;

/// <summary>
/// Source rules the analyzers cannot fully enforce. BannedApiAnalyzers catches member usage
/// (e.g. DateTimeOffset.UtcNow) but not the <c>double</c>/<c>float</c> keywords in declarations.
/// </summary>
public sealed partial class SourceRuleTests
{
    /// <summary>Escape hatch for a justified exception, e.g. a metrics histogram: put this marker on the line.</summary>
    private const string AllowMarker = "floating-point-allowed:";

    [Fact]
    public void Shipped_code_never_uses_floating_point_types()
    {
        var offences = SourceFiles()
            .SelectMany(file => File.ReadLines(file).Select((line, index) => (file, line, number: index + 1)))
            .Where(x => !x.line.Contains(AllowMarker, StringComparison.Ordinal))
            .Where(x => FloatingPoint().IsMatch(StripCommentsAndStrings(x.line)))
            .Select(x => $"{Path.GetRelativePath(Repository.Root, x.file)}:{x.number}: {x.line.Trim()}")
            .ToList();

        Assert.True(offences.Count == 0,
            "Floating point is banned in financial code (NFR-3); use decimal or Money:\n" + string.Join('\n', offences));
    }

    [Fact]
    public void Rule_detects_floating_point_and_ignores_comments_and_strings()
    {
        Assert.Matches(FloatingPoint(), StripCommentsAndStrings("internal static double Bad() => 1;"));
        Assert.Matches(FloatingPoint(), StripCommentsAndStrings("var x = (float)y;"));
        Assert.DoesNotMatch(FloatingPoint(), StripCommentsAndStrings("// never use double here"));
        Assert.DoesNotMatch(FloatingPoint(), StripCommentsAndStrings("var s = \"double\";"));
        Assert.DoesNotMatch(FloatingPoint(), StripCommentsAndStrings("var doubled = DoubleEntry();"));
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(Repository.SourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string StripCommentsAndStrings(string line)
    {
        var withoutStrings = StringLiteral().Replace(line, "\"\"");
        var comment = withoutStrings.IndexOf("//", StringComparison.Ordinal);
        return comment >= 0 ? withoutStrings[..comment] : withoutStrings;
    }

    [GeneratedRegex(@"\b(double|float|Double|Single)\b")]
    private static partial Regex FloatingPoint();

    [GeneratedRegex("\"(?:[^\"\\\\]|\\\\.)*\"")]
    private static partial Regex StringLiteral();
}
