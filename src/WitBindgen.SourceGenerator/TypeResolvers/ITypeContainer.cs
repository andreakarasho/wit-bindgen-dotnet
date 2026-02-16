using System.Diagnostics.CodeAnalysis;
using WitBindgen.SourceGenerator.Models;

namespace WitBindgen.SourceGenerator;

public interface ITypeContainer
{
    bool TryGetType(string name, [NotNullWhen(true)] out WitType? type);

    bool TryGetContainer(string name, [NotNullWhen(true)] out ITypeContainer? container);
}
