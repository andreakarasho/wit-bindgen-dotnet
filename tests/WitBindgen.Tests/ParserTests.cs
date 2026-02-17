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

    #region ECS WIT - Resources, Variants, Borrows

    [Fact]
    public void ParseEcsResources()
    {
        var wit = @"
package tecs:ecs;

interface ecs {
    resource app {
        add-systems: func(stage: stage-label, systems: list<borrow<system>>);
    }

    resource system {
        constructor(name: string);
        add-commands: func();
        add-query: func(query: list<query-term>);
        after: func(other: borrow<system>);
        before: func(other: borrow<system>);
    }

    resource commands {
        spawn: func() -> entity-commands;
        entity: func(entity: entity) -> entity-commands;
        despawn: func(entity: entity);
        remove-component: func(entity: entity, type-path: type-path);
    }

    resource entity-commands {
        id: func() -> entity;
        remove: func(types: list<type-path>);
        despawn: func();
    }

    record entity {
        id: s32,
        generation: s32,
    }

    resource query {
        iter: func() -> option<entity>;
    }

    type type-path = string;

    variant stage-label {
        startup,
        first,
        pre-update,
        update,
        post-update,
        last,
        custom(string),
    }

    variant query-term {
        ref(type-path),
        mut(type-path),
        %with(type-path),
        without(type-path),
    }
}
";
        var directory = Wit.Parse(wit);
        Assert.True(directory.Diagnostics.IsDefaultOrEmpty);

        var package = directory.Packages.Values.First();
        var version = package.Versions.Values.First();

        var interf = version.Definitions.Items[0] as WitInterface;
        Assert.NotNull(interf);
        Assert.Equal("ecs", interf!.Name);

        // Resources
        var resources = interf.Definitions.Items.OfType<WitResource>().ToArray();
        Assert.Equal(5, resources.Length);

        var app = resources.First(r => r.Name == "app");
        Assert.Single(app.Methods);
        Assert.Equal("add-systems", app.Methods[0].Name);

        var system = resources.First(r => r.Name == "system");
        Assert.Single(system.Constructors);
        Assert.Equal(4, system.Methods.Length);

        var commands = resources.First(r => r.Name == "commands");
        Assert.Equal(4, commands.Methods.Length);

        var entityCommands = resources.First(r => r.Name == "entity-commands");
        Assert.Equal(3, entityCommands.Methods.Length);

        var query = resources.First(r => r.Name == "query");
        Assert.Single(query.Methods);

        // Record
        var records = interf.Definitions.Items.OfType<WitRecord>().ToArray();
        Assert.Single(records);
        Assert.Equal("entity", records[0].Name);
        Assert.Equal(2, records[0].Fields.Length);
        Assert.Equal("id", records[0].Fields[0].Name);
        Assert.Equal(WitTypeKind.S32, records[0].Fields[0].Type.Kind);

        // Type alias
        var aliases = interf.Definitions.Items.OfType<WitTypeAlias>().ToArray();
        Assert.Single(aliases);
        Assert.Equal("type-path", aliases[0].Name);
        Assert.Equal(WitTypeKind.String, aliases[0].Type.Kind);

        // Variants
        var variants = interf.Definitions.Items.OfType<WitVariant>().ToArray();
        Assert.Equal(2, variants.Length);

        var stageLabel = variants.First(v => v.Name == "stage-label");
        Assert.Equal(7, stageLabel.Cases.Length);
        Assert.Null(stageLabel.Cases[0].Type); // startup has no payload
        Assert.NotNull(stageLabel.Cases[6].Type); // custom(string)
        Assert.Equal(WitTypeKind.String, stageLabel.Cases[6].Type!.Kind);

        var queryTerm = variants.First(v => v.Name == "query-term");
        Assert.Equal(4, queryTerm.Cases.Length);
        // All query-term cases have a type-path (User type) payload
        foreach (var @case in queryTerm.Cases)
        {
            Assert.NotNull(@case.Type);
        }
    }

    [Fact]
    public void ParseEcsVariantStageLabel()
    {
        var wit = @"
package tecs:ecs;

interface ecs {
    variant stage-label {
        startup,
        first,
        pre-update,
        update,
        post-update,
        last,
        custom(string),
    }
}
";
        var directory = Wit.Parse(wit);
        var interf = directory.Packages.Values.First().Versions.Values.First()
            .Definitions.Items[0] as WitInterface;
        var variant = interf!.Definitions.Items[0] as WitVariant;

        Assert.NotNull(variant);
        Assert.Equal("stage-label", variant!.Name);
        Assert.Equal(7, variant.Cases.Length);

        // No-payload cases
        Assert.Equal("startup", variant.Cases[0].Name);
        Assert.Null(variant.Cases[0].Type);
        Assert.Equal("first", variant.Cases[1].Name);
        Assert.Null(variant.Cases[1].Type);
        Assert.Equal("pre-update", variant.Cases[2].Name);
        Assert.Null(variant.Cases[2].Type);

        // Payload case
        Assert.Equal("custom", variant.Cases[6].Name);
        Assert.NotNull(variant.Cases[6].Type);
        Assert.Equal(WitTypeKind.String, variant.Cases[6].Type!.Kind);
    }

    [Fact]
    public void ParseResourceWithBorrowParam()
    {
        var wit = @"
package tecs:ecs;

interface ecs {
    resource system {
        constructor(name: string);
        after: func(other: borrow<system>);
        before: func(other: borrow<system>);
    }
}
";
        var directory = Wit.Parse(wit);
        var interf = directory.Packages.Values.First().Versions.Values.First()
            .Definitions.Items[0] as WitInterface;
        var resource = interf!.Definitions.Items[0] as WitResource;

        Assert.NotNull(resource);
        Assert.Equal("system", resource!.Name);
        Assert.Single(resource.Constructors);
        Assert.Equal(2, resource.Methods.Length);

        // Check borrow params
        var afterFunc = resource.Methods[0].Type as WitFuncType;
        Assert.NotNull(afterFunc);
        Assert.Single(afterFunc!.Parameters);
        Assert.Equal("other", afterFunc.Parameters[0].Name);
        Assert.Equal(WitTypeKind.Borrow, afterFunc.Parameters[0].Type.Kind);
    }

    [Fact]
    public void ParseResourceMethodReturnsOption()
    {
        var wit = @"
package tecs:ecs;

interface ecs {
    record entity {
        id: s32,
        generation: s32,
    }

    resource query {
        iter: func() -> option<entity>;
    }
}
";
        var directory = Wit.Parse(wit);
        var interf = directory.Packages.Values.First().Versions.Values.First()
            .Definitions.Items[0] as WitInterface;
        var resource = interf!.Definitions.Items.OfType<WitResource>().First();

        Assert.Equal("query", resource.Name);
        var iterFunc = resource.Methods[0].Type as WitFuncType;
        Assert.NotNull(iterFunc);
        Assert.Single(iterFunc!.Results);
        Assert.Equal(WitTypeKind.Option, iterFunc.Results[0].Kind);

        var optionType = iterFunc.Results[0] as WitOptionType;
        Assert.NotNull(optionType);
        // Element type is a custom type reference (entity)
        Assert.Equal(WitTypeKind.User, optionType!.ElementType.Kind);
    }

    [Fact]
    public void ParseWorldWithCrossPackageUse()
    {
        var wit = @"
package tecs:example;

world guest {
    import tecs:ecs/ecs;

    use tecs:ecs/ecs.{app, query, commands, entity};

    import get-position: func(q: borrow<query>, index: u8) -> position;
    import set-position: func(q: borrow<query>, index: u8, value: position);
    import get-velocity: func(q: borrow<query>, index: u8) -> velocity;
    import set-velocity: func(q: borrow<query>, index: u8, value: velocity);
    import commands-set-position: func(cmds: borrow<commands>, entity: entity, value: position);
    import commands-set-velocity: func(cmds: borrow<commands>, entity: entity, value: velocity);

    import get-positions: func(q: borrow<query>, index: u8) -> list<position>;
    import set-positions: func(q: borrow<query>, index: u8, values: list<position>);
    import get-velocities: func(q: borrow<query>, index: u8) -> list<velocity>;
    import set-velocities: func(q: borrow<query>, index: u8, values: list<velocity>);

    record position { x: f32, y: f32 }
    record velocity { x: f32, y: f32 }

    export setup: func(app: app);
    export run-system: func(index: u32, query: option<query>, commands: option<commands>);
}
";
        var directory = Wit.Parse(wit);
        Assert.True(directory.Diagnostics.IsDefaultOrEmpty);

        var package = directory.Packages.Values.First(p => p.Versions.Values.Any(v => v.Worlds.ContainsKey("guest")));
        var version = package.Versions.Values.First();
        var world = version.Worlds["guest"];

        Assert.Equal("guest", world.Name);
        Assert.Equal("Guest", world.CSharpName);

        // Check use statement is present
        var uses = world.Definitions.Items.OfType<WitUse>().ToArray();
        Assert.Single(uses);
        Assert.Equal(4, uses[0].Items.Length);

        // Check records
        var records = world.Definitions.Items.OfType<WitRecord>().ToArray();
        Assert.Equal(2, records.Length);
        Assert.Equal("position", records[0].Name);
        Assert.Equal("velocity", records[1].Name);

        // Check exports
        var exports = world.Definitions.Items.OfType<WitWorldExport>().ToArray();
        Assert.Equal(2, exports.Length);

        var setupExport = exports.First(e => e.ExportName == "setup");
        var setupFunc = setupExport.Type as WitFuncType;
        Assert.NotNull(setupFunc);
        Assert.Single(setupFunc!.Parameters);
        // app param is a use-imported type
        Assert.Equal("app", setupFunc.Parameters[0].Name);

        var runSystemExport = exports.First(e => e.ExportName == "run-system");
        var runSystemFunc = runSystemExport.Type as WitFuncType;
        Assert.NotNull(runSystemFunc);
        Assert.Equal(3, runSystemFunc!.Parameters.Length);
        Assert.Equal(WitTypeKind.U32, runSystemFunc.Parameters[0].Type.Kind);
        Assert.Equal(WitTypeKind.Option, runSystemFunc.Parameters[1].Type.Kind);
        Assert.Equal(WitTypeKind.Option, runSystemFunc.Parameters[2].Type.Kind);

        // Check imports with borrow params
        var imports = world.Definitions.Items.OfType<WitWorldImport>().ToArray();
        var getPositionImport = imports.First(i => i.ImportName == "get-position");
        var getPosFn = getPositionImport.Type as WitFuncType;
        Assert.NotNull(getPosFn);
        Assert.Equal(2, getPosFn!.Parameters.Length);
        Assert.Equal(WitTypeKind.Borrow, getPosFn.Parameters[0].Type.Kind);
        Assert.Equal(WitTypeKind.U8, getPosFn.Parameters[1].Type.Kind);

        // Check list imports
        var getPositionsImport = imports.First(i => i.ImportName == "get-positions");
        var getPositionsFn = getPositionsImport.Type as WitFuncType;
        Assert.NotNull(getPositionsFn);
        Assert.Single(getPositionsFn!.Results);
        Assert.Equal(WitTypeKind.List, getPositionsFn.Results[0].Kind);
    }

    [Fact]
    public void ParseTypeAlias()
    {
        var wit = @"
package tecs:ecs;

interface ecs {
    type type-path = string;
}
";
        var directory = Wit.Parse(wit);
        var interf = directory.Packages.Values.First().Versions.Values.First()
            .Definitions.Items[0] as WitInterface;

        var alias = interf!.Definitions.Items[0] as WitTypeAlias;
        Assert.NotNull(alias);
        Assert.Equal("type-path", alias!.Name);
        Assert.Equal(WitTypeKind.String, alias.Type.Kind);
    }

    [Fact]
    public void ParseResourceMethodWithListOfBorrow()
    {
        var wit = @"
package tecs:ecs;

interface ecs {
    resource system {
        constructor(name: string);
    }

    resource app {
        add-systems: func(stage: stage-label, systems: list<borrow<system>>);
    }

    variant stage-label {
        startup,
        update,
    }
}
";
        var directory = Wit.Parse(wit);
        var interf = directory.Packages.Values.First().Versions.Values.First()
            .Definitions.Items[0] as WitInterface;
        var app = interf!.Definitions.Items.OfType<WitResource>().First(r => r.Name == "app");

        var addSystemsFunc = app.Methods[0].Type as WitFuncType;
        Assert.NotNull(addSystemsFunc);
        Assert.Equal(2, addSystemsFunc!.Parameters.Length);

        // First param is variant stage-label
        var stageParam = addSystemsFunc.Parameters[0];
        Assert.Equal("stage", stageParam.Name);

        // Second param is list<borrow<system>>
        var systemsParam = addSystemsFunc.Parameters[1];
        Assert.Equal("systems", systemsParam.Name);
        Assert.Equal(WitTypeKind.List, systemsParam.Type.Kind);

        var listType = systemsParam.Type as WitListType;
        Assert.NotNull(listType);
        Assert.Equal(WitTypeKind.Borrow, listType!.ElementType.Kind);
    }

    [Fact]
    public void ParsePercentEscapedKeyword()
    {
        var wit = @"
package tecs:ecs;

interface ecs {
    type type-path = string;

    variant query-term {
        ref(type-path),
        mut(type-path),
        %with(type-path),
        without(type-path),
    }
}
";
        var directory = Wit.Parse(wit);
        var interf = directory.Packages.Values.First().Versions.Values.First()
            .Definitions.Items[0] as WitInterface;
        var variant = interf!.Definitions.Items.OfType<WitVariant>().First();

        Assert.Equal("query-term", variant.Name);
        Assert.Equal(4, variant.Cases.Length);
        Assert.Equal("ref", variant.Cases[0].Name);
        Assert.Equal("mut", variant.Cases[1].Name);
        // %with should parse to just "with" (percent-escaped keyword)
        Assert.Equal("with", variant.Cases[2].Name);
        Assert.Equal("without", variant.Cases[3].Name);
    }

    #endregion
}
