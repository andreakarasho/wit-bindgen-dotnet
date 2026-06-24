using System.Reflection;
using Wasmtime;
using Xunit;

namespace WitBindgen.E2EHost;

/// <summary>
/// xUnit collection fixture that compiles the guest WASM component once and exposes the
/// generated host export bindings. Shared across every test in the collection so the
/// (relatively expensive) compile + instantiate happens a single time.
///
/// The component must be produced first by:
///     dotnet publish tests/WitBindgen.E2E/Guest -c Release
/// which emits WitBindgen.E2EGuest.wasm under the guest publish directory. We resolve that
/// path in <see cref="ResolveWasmPath"/> so the test run does not depend on a manual copy.
/// </summary>
public sealed class WasmComponentFixture : IDisposable
{
    private readonly Engine _engine;
    private readonly Linker _linker;
    private readonly Component _component;
    private readonly List<Store> _stores = new();

    public WasmComponentFixture()
    {
        var wasmPath = ResolveWasmPath();

        _engine = new Engine();
        _linker = new Linker(_engine);
        _linker.AddWasiP2();

        // The e2e-test world declares no host-provided imports that the toolchain can wire
        // up (see the skipped-features note), so no Linker.Define(imports) call is needed.

        var bytes = File.ReadAllBytes(wasmPath);
        _component = Component.Compile(_engine, bytes);
    }

    /// <summary>
    /// Returns a freshly-instantiated set of export bindings backed by a new Store. Each test
    /// uses its own instance so a trap in one call cannot poison ("cannot enter component
    /// instance") the calls made by other tests. The expensive compile is shared; only the
    /// cheap Store + instantiation is per-call.
    /// </summary>
    internal Wit.E2e.Test.E2eTestExports NewExports()
    {
        var store = new Store(_engine);
        store.AddWasiP2();
        _stores.Add(store);
        var instance = store.GetComponentInstance(_component, _linker);
        return new Wit.E2e.Test.E2eTestExports(instance, store);
    }

    /// <summary>
    /// Locates the built guest component. Search order:
    ///  1. WITBINDGEN_E2E_WASM environment variable (explicit override).
    ///  2. The wasm copied next to the test assembly (CopyToOutputDirectory).
    ///  3. The guest project's Release publish output, walking up from the test assembly.
    /// </summary>
    public static string ResolveWasmPath()
    {
        const string wasmName = "WitBindgen.E2EGuest.wasm";

        var fromEnv = Environment.GetEnvironmentVariable("WITBINDGEN_E2E_WASM");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv))
            return fromEnv;

        var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Directory.GetCurrentDirectory();

        var nextToAssembly = Path.Combine(asmDir, wasmName);
        if (File.Exists(nextToAssembly))
            return nextToAssembly;

        // Walk up to the repo's tests/WitBindgen.E2E directory, then into Guest's publish output.
        var dir = new DirectoryInfo(asmDir);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName, "Guest", "bin", "Release", "net10.0", "wasi-wasm", "publish", wasmName);
            if (File.Exists(candidate))
                return candidate;

            // Also accept a sibling layout where the E2E root holds Guest/.
            var e2eRoot = Path.Combine(dir.FullName, "tests", "WitBindgen.E2E");
            var candidate2 = Path.Combine(
                e2eRoot, "Guest", "bin", "Release", "net10.0", "wasi-wasm", "publish", wasmName);
            if (File.Exists(candidate2))
                return candidate2;

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {wasmName}. Build the guest component first with " +
            $"'dotnet publish tests/WitBindgen.E2E/Guest -c Release', or set WITBINDGEN_E2E_WASM " +
            $"to the path of the built .wasm.");
    }

    public void Dispose()
    {
        foreach (var store in _stores)
            store.Dispose();
        _component.Dispose();
        _linker.Dispose();
        _engine.Dispose();
    }
}

[CollectionDefinition("wasm")]
public sealed class WasmCollection : ICollectionFixture<WasmComponentFixture>
{
}
