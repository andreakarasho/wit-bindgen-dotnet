namespace WitBindgen.SourceGenerator.Models;

public record WitTypeAlias(
    string Name,
    WitType Type
) : WitTypeDef;
