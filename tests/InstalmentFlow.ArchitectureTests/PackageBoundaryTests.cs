namespace InstalmentFlow.ArchitectureTests;

/// <summary>
/// ADR-013: the seven packages and the only project references they may take.
/// Adding a package or a reference requires an ADR and an update to this table.
/// </summary>
public sealed class PackageBoundaryTests
{
    private const string Core = "InstalmentFlow";
    private const string Payments = "InstalmentFlow.Payments";

    private static readonly Dictionary<string, string[]> s_allowedReferences = new(StringComparer.Ordinal)
    {
        [Core] = [],
        ["InstalmentFlow.EntityFrameworkCore"] = [Core, Payments],
        [Payments] = [Core],
        ["InstalmentFlow.Payments.Mpesa"] = [Core, Payments],
        ["InstalmentFlow.Payments.AirtelMoney"] = [Core, Payments],
        ["InstalmentFlow.AspNetCore"] = [Core, Payments],
        ["InstalmentFlow.Testing"] = [Core, Payments],
    };

    /// <summary>Build-time tooling that never flows to consumers.</summary>
    private static readonly HashSet<string> s_developmentOnlyPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "MinVer",
    };

    public static TheoryData<string> SourceProjects => new(Repository.SourceProjectNames());

    [Fact]
    public void Source_projects_are_exactly_the_approved_packages()
    {
        Assert.Equal(s_allowedReferences.Keys.Order(StringComparer.Ordinal), Repository.SourceProjectNames());
    }

    [Theory]
    [MemberData(nameof(SourceProjects))]
    public void Project_references_follow_the_dependency_rules(string project)
    {
        var forbidden = Repository.ProjectReferences(project).Except(s_allowedReferences[project]).ToList();

        Assert.True(forbidden.Count == 0, $"{project} must not reference: {string.Join(", ", forbidden)} (ADR-013).");
    }

    [Fact]
    public void Core_has_no_runtime_package_dependencies_except_extension_abstractions()
    {
        var runtimeDependencies = Repository.DirectPackages(Core)
            .Where(p => !p.PrivateAssets)
            .Select(p => p.Id)
            .Where(id => !(id.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)
                           && id.EndsWith(".Abstractions", StringComparison.Ordinal)))
            .ToList();

        Assert.True(runtimeDependencies.Count == 0,
            $"Core may depend only on Microsoft.Extensions.*.Abstractions; found: {string.Join(", ", runtimeDependencies)} (NFR-6).");
    }

    [Fact]
    public void Core_resolved_graph_contains_no_third_party_packages()
    {
        var thirdParty = Repository.ResolvedPackages(Core)
            .Where(id => !id.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
                         && !id.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                         && !s_developmentOnlyPackages.Contains(id))
            .ToList();

        Assert.True(thirdParty.Count == 0, $"Core resolves third-party packages: {string.Join(", ", thirdParty)} (NFR-6).");
    }

    [Theory]
    [MemberData(nameof(SourceProjects))]
    public void Development_only_packages_are_private_assets(string project)
    {
        var leaking = Repository.DirectPackages(project)
            .Where(p => s_developmentOnlyPackages.Contains(p.Id) && !p.PrivateAssets)
            .Select(p => p.Id)
            .ToList();

        Assert.True(leaking.Count == 0, $"{project} exposes build tooling to consumers: {string.Join(", ", leaking)}.");
    }

    [Theory]
    [MemberData(nameof(SourceProjects))]
    public void Public_api_is_tracked(string project)
    {
        var directory = Repository.ProjectDirectory(project);

        Assert.True(File.Exists(Path.Combine(directory, "PublicAPI.Shipped.txt")), $"{project}: PublicAPI.Shipped.txt missing (NFR-10).");
        Assert.True(File.Exists(Path.Combine(directory, "PublicAPI.Unshipped.txt")), $"{project}: PublicAPI.Unshipped.txt missing (NFR-10).");
    }
}
