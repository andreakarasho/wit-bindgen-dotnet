# wit-bindgen-dotnet

[![NuGet](https://img.shields.io/nuget/v/WitBindgen.SourceGenerator.svg)](https://www.nuget.org/packages/WitBindgen.SourceGenerator)
[![NuGet](https://img.shields.io/nuget/v/WitBindgen.Runtime.svg)](https://www.nuget.org/packages/WitBindgen.Runtime)
[![CI](https://github.com/andreakarasho/wit-bindgen-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/andreakarasho/wit-bindgen-dotnet/actions/workflows/ci.yml)

C# source generator that produces bindings from [WIT (WebAssembly Interface Types)](https://github.com/WebAssembly/component-model/blob/main/design/mvp/WIT.md) definitions, enabling .NET WebAssembly components using the [Component Model](https://component-model.bytecodealliance.org/).

## What it does

**wit-bindgen-dotnet** reads `.wit` files at compile time and generates C# code for both sides of the component boundary:

- **Guest side** (`WitBindgen.SourceGenerator`) — generates `DllImport` stubs for imports and `UnmanagedCallersOnly` exports, so your .NET code can be compiled to a WASM component via NativeAOT-LLVM.
- **Host side** (`Wasmtime.SourceGenerator` from [wasmtime-dotnet](https://github.com/andreakarasho/wasmtime-dotnet)) — generates an abstract `*Imports` class you implement to provide host functions, and a typed `*Exports` wrapper to call into the guest.

### Supported WIT types

| WIT type                                                                      | C# mapping                                                       |
| ----------------------------------------------------------------------------- | ---------------------------------------------------------------- |
| Primitives (`u8`..`u64`, `s8`..`s64`, `f32`, `f64`, `bool`, `char`, `string`) | Native C# types                                                  |
| `list<T>`                                                                     | `ReadOnlySpan<T>` (imports), arrays (exports)                    |
| `option<T>`                                                                   | `T?`                                                             |
| `result<T, E>`                                                                | Generated result types                                           |
| `record`                                                                      | `struct` with fields                                             |
| `enum`                                                                        | C# `enum`                                                        |
| `flags`                                                                       | C# `[Flags] enum`                                                |
| `variant`                                                                     | `struct` with discriminant + payload                             |
| `resource`                                                                    | Abstract class with handle table (host), DllImport stubs (guest) |
| `tuple<...>`                                                                  | C# tuples                                                        |
| `borrow<T>`                                                                   | Handle parameter                                                 |

## Project structure

```
wit-bindgen-dotnet/
  src/
    WitBindgen.SourceGenerator/   # Guest-side Roslyn source generator
    WitBindgen.Runtime/           # Runtime helpers for guest components
  examples/
    HelloWorld/                   # Minimal guest example
    Calculator/                   # Guest WASM component (NativeAOT-LLVM)
    CalculatorHost/               # Host that loads and runs the calculator
  tests/
    WitBindgen.Tests/             # Parser and code generation tests
  external/
    wasmtime-dotnet/              # Submodule: Wasmtime .NET bindings + host source generator
```

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- NativeAOT-LLVM workload (for publishing guest components to WASM)

## Building

Clone with submodules:

```bash
git clone --recurse-submodules https://github.com/andreakarasho/wit-bindgen-dotnet.git
cd wit-bindgen-dotnet
```

Build everything:

```bash
dotnet build WitBindgen.slnx
```

Run tests:

```bash
dotnet test WitBindgen.slnx
```

## Usage

### Writing a guest component

**1. Define your WIT world:**

```wit
// wit/hello.wit
package example:hello@1.0.0;

interface console {
    log: func(message: string);
}

world hello-world {
    import console;
    import get-name: func() -> string;
    export run: func() -> string;
}
```

**2. Create the project:**

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <RuntimeIdentifier>wasi-wasm</RuntimeIdentifier>
    </PropertyGroup>

    <ItemGroup>
        <PackageReference Include="WitBindgen.SourceGenerator" Version="0.1.0">
            <PrivateAssets>all</PrivateAssets>
            <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
        </PackageReference>
        <PackageReference Include="WitBindgen.Runtime" Version="0.1.0" />
    </ItemGroup>

    <ItemGroup>
        <WasmComponentTypeWit Include="wit\hello.wit" />
    </ItemGroup>
</Project>
```

The NuGet package sets WASI-WASM defaults (`AllowUnsafeBlocks`, `SelfContained`, etc.) and adds the NativeAOT-LLVM compiler packages automatically. To override the LLVM version:

```xml
<PropertyGroup>
    <NativeAotLlvmVersion>10.0.0-preview.5.25277.114</NativeAotLlvmVersion>
</PropertyGroup>
```

**3. Implement the exports:**

The source generator creates partial classes in the `Wit.<Namespace>.<Package>` namespace. You implement the exported functions as partial methods:

```csharp
namespace Wit.Example.Hello;

public static partial class HelloWorld
{
    public static partial string Run()
    {
        // Call imported functions
        var name = Imports.GetName();
        Console.Log($"Hello, {name}!");
        return $"Hello, {name}!";
    }
}
```

**4. Publish to WASM:**

```bash
dotnet publish -c Release
```

### Writing a host application

**1. Create the project** referencing `Wasmtime` and its source generator, with the same WIT file:

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>net10.0</TargetFramework>
        <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    </PropertyGroup>

    <ItemGroup>
        <ProjectReference Include="path/to/Wasmtime.csproj" />
        <ProjectReference Include="path/to/Wasmtime.SourceGenerator.csproj"
                          OutputItemType="Analyzer"
                          ReferenceOutputAssembly="false" />
    </ItemGroup>

    <ItemGroup>
        <AdditionalFiles Include="path/to/hello.wit" />
    </ItemGroup>
</Project>
```

**2. Implement the imports** by subclassing the generated abstract `*Imports` class:

```csharp
class HelloImportsImpl : Wit.Example.Hello.HelloWorldImports
{
    public override void Log(string message)
    {
        Console.WriteLine($"[guest] {message}");
    }

    public override string GetName()
    {
        return "World";
    }
}
```

**3. Load and run the component:**

```csharp
using Wasmtime;

using var engine = new Engine();
using var linker = new Linker(engine);
linker.AddWasiP2();

var imports = new HelloImportsImpl();
linker.Define(imports);

var bytes = File.ReadAllBytes("hello.wasm");
using var component = Component.Compile(engine, bytes);
using var store = new Store(engine);
store.AddWasiP2();

var instance = store.GetComponentInstance(component, linker);
var exports = new Wit.Example.Hello.HelloWorldExports(instance, store);

var result = exports.Run();
Console.WriteLine(result); // "Hello, World!"
```

## How it works

```
                    ┌──────────────┐
                    │  .wit file   │
                    └──────┬───────┘
                           │
              ┌────────────┴────────────┐
              │                         │
   ┌──────────▼──────────┐  ┌──────────▼──────────┐
   │ WitBindgen.Source    │  │ Wasmtime.Source      │
   │ Generator (guest)    │  │ Generator (host)     │
   └──────────┬──────────┘  └──────────┬──────────┘
              │                         │
   ┌──────────▼──────────┐  ┌──────────▼──────────┐
   │  DllImport stubs    │  │  Abstract *Imports   │
   │  + export wrappers  │  │  + typed *Exports    │
   └──────────┬──────────┘  └──────────┬──────────┘
              │                         │
   ┌──────────▼──────────┐  ┌──────────▼──────────┐
   │  NativeAOT-LLVM     │  │  Wasmtime runtime   │
   │  → .wasm component  │  │  → loads .wasm       │
   └─────────────────────┘  └─────────────────────┘
```

Both generators read the same `.wit` file and produce complementary code. The guest compiles to a `.wasm` component that the host loads and runs via Wasmtime.

## License

Apache-2.0 WITH LLVM-exception
