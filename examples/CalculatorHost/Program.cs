using Wasmtime;

namespace CalculatorHost;

class Program
{
    static void Main(string[] args)
    {
        var wasmPath = args.Length > 0 ? args[0] : "calculator.wasm";

        if (!File.Exists(wasmPath))
        {
            Console.Error.WriteLine($"WASM file not found: {wasmPath}");
            Console.Error.WriteLine("Build the Calculator guest project to WASM first, then copy the .wasm file here.");
            return;
        }

        using var engine = new Engine();
        using var linker = new Linker(engine);

        // Register WASI P2 support (required by most components)
        linker.AddWasiP2();

        // Register our host-provided imports
        var imports = new CalculatorImportsImpl();
        linker.Define(imports);

        // Compile the WASM component
        var bytes = File.ReadAllBytes(wasmPath);
        using var component = Component.Compile(engine, bytes);

        // Create a store and instantiate
        using var store = new Store(engine);
        store.AddWasiP2();

        var instance = store.GetComponentInstance(component, linker);
        var exports = new Wit.Example.Calculator.CalculatorExports(instance, store);

        // Call the exported 'add' function
        Console.WriteLine("=== Calculator Host ===");
        Console.WriteLine();

        var addResult = exports.Add(3.5, 2.5);
        Console.WriteLine($"add(3.5, 2.5) = {addResult}");

        // Call the exported 'calculate' function with different operations
        // op: 0=add, 1=subtract, 2=multiply, 3=divide
        var ops = new (uint op, string name, double a, double b)[]
        {
            (0, "add",      10.0, 5.0),
            (1, "subtract", 10.0, 5.0),
            (2, "multiply", 10.0, 5.0),
            (3, "divide",   10.0, 3.0),
        };

        Console.WriteLine();
        foreach (var (op, name, a, b) in ops)
        {
            var result = exports.Calculate(op, a, b);
            Console.WriteLine($"calculate({name}, {a}, {b}) = {result}");
        }
    }
}
