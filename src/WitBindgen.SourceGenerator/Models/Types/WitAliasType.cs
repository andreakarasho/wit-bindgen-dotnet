namespace WitBindgen.SourceGenerator.Models;

/// <summary>
/// A custom type that is a strict subtype of another custom type.
/// This is used for imported interfaces via use statements in WIT.
/// </summary>
public record WitAliasType(
    WitCustomType Parent,
    string Name
) : WitCustomType(Parent.Package, Name)
{
    protected internal override ITypeContainer GetContainer(ITypeContainerResolver resolver,
        bool allowContainer = false, string? typeName = null)
    {
        var parentContainer = Parent.GetContainer(resolver, allowContainer: true, typeName: Name);

        if (!parentContainer.TryGetContainer(Parent.Name, out var container))
        {
            throw new InvalidOperationException($"Could not resolve subtype '{Name}' in '{Parent.Name}'");
        }

        return container;
    }
}
