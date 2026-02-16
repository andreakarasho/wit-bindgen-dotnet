using WitBindgen.SourceGenerator;
using WitBindgen.SourceGenerator.Models;
using Xunit;

namespace WitBindgen.Tests;

public class ParserTests
{
    [Fact]
    public void ParseSimpleWorld()
    {
        var wit = @"
package my:pkg@1.0.0;

world hello-world {
    export run: func() -> string;
}
";
        var directory = Wit.Parse(wit);
        Assert.NotNull(directory);
        Assert.True(directory.Packages.Count > 0);

        var package = directory.Packages.Values.First();
        var version = package.Versions.Values.First();
        Assert.True(version.Worlds.ContainsKey("hello-world"));

        var world = version.Worlds["hello-world"];
        Assert.Equal("hello-world", world.Name);
        Assert.Equal("HelloWorld", world.CSharpName);
    }

    [Fact]
    public void ParseWorldWithImportAndExport()
    {
        var wit = @"
package my:pkg@1.0.0;

world greeter {
    import greet: func(name: string) -> string;
    export run: func() -> string;
}
";
        var directory = Wit.Parse(wit);
        var package = directory.Packages.Values.First();
        var version = package.Versions.Values.First();
        var world = version.Worlds["greeter"];

        var items = world.Definitions.Items;
        Assert.Equal(2, items.Length);

        var import = items[0] as WitWorldImport;
        Assert.NotNull(import);
        Assert.Equal("greet", import!.ImportName);
        Assert.IsType<WitFuncType>(import.Type);

        var funcType = (WitFuncType)import.Type;
        Assert.Single(funcType.Parameters);
        Assert.Equal("name", funcType.Parameters[0].Name);
        Assert.Equal(WitTypeKind.String, funcType.Parameters[0].Type.Kind);
        Assert.Single(funcType.Results);
        Assert.Equal(WitTypeKind.String, funcType.Results[0].Kind);

        var export = items[1] as WitWorldExport;
        Assert.NotNull(export);
        Assert.Equal("run", export!.ExportName);
    }

    [Fact]
    public void ParseRecord()
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
        var package = directory.Packages.Values.First();
        var version = package.Versions.Values.First();

        var interf = version.Definitions.Items[0] as WitInterface;
        Assert.NotNull(interf);
        Assert.Equal("types", interf!.Name);

