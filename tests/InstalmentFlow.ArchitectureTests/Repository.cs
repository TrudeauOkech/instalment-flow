using System.Text.Json;
using System.Xml.Linq;

namespace InstalmentFlow.ArchitectureTests;

/// <summary>Locates the repository and reads project metadata without loading the assemblies.</summary>
internal static class Repository
{
    public static string Root { get; } = FindRoot();

    public static string SourceDirectory => Path.Combine(Root, "src");

    public static IReadOnlyList<string> SourceProjectNames() =>
        Directory.GetDirectories(SourceDirectory)
            .Where(d => File.Exists(Path.Combine(d, Path.GetFileName(d) + ".csproj")))
            .Select(d => Path.GetFileName(d))
            .Order(StringComparer.Ordinal)
            .ToList();

    public static string ProjectDirectory(string project) => Path.Combine(SourceDirectory, project);

    public static IReadOnlyList<string> ProjectReferences(string project)
    {
        var csproj = XDocument.Load(Path.Combine(ProjectDirectory(project), project + ".csproj"));
        return csproj.Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include") ?? throw new InvalidDataException($"{project}: ProjectReference without Include."))
            .Select(path => Path.GetFileNameWithoutExtension(path.Replace('\\', '/')))
            .ToList();
    }

    /// <summary>Direct package dependencies from restore output (project.assets.json).</summary>
    public static IReadOnlyList<PackageDependency> DirectPackages(string project)
    {
        using var assets = ReadAssets(project);
        var frameworks = assets.RootElement.GetProperty("project").GetProperty("frameworks");
        var result = new List<PackageDependency>();
        foreach (var framework in frameworks.EnumerateObject())
        {
            if (!framework.Value.TryGetProperty("dependencies", out var dependencies))
            {
                continue;
            }

            foreach (var dependency in dependencies.EnumerateObject())
            {
                var privateAssets = dependency.Value.TryGetProperty("suppressParent", out var suppress)
                    && string.Equals(suppress.GetString(), "All", StringComparison.OrdinalIgnoreCase);
                result.Add(new PackageDependency(dependency.Name, privateAssets));
            }
        }

        return result;
    }

    /// <summary>Every package in the resolved (transitive) graph.</summary>
    public static IReadOnlyList<string> ResolvedPackages(string project)
    {
        using var assets = ReadAssets(project);
        return assets.RootElement.GetProperty("targets").EnumerateObject()
            .SelectMany(target => target.Value.EnumerateObject())
            .Where(library => library.Value.GetProperty("type").GetString() == "package")
            .Select(library => library.Name.Split('/')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static JsonDocument ReadAssets(string project)
    {
        var path = Path.Combine(ProjectDirectory(project), "obj", "project.assets.json");
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"{path} not found. Restore the solution before running architecture tests.");
        }

        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "InstalmentFlow.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (InstalmentFlow.slnx) not found.");
    }
}

internal sealed record PackageDependency(string Id, bool PrivateAssets);
