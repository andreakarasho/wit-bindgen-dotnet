namespace WitBindgen.SourceGenerator.Models;

public record WitEnumType(
    WitPackageNameVersion Package,
    string Name
) : WitType(WitTypeKind.Enum)
{
    public string CSharpName { get; } = StringUtils.GetName(Name);
}

public record WitFlagsType(
    WitPackageNameVersion Package,
    string Name
) : WitType(WitTypeKind.Flags)
{
    public string CSharpName { get; } = StringUtils.GetName(Name);
}