        var record = interf.Definitions.Items[0] as WitRecord;
        Assert.NotNull(record);
        Assert.Equal("point", record!.Name);
        Assert.Equal("Point", record.CSharpName);
        Assert.Equal(2, record.Fields.Length);
        Assert.Equal("x", record.Fields[0].Name);
        Assert.Equal(WitTypeKind.S32, record.Fields[0].Type.Kind);
    }

    [Fact]
    public void ParseEnum()
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
        var package = directory.Packages.Values.First();
        var version = package.Versions.Values.First();

        var interf = version.Definitions.Items[0] as WitInterface;
        Assert.NotNull(interf);

        var @enum = interf!.Definitions.Items[0] as WitEnum;
        Assert.NotNull(@enum);
        Assert.Equal("color", @enum!.Name);
        Assert.Equal("Color", @enum.CSharpName);
        Assert.Equal(3, @enum.Values.Length);
        Assert.Equal("red", @enum.Values[0].Name);
        Assert.Equal("Red", @enum.Values[0].CSharpName);
    }

    [Fact]
    public void ParseFlags()
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
        var package = directory.Packages.Values.First();
        var version = package.Versions.Values.First();

        var interf = version.Definitions.Items[0] as WitInterface;
        Assert.NotNull(interf);

        var flags = interf!.Definitions.Items[0] as WitFlags;
        Assert.NotNull(flags);
        Assert.Equal("permissions", flags!.Name);
        Assert.Equal(3, flags.Values.Length);
    }

    [Fact]
    public void ParseVariant()
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
        var package = directory.Packages.Values.First();
        var version = package.Versions.Values.First();

        var interf = version.Definitions.Items[0] as WitInterface;
        Assert.NotNull(interf);

        var variant = interf!.Definitions.Items[0] as WitVariant;
        Assert.NotNull(variant);
        Assert.Equal("my-variant", variant!.Name);
        Assert.Equal(3, variant.Cases.Length);
        Assert.Equal("case-a", variant.Cases[0].Name);
        Assert.NotNull(variant.Cases[0].Type);
        Assert.Equal(WitTypeKind.S32, variant.Cases[0].Type!.Kind);
        Assert.Null(variant.Cases[2].Type);
    }

    [Fact]
    public void ParseFunctionWithMultipleParams()
    {
        var wit = @"
package my:pkg@1.0.0;

world test-world {
    import add: func(a: s32, b: s32) -> s32;
}
";
        var directory = Wit.Parse(wit);
        var package = directory.Packages.Values.First();
        var version = package.Versions.Values.First();
        var world = version.Worlds["test-world"];

        var import = world.Definitions.Items[0] as WitWorldImport;
        Assert.NotNull(import);

        var funcType = import!.Type as WitFuncType;
        Assert.NotNull(funcType);
        Assert.Equal(2, funcType!.Parameters.Length);
        Assert.Equal("a", funcType.Parameters[0].Name);
        Assert.Equal("b", funcType.Parameters[1].Name);
        Assert.Single(funcType.Results);
        Assert.Equal(WitTypeKind.S32, funcType.Results[0].Kind);
    }

    [Fact]
    public void ParseResultType()
    {
        var wit = @"
package my:pkg@1.0.0;

world test-world {
    import try-something: func() -> result<string, string>;
}
";
        var directory = Wit.Parse(wit);
        var package = directory.Packages.Values.First();
        var version = package.Versions.Values.First();
        var world = version.Worlds["test-world"];

        var import = world.Definitions.Items[0] as WitWorldImport;
        Assert.NotNull(import);

        var funcType = import!.Type as WitFuncType;
        Assert.NotNull(funcType);
        Assert.Single(funcType!.Results);

        var resultType = funcType.Results[0] as WitResultType;
        Assert.NotNull(resultType);
        Assert.Equal(WitTypeKind.String, resultType!.OkType.Kind);
        Assert.Equal(WitTypeKind.String, resultType.ErrType.Kind);
    }

    [Fact]
    public void ParseOptionType()
    {
        var wit = @"
package my:pkg@1.0.0;

world test-world {
    import find: func(key: string) -> option<string>;
}
";
        var directory = Wit.Parse(wit);
        var package = directory.Packages.Values.First();
        var version = package.Versions.Values.First();
        var world = version.Worlds["test-world"];

        var import = world.Definitions.Items[0] as WitWorldImport;
        var funcType = import!.Type as WitFuncType;
        Assert.Single(funcType!.Results);

        var optionType = funcType.Results[0] as WitOptionType;
        Assert.NotNull(optionType);
        Assert.Equal(WitTypeKind.String, optionType!.ElementType.Kind);
    }

    [Fact]
    public void ParseListType()
    {
        var wit = @"
package my:pkg@1.0.0;

world test-world {
    import get-items: func() -> list<u32>;
}
";
        var directory = Wit.Parse(wit);
        var package = directory.Packages.Values.First();
        var version = package.Versions.Values.First();
        var world = version.Worlds["test-world"];

        var import = world.Definitions.Items[0] as WitWorldImport;
        var funcType = import!.Type as WitFuncType;
        Assert.Single(funcType!.Results);

        var listType = funcType.Results[0] as WitListType;
        Assert.NotNull(listType);
        Assert.Equal(WitTypeKind.U32, listType!.ElementType.Kind);
    }

    [Fact]
    public void ParseUseStatement()
    {
        var wit = @"
package my:pkg@1.0.0;

interface base-types {
    record point {
        x: s32,
        y: s32,
    }
}

interface drawing {
    use base-types.{point};

    draw-point: func(p: point);
}
";
        var directory = Wit.Parse(wit);
        var package = directory.Packages.Values.First();
        var version = package.Versions.Values.First();
        Assert.Equal(2, version.Definitions.Items.Length);

        var drawing = version.Definitions.Items[1] as WitInterface;
        Assert.NotNull(drawing);
        Assert.Equal("drawing", drawing!.Name);
    }

    [Fact]
    public void ParseResource()
    {
        var wit = @"
package my:pkg@1.0.0;

interface io {
    resource output-stream {
        constructor();
        write: func(data: list<u8>) -> result;
    }
}
";
        var directory = Wit.Parse(wit);
        var package = directory.Packages.Values.First();
        var version = package.Versions.Values.First();

        var interf = version.Definitions.Items[0] as WitInterface;
        Assert.NotNull(interf);

        var resource = interf!.Definitions.Items[0] as WitResource;
        Assert.NotNull(resource);
        Assert.Equal("output-stream", resource!.Name);
        Assert.Single(resource.Constructors);
        Assert.Single(resource.Methods);
    }

    [Fact]
    public void StringUtilsConvertsKebabCase()
    {
        Assert.Equal("FooBar", StringUtils.GetName("foo-bar"));
        Assert.Equal("HelloWorld", StringUtils.GetName("hello-world"));
        Assert.Equal("X", StringUtils.GetName("x"));
        Assert.Equal("fooBar", StringUtils.GetName("foo-bar", uppercaseFirst: false));
    }

    [Fact]
    public void ParseMultiplePackagesInDirectoryFails()
    {
        var dir = new WitRawDirectory(
            Path: "/test",
            Files: new[]
            {
                "package my:pkg-a@1.0.0;",
                "package my:pkg-b@1.0.0;"
            }
        );

        var directory = Wit.Parse(dir);
        Assert.True(directory.Diagnostics.Length > 0);
    }
}
