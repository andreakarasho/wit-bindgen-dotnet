using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using WitBindgen.SourceGenerator.Generators.Guest;
using WitBindgen.SourceGenerator.Models;

namespace WitBindgen.SourceGenerator;

/// <summary>
/// Roslyn incremental source generator that reads WIT files and generates guest-side C# bindings
/// for the Component Model canonical ABI.
/// </summary>
[Generator(LanguageNames.CSharp)]
public class ComponentGuestGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Collect .wit files from AdditionalTexts
        var rawWitFiles = context.AdditionalTextsProvider
            .Where(a => a.Path.EndsWith(".wit", StringComparison.OrdinalIgnoreCase))
            .Select((text, cancellationToken) => (path: text.Path, content: text.GetText(cancellationToken)?.ToString() ?? ""))
            .Collect()
            .SelectMany(GetRawDirectories);

        var witFiles = rawWitFiles
            .Select(ParseWitDirectory);

        var packages = witFiles
            .SelectMany((x, _) => x.Packages)
            .Collect();

        // Generate the guest bindings
        context.RegisterSourceOutput(packages, GuestWriter.GenerateGuestBindings);
    }

    /// <summary>
    /// Groups all files by their directory.
    /// </summary>
    private static IEnumerable<WitRawDirectory> GetRawDirectories(ImmutableArray<(string path, string content)> array, CancellationToken ct)
    {
        var dictionary = new Dictionary<string, ImmutableArray<string>.Builder>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, content) in array)
        {
            var directory = Path.GetDirectoryName(path) ?? string.Empty;

            if (!dictionary.TryGetValue(directory, out var includes))
            {
                includes = ImmutableArray.CreateBuilder<string>();
                dictionary[directory] = includes;
            }

            includes.Add(content);
        }

        var results = ImmutableArray.CreateBuilder<WitRawDirectory>(dictionary.Count);

        foreach (var kv in dictionary)
        {
            results.Add(new WitRawDirectory(kv.Key, kv.Value.ToImmutable()));
        }

        return results;
    }

    /// <summary>
    /// Parses a WIT directory into a WitDirectory.
    /// </summary>
    private static WitDirectory ParseWitDirectory(WitRawDirectory directory, CancellationToken ct)
    {
        try
        {
            return Wit.Parse(directory);
        }
        catch
        {
            return WitDirectory.Empty;
        }
    }
}
