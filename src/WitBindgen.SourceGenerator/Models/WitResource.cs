namespace WitBindgen.SourceGenerator.Models;

public record WitResource(
    WitPackageNameVersion Package,
    string Name,
    EquatableArray<WitResourceConstructor> Constructors,
    EquatableArray<WitField> Methods,
    EquatableArray<WitField> StaticMethods
) : WitTypeDef
{
    public string CSharpName { get; } = StringUtils.GetName(Name);

    public WitType Type { get; } = new WitResourceType(Package, Name, Methods);
}

public record WitResourceConstructor(
    EquatableArray<WitFuncParameter> Parameters,
    WitType? ReturnType
);
