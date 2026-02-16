using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using WitBindgen.SourceGenerator;
using WitBindgen.SourceGenerator.Generators.Guest;
using WitBindgen.SourceGenerator.Models;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace WitBindgen.Tests;

public class CodeGenerationTests
{
    #region GuestTypeWriter - Records

    [Fact]
    public void GenerateRecordStruct()
    {
        var wit = @"
package my:pkg@1.0.0;

interface types {
    record point {
        x: s32,
        y: s32,
    }
}
";
        var directory = Wit.Parse(wit);
        var interf = directory.Packages.Values.First().Versions.Values.First()
            .Definitions.Items[0] as WitInterface;
        var record = interf!.Definitions.Items[0] as WitRecord;

        var sb = new IndentedStringBuilder();
        GuestTypeWriter.WriteRecord(sb, record!);
        var code = sb.ToString();

        Assert.Contains("public struct Point", code);
        Assert.Contains("public int X;", code);
        Assert.Contains("public int Y;", code);
    }

    [Fact]
    public void GenerateRecordWithMixedTypes()
    {
        var wit = @"
package my:pkg@1.0.0;

interface types {
    record person {
        name: string,
        age: u32,
        score: f64,
    }
}
";
        var directory = Wit.Parse(wit);
        var interf = directory.Packages.Values.First().Versions.Values.First()
            .Definitions.Items[0] as WitInterface;
        var record = interf!.Definitions.Items[0] as WitRecord;

        var sb = new IndentedStringBuilder();
        GuestTypeWriter.WriteRecord(sb, record!);
        var code = sb.ToString();

        Assert.Contains("public struct Person", code);
        Assert.Contains("public string Name;", code);
        Assert.Contains("public uint Age;", code);
        Assert.Contains("public double Score;", code);
    }

    #endregion

    #region GuestTypeWriter - Enums

    [Fact]
    public void GenerateEnum()
    {
        var wit = @"
package my:pkg@1.0.0;

interface types {
    enum color {
        red,
        green,
        blue,
    }
}
";
        var directory = Wit.Parse(wit);
        var interf = directory.Packages.Values.First().Versions.Values.First()
            .Definitions.Items[0] as WitInterface;
        var @enum = interf!.Definitions.Items[0] as WitEnum;

        var sb = new IndentedStringBuilder();
        GuestTypeWriter.WriteEnum(sb, @enum!);
        var code = sb.ToString();

        Assert.Contains("public enum Color", code);
        Assert.Contains("Red = 0", code);
        Assert.Contains("Green = 1", code);
        Assert.Contains("Blue = 2", code);
    }

    #endregion

    #region GuestTypeWriter - Flags

    [Fact]
    public void GenerateFlags()
    {
        var wit = @"
package my:pkg@1.0.0;

interface types {
    flags permissions {
        read,
        write,
        execute,
    }
}
";
        var directory = Wit.Parse(wit);
        var interf = directory.Packages.Values.First().Versions.Values.First()
            .Definitions.Items[0] as WitInterface;
        var flags = interf!.Definitions.Items[0] as WitFlags;

        var sb = new IndentedStringBuilder();
        GuestTypeWriter.WriteFlags(sb, flags!);
        var code = sb.ToString();

        Assert.Contains("[System.Flags]", code);
        Assert.Contains("public enum Permissions", code);
        Assert.Contains("Read = 1", code);
        Assert.Contains("Write = 2", code);
        Assert.Contains("Execute = 4", code);
    }

    #endregion

    #region GuestTypeWriter - Variants

    [Fact]
    public void GenerateVariant()
    {
        var wit = @"
package my:pkg@1.0.0;

interface types {
    variant my-variant {
        case-a(s32),
        case-b(f32),
        case-c,
    }
}
";
        var directory = Wit.Parse(wit);
        var interf = directory.Packages.Values.First().Versions.Values.First()
            .Definitions.Items[0] as WitInterface;
        var variant = interf!.Definitions.Items[0] as WitVariant;

        var sb = new IndentedStringBuilder();
        GuestTypeWriter.WriteVariant(sb, variant!);
        var code = sb.ToString();

        Assert.Contains("StructLayout", code);
        Assert.Contains("LayoutKind.Explicit", code);
        Assert.Contains("public struct MyVariant", code);
        Assert.Contains("public Case Discriminant;", code);
        Assert.Contains("FieldOffset(0)", code);
        Assert.Contains("FieldOffset(4)", code);
        Assert.Contains("CaseAPayload", code);
        Assert.Contains("CaseBPayload", code);
        Assert.Contains("public enum Case", code);
        Assert.Contains("CaseA = 0", code);
        Assert.Contains("CaseB = 1", code);
        Assert.Contains("CaseC = 2", code);
        Assert.Contains("CreateCaseA(int val)", code);
        Assert.Contains("CreateCaseB(float val)", code);
        Assert.Contains("CreateCaseC()", code);
    }

