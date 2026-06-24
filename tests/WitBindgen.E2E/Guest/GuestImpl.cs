namespace Wit.E2e.Test;

/// <summary>
/// Guest-side implementation of every exported function in the e2e-test world.
/// Each function performs a real round-trip transform (echo / sum / project) so the
/// host can assert the value that comes back across the canonical-ABI boundary.
///
/// The commons types (Point, Color, SmallValue, ...) are brought into scope unqualified
/// via the project-wide `global using static Wit.E2e.Commons.Types;` (see GlobalUsings.cs),
/// matching the unqualified names the generator emits in the generated partial declarations.
/// </summary>
public static partial class E2eTest
{
    // ============ PRIMITIVES ============
    public static partial bool EchoBool(bool value) => value;
    public static partial byte EchoU8(byte value) => value;
    public static partial ushort EchoU16(ushort value) => value;
    public static partial uint EchoU32(uint value) => value;
    public static partial ulong EchoU64(ulong value) => value;
    public static partial sbyte EchoS8(sbyte value) => value;
    public static partial short EchoS16(short value) => value;
    public static partial int EchoS32(int value) => value;
    public static partial long EchoS64(long value) => value;
    public static partial float EchoF32(float value) => value;
    public static partial double EchoF64(double value) => value;

    // ============ STRING ============
    public static partial string EchoString(string value) => value;

    public static partial string Uppercase(string text) => text.ToUpperInvariant();

    // ============ LISTS ============
    public static partial string[] EchoListString(string[] values) => values;

    // Primitive lists are lifted as a zero-copy ReadOnlySpan<T> over WASM memory; .ToArray()
    // must observe the real data (regression for the free-before-use bug).
    public static partial byte[] EchoListU8(ReadOnlySpan<byte> values) => values.ToArray();
    public static partial uint[] EchoListU32(ReadOnlySpan<uint> values) => values.ToArray();
    public static partial double[] EchoListF64(ReadOnlySpan<double> values) => values.ToArray();

    // list<enum> is non-blittable -> lifted into a Color[].
    public static partial Color[] EchoListColor(Color[] values) => values;

    // ============ RECORDS ============
    public static partial Point EchoPoint(Point p) => p;
    public static partial Measurement EchoMeasurement(Measurement m) => m;
    public static partial TupleLike EchoTupleLike(TupleLike t) => t;
    public static partial Entity EchoEntity(Entity e) => e;

    // ============ OPTION ============
    public static partial uint? EchoOptionU32(uint? value) => value;
    public static partial Point? EchoOptionPoint(Point? value) => value;
    public static partial Color? EchoOptionColor(Color? value) => value;

    // ============ ENUM ============
    public static partial Color EchoColor(Color c) => c;

    public static partial string ColorName(Color c) => c switch
    {
        Color.Red => "red",
        Color.Green => "green",
        Color.Blue => "blue",
        _ => "unknown",
    };

    // ============ CHAR + FLAGS ============
    public static partial uint EchoChar(uint value) => value;
    public static partial Permission EchoPermission(Permission value) => value;

    // ============ VARIANT (returned across the ABI) ============
    public static partial SmallValue MakeSmallNone() => SmallValue.CreateNone();
    public static partial SmallValue MakeSmallByte(byte v) => SmallValue.CreateByte(v);
    public static partial SmallValue MakeSmallWord(ushort v) => SmallValue.CreateWord(v);

    public static partial MixedValue MakeMixedInt(int v) => MixedValue.CreateInt(v);
    public static partial MixedValue MakeMixedFloat(float v) => MixedValue.CreateFloat(v);

    public static partial LargeEnum MakeLargeEmpty() => LargeEnum.CreateCaseA();
    public static partial LargeEnum MakeLargeTagged(string s) => LargeEnum.CreateCaseE(s);
}
