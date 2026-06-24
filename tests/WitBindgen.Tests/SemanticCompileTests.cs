using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using WitBindgen.SourceGenerator;
using Xunit;

namespace WitBindgen.Tests;

/// <summary>
/// Semantic-compile gate. The other codegen tests assert parse-level diagnostics and
/// string-contains only — they do NOT prove the generated C# binds and compiles, which is
/// how the cast-vs-multiply bug (CS0119, a *semantic* error) shipped while every test stayed
/// green. These tests run the generator and then bind the generated trees against a real
/// reference set (the running framework's TPA + WitBindgen.Runtime), failing on any C# error.
///
/// Scope is intentionally import-side: imported functions and type declarations generate
/// fully self-contained code. Exported functions emit `public static partial` declarations the
/// user must implement, so an export compilation needs synthesized impls — that path is covered
/// by building tests/WitBindgen.E2E/Guest (which supplies the real partials).
/// </summary>
public class SemanticCompileTests
{
    [Fact]
    public void GeneratedImportCodeBindsAndCompiles()
    {
        // Exercises every import-side lift path that emits a `(Type)*(...)` cast:
        //  - list<enum>            -> WriteLiftListElement enum case
        //  - variant return        -> WriteMemoryLoad variant discriminant read
        //  - record-with-enum      -> WriteMemoryLoad enum case (indirect/retptr)
        //  - tuple<flags, list>    -> WriteMemoryLoad flags case + nested list lift
        //  - char / flags returns  -> high-level wrapper lift casts (bug #5)
        var wit = @"
package my:pkg@1.0.0;

interface t {
    enum color { red, green, blue }
    flags perms { a, b, c }
    variant shape { circle(f64), sq(u32), empty }
    record swatch { c: color, n: u32 }

    get-colors: func() -> list<color>;
    get-shape: func() -> shape;
    get-swatch: func() -> swatch;
    get-combo: func() -> tuple<perms, list<u32>>;
    get-char: func() -> char;
    echo-char: func(c: char) -> char;
    get-perms: func() -> perms;
    echo-perms: func(p: perms) -> perms;
}

world w { import t; }
";
        AssertCompiles(wit);
    }

    [Fact]
    public void GeneratedExportCharFlagsEnumVariantBindsAndCompiles()
    {
        // Export-side lift/lower for char (int core -> uint high-level), flags (typed cast both
        // ways), enum, and a variant return. Bug #5: char/flags params + flags returns previously
        // emitted uncast assignments (CS1503) / `return default`.
        var wit = @"
package my:pkg@1.0.0;

world w {
    enum color { red, green, blue }
    flags perms { a, b, c }
    variant shape { circle(f64), sq(u32), empty }

    export echo-char: func(c: char) -> char;
    export echo-perms: func(p: perms) -> perms;
    export echo-color: func(c: color) -> color;
    export make-shape: func(n: u32) -> shape;
}
";
        var impl = @"
namespace Wit.My.Pkg;
public static partial class W
{
    public static partial uint EchoChar(uint c) => c;
    public static partial Perms EchoPerms(Perms p) => p;
    public static partial Color EchoColor(Color c) => c;
    public static partial Shape MakeShape(uint n) => Shape.CreateSq(n);
}
";
        AssertCompiles(wit, impl);
    }

    [Fact]
    public void GeneratedExportVariantParamsBindAndCompile()
    {
        // Bug #3: exported variant params were not lifted (LiftParam fell to default and emitted
        // an undefined `v`). Covers a value-payload variant and a string-payload variant.
        var wit = @"
package my:pkg@1.0.0;

world w {
    variant small-value { none, byte(u8), word(u16) }
    variant labeled { plain, named(string) }

    export take-small: func(v: small-value) -> u16;
    export take-labeled: func(v: labeled) -> u32;
}
";
        var impl = @"
namespace Wit.My.Pkg;
public static partial class W
{
    public static partial ushort TakeSmall(SmallValue v)
        => v.Discriminant == SmallValue.Case.Word ? v.WordPayload : (ushort)0;
    public static partial uint TakeLabeled(Labeled v)
        => v.Discriminant == Labeled.Case.Named ? (uint)v.NamedPayload.Length : 0u;
}
";
        AssertCompiles(wit, impl);
    }