    #endregion

    #region GuestImportWriter

    [Fact]
    public void GenerateImportSimpleFunction()
    {
        var wit = @"
package my:pkg@1.0.0;

world test-world {
    import add: func(a: s32, b: s32) -> s32;
}
";
        var directory = Wit.Parse(wit);
        var world = directory.Packages.Values.First().Versions.Values.First().Worlds["test-world"];
        var import = world.Definitions.Items[0] as WitWorldImport;
        var funcType = import!.Type as WitFuncType;

        var sb = new IndentedStringBuilder();
        GuestImportWriter.WriteImportFunction(sb, "Imports", "$root", "add", funcType!);
        var code = sb.ToString();

        // Verify DllImport stub
        Assert.Contains("[System.Runtime.InteropServices.DllImport(\"$root\", EntryPoint = \"add\")]", code);
        Assert.Contains("[System.Runtime.InteropServices.WasmImportLinkage]", code);
        Assert.Contains("internal static extern", code);
        Assert.Contains("int Add(", code);

        // Verify high-level wrapper
        Assert.Contains("[System.Runtime.CompilerServices.SkipLocalsInit]", code);
        Assert.Contains("public static unsafe int Add(int a, int b)", code);
    }

    [Fact]
    public void GenerateImportStringFunction()
    {
        var wit = @"
package my:pkg@1.0.0;

world test-world {
    import greet: func(name: string) -> string;
}
";
        var directory = Wit.Parse(wit);
        var world = directory.Packages.Values.First().Versions.Values.First().Worlds["test-world"];
        var import = world.Definitions.Items[0] as WitWorldImport;
        var funcType = import!.Type as WitFuncType;

        var sb = new IndentedStringBuilder();
        GuestImportWriter.WriteImportFunction(sb, "Imports", "$root", "greet", funcType!);
        var code = sb.ToString();

        // Raw import should use flattened string params (ptr, len) + retPtr
        Assert.Contains("DllImport(\"$root\", EntryPoint = \"greet\")", code);
        Assert.Contains("WasmImportLinkage", code);

        // High-level wrapper
        Assert.Contains("public static unsafe string Greet(string name)", code);
        // String lowering (stackalloc/ArrayPool pattern)
        Assert.Contains("UTF8.GetByteCount(name)", code);
        Assert.Contains("stackalloc byte[nameByteLen]", code);
        Assert.Contains("new Span<byte>(nameRented = System.Buffers.ArrayPool<byte>.Shared.Rent(nameByteLen), 0, nameByteLen)", code);
        Assert.Contains("UTF8.GetBytes(name", code);
        Assert.Contains("InteropHelpers.SpanToPointer(nameBuf)", code);
        // Cleanup uses ArrayPool return instead of Free
        Assert.Contains("ArrayPool<byte>.Shared.Return(nameRented)", code);
        // String lifting from result
        Assert.Contains("UTF8.GetString(", code);
    }

    [Fact]
    public void GenerateImportVoidFunction()
    {
        var wit = @"
package my:pkg@1.0.0;

world test-world {
    import do-nothing: func();
}
";
        var directory = Wit.Parse(wit);
        var world = directory.Packages.Values.First().Versions.Values.First().Worlds["test-world"];
        var import = world.Definitions.Items[0] as WitWorldImport;
        var funcType = import!.Type as WitFuncType;

        var sb = new IndentedStringBuilder();
        GuestImportWriter.WriteImportFunction(sb, "Imports", "$root", "do-nothing", funcType!);
        var code = sb.ToString();

        Assert.Contains("internal static extern void DoNothing()", code);
        Assert.Contains("public static unsafe void DoNothing()", code);
    }

    [Fact]
    public void GenerateImportBoolFunction()
    {
        var wit = @"
package my:pkg@1.0.0;

world test-world {
    import check: func(flag: bool) -> bool;
}
";
        var directory = Wit.Parse(wit);
        var world = directory.Packages.Values.First().Versions.Values.First().Worlds["test-world"];
        var import = world.Definitions.Items[0] as WitWorldImport;
        var funcType = import!.Type as WitFuncType;

        var sb = new IndentedStringBuilder();
        GuestImportWriter.WriteImportFunction(sb, "Imports", "$root", "check", funcType!);
        var code = sb.ToString();

        // Bool params are lowered to int
        Assert.Contains("public static unsafe bool Check(bool flag)", code);
        Assert.Contains("flag ? 1 : 0", code);
        // Bool result is lifted from int
        Assert.Contains("!= 0", code);
    }

