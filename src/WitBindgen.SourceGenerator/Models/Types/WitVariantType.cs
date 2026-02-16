namespace WitBindgen.SourceGenerator.Models;

public record WitVariantType(
    WitPackageNameVersion Package,
    string Name,
    EquatableArray<WitVariantCase> Values
) : WitType(WitTypeKind.Variant)
{
    public string CSharpName { get; } = StringUtils.GetName(Name);
}
