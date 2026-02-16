namespace WitBindgen.SourceGenerator.Models;

/// <summary>
/// Represents a tuple type in WIT.
/// </summary>
public record WitTupleType(
    EquatableArray<WitType> ElementTypes
) : WitType(WitTypeKind.Tuple);