    #endregion

    #region GuestExportWriter

    [Fact]
    public void GenerateExportVoidFunction()
    {
        var wit = @"
package my:pkg@1.0.0;

world test-world {
    export run: func();
}
";
        var directory = Wit.Parse(wit);
        var world = directory.Packages.Values.First().Versions.Values.First().Worlds["test-world"];
        var export = world.Definitions.Items[0] as WitWorldExport;
        var funcType = export!.Type as WitFuncType;

        var sb = new IndentedStringBuilder();
        GuestExportWriter.WriteExportFunction(sb, "run", "run", funcType!);
        var code = sb.ToString();

        // Partial method declaration
        Assert.Contains("public static partial void Run();", code);

        // Trampoline
        Assert.Contains("[System.Runtime.InteropServices.UnmanagedCallersOnly(EntryPoint = \"run\")]", code);
        Assert.Contains("SkipLocalsInit", code);
        Assert.Contains("Run();", code);
    }

    [Fact]
    public void GenerateExportStringFunction()
    {
        var wit = @"
package my:pkg@1.0.0;

world test-world {
    export run: func() -> string;
}
";
        var directory = Wit.Parse(wit);
        var world = directory.Packages.Values.First().Versions.Values.First().Worlds["test-world"];
        var export = world.Definitions.Items[0] as WitWorldExport;
        var funcType = export!.Type as WitFuncType;

        var sb = new IndentedStringBuilder();
        GuestExportWriter.WriteExportFunction(sb, "run", "run", funcType!);
        var code = sb.ToString();

        // Partial method
        Assert.Contains("public static partial string Run();", code);

        // Trampoline with string lowering
        Assert.Contains("UnmanagedCallersOnly(EntryPoint = \"run\")", code);
        Assert.Contains("var result = Run();", code);
        Assert.Contains("UTF8.GetByteCount(result)", code);
        Assert.Contains("InteropHelpers.Alloc(byteLen, 1)", code);
        Assert.Contains("GetReturnArea()", code);

        // Post-return cleanup
        Assert.Contains("cabi_post_run", code);
        Assert.Contains("InteropHelpers.Free(", code);
    }

    [Fact]
    public void GenerateExportPrimitiveFunction()
    {
        var wit = @"
package my:pkg@1.0.0;

world test-world {
    export compute: func(x: s32, y: s32) -> s32;
}
";
        var directory = Wit.Parse(wit);
        var world = directory.Packages.Values.First().Versions.Values.First().Worlds["test-world"];
        var export = world.Definitions.Items[0] as WitWorldExport;
        var funcType = export!.Type as WitFuncType;

        var sb = new IndentedStringBuilder();
        GuestExportWriter.WriteExportFunction(sb, "compute", "compute", funcType!);
        var code = sb.ToString();

        // Partial method
        Assert.Contains("public static partial int Compute(int x, int y);", code);

        // Trampoline with i32 return type (single flat result)
        Assert.Contains("UnmanagedCallersOnly(EntryPoint = \"compute\")", code);
        Assert.Contains("var result = Compute(", code);
    }

    [Fact]
    public void GenerateExportStringParam()
    {
        var wit = @"
package my:pkg@1.0.0;

world test-world {
    export process: func(input: string);
}
";
        var directory = Wit.Parse(wit);
        var world = directory.Packages.Values.First().Versions.Values.First().Worlds["test-world"];
        var export = world.Definitions.Items[0] as WitWorldExport;
        var funcType = export!.Type as WitFuncType;

        var sb = new IndentedStringBuilder();
        GuestExportWriter.WriteExportFunction(sb, "process", "process", funcType!);
        var code = sb.ToString();

        // Partial method takes string
        Assert.Contains("public static partial void Process(string input);", code);

        // Trampoline lifts string from (ptr, len) pair
        Assert.Contains("UTF8.GetString(", code);
    }

    #endregion

    #region Full Pipeline via CSharpGeneratorDriver

