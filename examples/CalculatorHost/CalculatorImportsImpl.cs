namespace CalculatorHost;

/// <summary>
/// Host-side implementation of the calculator's imported functions.
/// The guest WASM component calls these when it needs host functionality.
/// </summary>
internal class CalculatorImportsImpl : Wit.Example.Calculator.CalculatorImports
{
    // logger interface: log(level, message)
    public override void Log(uint level, string message)
    {
        Console.WriteLine($"[guest] {message}");
    }

    // import get-precision: func() -> u32
    public override uint GetPrecision()
    {
        return 2;
    }

    // import get-precision2: func(a: list<string>) -> u32
    public override uint GetPrecision2(ReadOnlySpan<string> a)
    {
        return (uint)a.Length;
    }

    // import get-precision3: func(a: list<u32>) -> u32
    public override uint GetPrecision3(ReadOnlySpan<uint> a)
    {
        return (uint)a.Length;
    }

    // resource blob: constructor(init: list<u8>)
    public override Blob NewBlob(ReadOnlySpan<byte> init)
    {
        return new BlobImpl(init.ToArray());
    }

    // resource blob2: constructor(init: list<u8>) -> result<blob2>
    public override Blob2 NewBlob2(ReadOnlySpan<byte> init)
    {
        return new Blob2Impl(init.ToArray());
    }

    private class BlobImpl : Blob
    {
        private readonly List<byte> _data;

        public BlobImpl(byte[] init)
        {
            _data = new List<byte>(init);
        }

        public override void Write(ReadOnlySpan<byte> bytes)
        {
            _data.AddRange(bytes.ToArray());
        }

        public override byte[] Read(uint n)
        {
            var count = (int)Math.Min(n, _data.Count);
            return _data.GetRange(0, count).ToArray();
        }

        public override Blob Merge(Blob lhs, Blob rhs)
        {
            var lhsData = ((BlobImpl)lhs)._data;
            var rhsData = ((BlobImpl)rhs)._data;
            var merged = new byte[lhsData.Count + rhsData.Count];
            lhsData.CopyTo(merged);
            rhsData.CopyTo(merged, lhsData.Count);
            return new BlobImpl(merged);
        }

        public override void Dispose() { }
    }

    private class Blob2Impl : Blob2
    {
        private readonly byte[] _data;

        public Blob2Impl(byte[] init)
        {
            _data = init;
        }

        public override void Dispose() { }
    }
}
