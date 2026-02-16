using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace WitBindgen.SourceGenerator.Models;

/// <summary>
/// Basic diagnostic description for reporting diagnostic inside the incremental pipeline.
/// </summary>
public sealed record ReportedDiagnostic(
    DiagnosticDescriptor Descriptor,
    string FilePath,
    TextSpan TextSpan,
    LinePositionSpan LineSpan,
    EquatableArray<object> Trivia)
{
    public static implicit operator Diagnostic(ReportedDiagnostic diagnostic)
        => Diagnostic.Create(
            descriptor: diagnostic.Descriptor,
            location: Location.Create(diagnostic.FilePath, diagnostic.TextSpan, diagnostic.LineSpan),
            messageArgs: diagnostic.Trivia.GetUnsafeArray());

    public static ReportedDiagnostic Create(DiagnosticDescriptor descriptor, Location location, EquatableArray<object> trivia)
    {
        return new(descriptor, location.SourceTree?.FilePath ?? string.Empty, location.SourceSpan, location.GetLineSpan().Span, trivia);
    }
}
