namespace WitBindgen.SourceGenerator.Models;

/// <summary>
/// Represents a user-defined type.
/// </summary>
/// <param name="Package">The package that requested this type.</param>
/// <param name="Name">The name of the type.</param>
public record WitCustomType(WitPackageNameVersion Package, string Name) : WitType(WitTypeKind.User)
{
    protected internal virtual ITypeContainer GetContainer(
        ITypeContainerResolver resolver,
        bool allowContainer = false,
        string? typeName = null
    )
    {
        typeName ??= Name;

        var current = Package;

        while (current.PackageName.Name.Length > 0)
        {
            var container = resolver.Resolve(current);

            if ((
                    container.TryGetType(typeName, out var type)
                    && (
                        !allowContainer
                        || type is not WitAliasType
                    )
                ) || (
                    allowContainer
                    && container.TryGetContainer(Name, out _)
                ))
            {
                return container;
            }

            (_, current) = current.WithoutLastNamePart();
        }

        throw new InvalidOperationException($"Could not resolve type '{typeName}' in '{Package}'");
    }

    public WitType Resolve(ITypeContainerResolver resolver)
    {
        var container = GetContainer(resolver, allowContainer: false);

        if (!container.TryGetType(Name, out var type))
        {
            throw new InvalidOperationException($"Could not resolve type '{Name}' in '{Package}'");
        }

        return type;
    }

    public void Deconstruct(out WitPackageNameVersion Package, out string Name)
    {
        Package = this.Package;
        Name = this.Name;
    }
}