    [Fact]
    public void GeneratedImportResultReturnsBindAndCompile()
    {
        // Bug #2: result<T,E> was mapped to `object` with no lift -> didn't compile / invalid.
        // Covers full result, ok-only (result<t>), err-only (result<_,e>), and a primitive pair.
        var wit = @"
package my:pkg@1.0.0;

interface t {
    record point { x: s32, y: s32 }
    try-parse: func(s: string) -> result<u32, string>;
    only-ok: func() -> result<point>;
    only-err: func() -> result<_, string>;
    res-prim: func() -> result<u64, u32>;
}

world w { import t; }
";
        var code = AssertCompiles(wit);
        Assert.Contains("global::WitBindgen.Runtime.WitResult<uint, string>", code);
        Assert.Contains(".FromOk(", code);
        Assert.Contains(".FromErr(", code);
    }

    [Fact]
    public void GeneratedExportResultBindsAndCompiles()
    {
        // Bug #2 on the export side: result return (memory store + post-return free of the
        // heap-bearing arm) and result param (flat lift).
        var wit = @"
package my:pkg@1.0.0;

world w {
    export try-thing: func(n: u32) -> result<u32, string>;
    export consume: func(r: result<u32, string>) -> bool;
}
";
        var impl = @"
namespace Wit.My.Pkg;
public static partial class W
{
    public static partial global::WitBindgen.Runtime.WitResult<uint, string> TryThing(uint n)
        => n > 0
            ? global::WitBindgen.Runtime.WitResult<uint, string>.FromOk(n)
            : global::WitBindgen.Runtime.WitResult<uint, string>.FromErr(""bad"");
    public static partial bool Consume(global::WitBindgen.Runtime.WitResult<uint, string> r) => r.IsOk;
}
";
        AssertCompiles(wit, impl);
    }

    /// <summary>
    /// Runs the guest generator over <paramref name="witContent"/> and asserts the generated
    /// trees bind with no C# errors. Returns the concatenated generated source for callers that
    /// want to assert on it as well.
    ///
    /// <paramref name="userImpl"/> supplies the partial-class implementations that exported
    /// functions require (the generator emits `public static partial` declarations the user must
    /// fill in). Pass it whenever the WIT has exports, mirroring the real guest project.
    /// </summary>
    internal static string AssertCompiles(string witContent, string? userImpl = null, string fileName = "test.wit")
    {
        var (code, errors) = GenerateAndCompile(witContent, userImpl, fileName);
        Assert.True(
            errors.Count == 0,
            $"Generated code failed to bind ({errors.Count} error(s)):\n  " +
            string.Join("\n  ", errors) +
            "\n\n--- GENERATED ---\n" + code);
        return code;
    }

    internal static (string code, IReadOnlyList<string> errors) GenerateAndCompile(
        string witContent, string? userImpl = null, string fileName = "test.wit")
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        // The generator emits bare `Span`/`ReadOnlySpan`/`Math` that resolve via the guest
        // projects' <ImplicitUsings>enable</ImplicitUsings> (global using System). Replicate that
        // here so the gate compiles in the same namespace environment the real build provides.
        var globalUsings = CSharpSyntaxTree.ParseText(
            "global using global::System;\nglobal using global::System.Collections.Generic;\n",
            parseOptions);
        var baseTrees = new List<SyntaxTree> { globalUsings };
        if (userImpl is not null)
            baseTrees.Add(CSharpSyntaxTree.ParseText(userImpl, parseOptions, path: "UserImpl.cs"));

        var baseCompilation = CSharpCompilation.Create(
            "GenInput",
            baseTrees,
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var generator = new ComponentGuestGenerator();
        var driver = CSharpGeneratorDriver.Create(
            new[] { generator.AsSourceGenerator() },
            parseOptions: parseOptions);

        driver = (CSharpGeneratorDriver)driver
            .AddAdditionalTexts(ImmutableArray.Create<AdditionalText>(
                new InMemoryAdditionalText(fileName, witContent)));

        driver.RunGeneratorsAndUpdateCompilation(baseCompilation, out var outputCompilation, out _);

        var errors = outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();

        // The leading base trees (global usings, optional user impl) precede the generated output.
        var code = string.Join("\n\n", outputCompilation.SyntaxTrees.Skip(baseTrees.Count).Select(t => t.ToString()));
        return (code, errors);
    }

    private static readonly ImmutableArray<MetadataReference> References = BuildReferences();

    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        var tpa = (string)(AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "");
        var refs = tpa.Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
        // WitBindgen.Runtime is project-referenced by this test project (see .csproj).
        refs.Add(MetadataReference.CreateFromFile(typeof(WitBindgen.Runtime.InteropHelpers).Assembly.Location));
        return refs.ToImmutableArray();
    }
}
