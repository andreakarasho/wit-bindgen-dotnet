namespace WitBindgen.SourceGenerator.Models;

public record WitWorldInclude(
    WitPackageNameVersion Package,
    string WorldName
) : WitWorldItem;
