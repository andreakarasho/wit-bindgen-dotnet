using WitBindgen.SourceGenerator.Models;

namespace WitBindgen.SourceGenerator;

/// <summary>
/// Resolves types from a collection of packages.
/// </summary>
public class ProjectTypeContainerResolver : ITypeContainerResolver
{
    private readonly Dictionary<WitPackageName, WitPackage> _packages;

    public IReadOnlyDictionary<WitPackageName, WitPackage> Packages => _packages;

    public ProjectTypeContainerResolver(IEnumerable<WitPackage> packages)
    {
        _packages = packages
            .GroupBy(p => p.PackageName)
            .ToDictionary(g => g.Key, group =>
            {
                var versions = new Dictionary<SemVer, WitPackageVersion>();

                foreach (var pkg in group)
                {
                    foreach (var kv in pkg.Versions)
                    {
                        var result = kv.Value;

                        if (versions.TryGetValue(kv.Key, out var existing))
                        {
                            result = existing.Merge(result);
                        }

                        versions[kv.Key] = result;
                    }
                }

                return new WitPackage(group.Key, versions);
            });
    }

    public ITypeContainer Resolve(WitPackageNameVersion fullName)
    {
        var package = fullName;
        List<string>? path = null;

        WitPackageVersion? version = null;
        while (package.PackageName.Name.Length > 0)
        {
            if (_packages.TryGetValue(package.PackageName, out var pkg) &&
                pkg.Versions.TryGetValue(package.Version, out version))
            {
                break;
            }

            (var item, package) = package.WithoutLastNamePart();

            path ??= [];
            path.Add(item);
        }

        if (version is null)
        {
            throw new InvalidOperationException($"Could not determine package '{package}'");
        }

        if (path is null)
        {
            return version;
        }

        // Reverse the path to get the correct order
        path.Reverse();

        // Try to get the type from the version
        while (true)
        {
            ITypeContainer? container = version;

            foreach (var part in path)
            {
                if (!container.TryGetContainer(part, out var next))
                {
                    container = null;
                    break;
                }

                container = next;
            }

            if (container != null)
            {
                return container;
            }

            if (path.Count == 0)
            {
                break;
            }

            path.RemoveAt(path.Count - 1);
        }

        throw new InvalidOperationException($"Type '{fullName}' not found.");
    }
}
