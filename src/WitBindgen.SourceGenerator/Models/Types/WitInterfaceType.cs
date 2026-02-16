namespace WitBindgen.SourceGenerator.Models;

public record WitInterfaceType(
    string Name,
    EquatableArray<WitField> Fields
) : WitType(WitTypeKind.Interface);
