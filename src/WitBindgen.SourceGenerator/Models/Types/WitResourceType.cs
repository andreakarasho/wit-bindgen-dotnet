namespace WitBindgen.SourceGenerator.Models;

public record WitResourceType(
    WitPackageNameVersion Package,
    string Name,
    EquatableArray<WitField> Fields
) : WitType(WitTypeKind.Resource)
{
    public string CSharpName { get; } = StringUtils.GetName(Name);
}