    [Fact]
    public void SourceGeneratorProducesOutput()
    {
        // Create a minimal compilation
        var compilation = CSharpCompilation.Create("TestAssembly",
            new[] { CSharpSyntaxTree.ParseText("") },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // Create the generator
        var generator = new ComponentGuestGenerator();

        // Create a .wit file as AdditionalText
        var witContent = @"
package my:pkg@1.0.0;

world hello-world {
    export run: func() -> string;
}
";
        var additionalText = new InMemoryAdditionalText("test/hello.wit", witContent);

        // Run the generator
        var driver = CSharpGeneratorDriver.Create(generator)
            .AddAdditionalTexts(ImmutableArray.Create<AdditionalText>(additionalText));

        driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(
            compilation, out var outputCompilation, out var diagnostics);

        var result = driver.GetRunResult();
        Assert.NotEmpty(result.GeneratedTrees);

        // Verify at least one generated source contains expected patterns
        var generatedCode = result.GeneratedTrees
            .Select(t => t.GetText().ToString())
            .Aggregate((a, b) => a + "\n" + b);

        Assert.Contains("// <auto-generated/>", generatedCode);
        Assert.Contains("#nullable enable", generatedCode);
        Assert.Contains("UnmanagedCallersOnly", generatedCode);
        Assert.Contains("partial string Run()", generatedCode);
    }

    [Fact]
    public void SourceGeneratorHandlesImportsAndExports()
    {
        var compilation = CSharpCompilation.Create("TestAssembly",
            new[] { CSharpSyntaxTree.ParseText("") },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new ComponentGuestGenerator();

        var witContent = @"
package my:pkg@1.0.0;

world greeter {
    import greet: func(name: string) -> string;
    export run: func() -> string;
}
";
        var additionalText = new InMemoryAdditionalText("test/greeter.wit", witContent);

        var driver = CSharpGeneratorDriver.Create(generator)
            .AddAdditionalTexts(ImmutableArray.Create<AdditionalText>(additionalText));

        driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(
            compilation, out _, out _);

        var result = driver.GetRunResult();
        Assert.NotEmpty(result.GeneratedTrees);

        var generatedCode = result.GeneratedTrees
            .Select(t => t.GetText().ToString())
            .Aggregate((a, b) => a + "\n" + b);

        // Import patterns
        Assert.Contains("DllImport", generatedCode);
        Assert.Contains("WasmImportLinkage", generatedCode);

        // Export patterns
        Assert.Contains("UnmanagedCallersOnly", generatedCode);
    }

    [Fact]
    public void SourceGeneratorHandlesInterfaceTypes()
    {
        var compilation = CSharpCompilation.Create("TestAssembly",
            new[] { CSharpSyntaxTree.ParseText("") },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new ComponentGuestGenerator();

        var witContent = @"
package my:pkg@1.0.0;

interface types {
    record point {
        x: s32,
        y: s32,
    }

    enum color {
        red,
        green,
        blue,
    }
}
";
        var additionalText = new InMemoryAdditionalText("test/types.wit", witContent);

        var driver = CSharpGeneratorDriver.Create(generator)
            .AddAdditionalTexts(ImmutableArray.Create<AdditionalText>(additionalText));

        driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(
            compilation, out _, out _);

        var result = driver.GetRunResult();
        Assert.NotEmpty(result.GeneratedTrees);

        var generatedCode = result.GeneratedTrees
            .Select(t => t.GetText().ToString())
            .Aggregate((a, b) => a + "\n" + b);

        Assert.Contains("public struct Point", generatedCode);
        Assert.Contains("public enum Color", generatedCode);
    }

    [Fact]
    public void SourceGeneratorHandlesNoWitFiles()
    {
        var compilation = CSharpCompilation.Create("TestAssembly",
            new[] { CSharpSyntaxTree.ParseText("") },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new ComponentGuestGenerator();

        var driver = CSharpGeneratorDriver.Create(generator);

        driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(
            compilation, out _, out var diagnostics);

        var result = driver.GetRunResult();
        Assert.Empty(result.GeneratedTrees);
        Assert.Empty(result.Diagnostics);
    }

    #endregion

    #region GuestTypeWriter - WriteAllTypes

    [Fact]
    public void WriteAllTypesGeneratesMultipleTypes()
    {
        var wit = @"
package my:pkg@1.0.0;

interface types {
    record point {
        x: s32,
        y: s32,
    }

    enum color {
        red,
        green,
        blue,
    }

    flags permissions {
        read,
        write,
    }
}
";
        var directory = Wit.Parse(wit);
        var interf = directory.Packages.Values.First().Versions.Values.First()
            .Definitions.Items[0] as WitInterface;

        var sb = new IndentedStringBuilder();
        GuestTypeWriter.WriteAllTypes(sb, interf!.Definitions);
        var code = sb.ToString();

        Assert.Contains("public struct Point", code);
        Assert.Contains("public enum Color", code);
        Assert.Contains("[System.Flags]", code);
        Assert.Contains("public enum Permissions", code);
    }

    #endregion

    #region GuestImportWriter - Resources

    [Fact]
    public void GenerateResourceClass()
    {
        var wit = @"
package my:pkg@1.0.0;

interface types {
    resource blob {
        constructor(init: list<u8>);
        write: func(bytes: list<u8>);
        read: func(n: u32) -> list<u8>;
        merge: static func(lhs: borrow<blob>, rhs: borrow<blob>) -> blob;
    }
}
";
        var directory = Wit.Parse(wit);
        var interf = directory.Packages.Values.First().Versions.Values.First()
            .Definitions.Items[0] as WitInterface;
        var resource = interf!.Definitions.Items[0] as WitResource;

        var sb = new IndentedStringBuilder();
        GuestImportWriter.WriteImportResource(sb, "my:pkg/types@1.0.0", resource!);
        var code = sb.ToString();

        // Class structure
        Assert.Contains("public class Blob : System.IDisposable", code);
        Assert.Contains("internal int Handle { get; }", code);
        Assert.Contains("private Blob(int handle)", code);

        // Constructor
        Assert.Contains("public unsafe Blob(", code);
        Assert.Contains("[constructor]blob", code);

        // Instance methods
        Assert.Contains("public unsafe void Write(", code);
        Assert.Contains("[method]blob.write", code);
        Assert.Contains("public unsafe System.Collections.Generic.List<byte> Read(", code);
        Assert.Contains("[method]blob.read", code);

        // Static method
        Assert.Contains("public static unsafe Blob Merge(", code);
        Assert.Contains("[static]blob.merge", code);

        // Dispose
        Assert.Contains("public void Dispose()", code);
        Assert.Contains("[resource-drop]blob", code);
    }

    [Fact]
    public void GenerateResourceWithFallibleConstructor()
    {
        var wit = @"
package my:pkg@1.0.0;

interface types {
    resource blob2 {
        constructor(init: list<u8>) -> result<blob2>;
    }
}
";
        var directory = Wit.Parse(wit);
        var interf = directory.Packages.Values.First().Versions.Values.First()
            .Definitions.Items[0] as WitInterface;
        var resource = interf!.Definitions.Items[0] as WitResource;

        var sb = new IndentedStringBuilder();
        GuestImportWriter.WriteImportResource(sb, "my:pkg/types@1.0.0", resource!);
        var code = sb.ToString();

        // Should have a static factory method instead of public constructor
        Assert.Contains("public class Blob2 : System.IDisposable", code);
        Assert.Contains("static unsafe Blob2 Create(", code);
        Assert.Contains("NotImplementedException", code);
    }

    [Fact]
    public void ResourceParserSeparatesStaticMethods()
    {
        var wit = @"
package my:pkg@1.0.0;

interface types {
    resource blob {
        constructor(init: list<u8>);
        write: func(bytes: list<u8>);
        merge: static func(lhs: borrow<blob>, rhs: borrow<blob>) -> blob;
    }
}
";
        var directory = Wit.Parse(wit);
        var interf = directory.Packages.Values.First().Versions.Values.First()
            .Definitions.Items[0] as WitInterface;
        var resource = interf!.Definitions.Items[0] as WitResource;

        Assert.NotNull(resource);
        Assert.Equal("blob", resource.Name);
        Assert.Single(resource.Constructors);
        Assert.Single(resource.Methods);    // write is instance method
        Assert.Single(resource.StaticMethods); // merge is static method
        Assert.Equal("write", resource.Methods[0].Name);
        Assert.Equal("merge", resource.StaticMethods[0].Name);
    }

    #endregion
}

/// <summary>
/// In-memory implementation of AdditionalText for testing source generators.
/// </summary>
internal sealed class InMemoryAdditionalText : AdditionalText
{
    private readonly string _text;

    public InMemoryAdditionalText(string path, string text)
    {
        Path = path;
        _text = text;
    }

    public override string Path { get; }

    public override Microsoft.CodeAnalysis.Text.SourceText? GetText(CancellationToken cancellationToken = default)
    {
        return Microsoft.CodeAnalysis.Text.SourceText.From(_text);
    }
}
