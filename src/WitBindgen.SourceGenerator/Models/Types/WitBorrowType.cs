namespace WitBindgen.SourceGenerator.Models;

/// <summary>
/// Represents a borrow type in WIT.
/// </summary>
public record WitBorrowType(
    WitType ElementType
) : WitType(WitTypeKind.Borrow);
