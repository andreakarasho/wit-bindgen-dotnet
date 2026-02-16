using WitBindgen.SourceGenerator;
using WitBindgen.SourceGenerator.Generators.Guest;
using WitBindgen.SourceGenerator.Models;
using Xunit;

namespace WitBindgen.Tests;

public class CanonicalAbiTests
{
    private static readonly WitPackageNameVersion TestPackage = new(
        new WitPackageName(
            new EquatableArray<string>(new[] { "test" }),
            new EquatableArray<string>(new[] { "pkg" })
        ),
        new SemVer(1, 0, 0, "", "")
    );

    #region Flatten - Primitives

    [Theory]
    [InlineData(WitTypeKind.Bool)]
    [InlineData(WitTypeKind.U8)]
    [InlineData(WitTypeKind.U16)]
    [InlineData(WitTypeKind.U32)]
    [InlineData(WitTypeKind.S8)]
    [InlineData(WitTypeKind.S16)]
    [InlineData(WitTypeKind.S32)]
    [InlineData(WitTypeKind.Char)]
    public void FlattenPrimitiveToI32(WitTypeKind kind)
    {
        var type = GetPrimitiveType(kind);
        var flat = CanonicalAbi.Flatten(type);
        Assert.Single(flat);
        Assert.Equal(CoreWasmType.I32, flat[0]);
    }

    [Theory]
    [InlineData(WitTypeKind.U64)]
    [InlineData(WitTypeKind.S64)]
    public void FlattenPrimitiveToI64(WitTypeKind kind)
    {
        var type = GetPrimitiveType(kind);
        var flat = CanonicalAbi.Flatten(type);
        Assert.Single(flat);
        Assert.Equal(CoreWasmType.I64, flat[0]);
    }

    [Fact]
    public void FlattenF32()
    {
        var flat = CanonicalAbi.Flatten(WitType.F32);
        Assert.Single(flat);
        Assert.Equal(CoreWasmType.F32, flat[0]);
    }

    [Fact]
    public void FlattenF64()
    {
        var flat = CanonicalAbi.Flatten(WitType.F64);
        Assert.Single(flat);
        Assert.Equal(CoreWasmType.F64, flat[0]);
    }

    #endregion

    #region Flatten - Compound Types

    [Fact]
    public void FlattenStringToPtrLen()
    {
        var flat = CanonicalAbi.Flatten(WitType.String);
        Assert.Equal(2, flat.Count);
        Assert.Equal(CoreWasmType.I32, flat[0]);
        Assert.Equal(CoreWasmType.I32, flat[1]);
    }

    [Fact]
    public void FlattenListToPtrLen()
    {
        var list = new WitListType(WitType.U32);
        var flat = CanonicalAbi.Flatten(list);
        Assert.Equal(2, flat.Count);
        Assert.Equal(CoreWasmType.I32, flat[0]);
        Assert.Equal(CoreWasmType.I32, flat[1]);
    }

    [Fact]
    public void FlattenRecordFlattensAllFields()
    {
        // record { x: s32, y: f64 } → [i32, f64]
        var record = new WitRecordType(
            TestPackage,
            "point",
            new EquatableArray<WitField>(new[]
            {
                new WitField("x", WitType.S32),
                new WitField("y", WitType.F64)
            })
        );
        var flat = CanonicalAbi.Flatten(record);
        Assert.Equal(2, flat.Count);
        Assert.Equal(CoreWasmType.I32, flat[0]);
        Assert.Equal(CoreWasmType.F64, flat[1]);
    }

    [Fact]
    public void FlattenTupleFlattensAllElements()
    {
        // tuple<s32, f32, s64> → [i32, f32, i64]
        var tuple = new WitTupleType(
            new EquatableArray<WitType>(new WitType[] { WitType.S32, WitType.F32, WitType.S64 })
        );
        var flat = CanonicalAbi.Flatten(tuple);
        Assert.Equal(3, flat.Count);
        Assert.Equal(CoreWasmType.I32, flat[0]);
        Assert.Equal(CoreWasmType.F32, flat[1]);
        Assert.Equal(CoreWasmType.I64, flat[2]);
    }

    [Fact]
    public void FlattenOptionAddsDiscriminant()
    {
        // option<s32> → [i32 (disc), i32 (payload)]
        var option = new WitOptionType(WitType.S32);
        var flat = CanonicalAbi.Flatten(option);
        Assert.Equal(2, flat.Count);
        Assert.Equal(CoreWasmType.I32, flat[0]); // discriminant
        Assert.Equal(CoreWasmType.I32, flat[1]); // payload
    }

    [Fact]
    public void FlattenResultAddsDiscriminantPlusMaxPayload()
    {
        // result<s32, string> → [i32 (disc), i32, i32] (max of [i32] and [i32,i32])
        var result = new WitResultType(WitType.S32, WitType.String);
        var flat = CanonicalAbi.Flatten(result);
        Assert.Equal(3, flat.Count);
        Assert.Equal(CoreWasmType.I32, flat[0]); // discriminant
        Assert.Equal(CoreWasmType.I32, flat[1]);
        Assert.Equal(CoreWasmType.I32, flat[2]);
    }

    [Fact]
    public void FlattenResultNoError()
    {
        // result<s32> → [i32 (disc), i32]
        var result = new WitResultNoErrorType(WitType.S32);
        var flat = CanonicalAbi.Flatten(result);
        Assert.Equal(2, flat.Count);
        Assert.Equal(CoreWasmType.I32, flat[0]); // discriminant
        Assert.Equal(CoreWasmType.I32, flat[1]); // ok payload
    }

    [Fact]
    public void FlattenResultNoResult()
    {
        // result<_, string> → [i32 (disc), i32, i32]
        var result = new WitResultNoResultType(WitType.String);
        var flat = CanonicalAbi.Flatten(result);
        Assert.Equal(3, flat.Count);
        Assert.Equal(CoreWasmType.I32, flat[0]); // discriminant
        Assert.Equal(CoreWasmType.I32, flat[1]); // ptr
        Assert.Equal(CoreWasmType.I32, flat[2]); // len
    }

    [Fact]
    public void FlattenEmptyResult()
    {
        var flat = CanonicalAbi.Flatten(WitType.EmptyResult);
        // Empty result = discriminant only
        Assert.Single(flat);
        Assert.Equal(CoreWasmType.I32, flat[0]);
    }

    #endregion

    #region FlatCount

    [Fact]
    public void FlatCountPrimitive()
    {
        Assert.Equal(1, CanonicalAbi.FlatCount(WitType.S32));
        Assert.Equal(1, CanonicalAbi.FlatCount(WitType.F64));
    }

    [Fact]
    public void FlatCountString()
    {
        Assert.Equal(2, CanonicalAbi.FlatCount(WitType.String));
    }

    [Fact]
    public void FlatCountRecord()
    {
        var record = new WitRecordType(
            TestPackage,
            "point",
            new EquatableArray<WitField>(new[]
            {
                new WitField("x", WitType.S32),
                new WitField("y", WitType.S32),
                new WitField("z", WitType.S32),
            })
        );
        Assert.Equal(3, CanonicalAbi.FlatCount(record));
    }

    #endregion

    #region ShouldUseRetPtr

    [Fact]
    public void ShouldUseRetPtrForVoidReturn()
    {
        var func = new WitFuncType(
            new EquatableArray<WitFuncParameter>(Array.Empty<WitFuncParameter>()),
            new EquatableArray<WitType>(Array.Empty<WitType>())
        );
        Assert.False(CanonicalAbi.ShouldUseRetPtr(func));
    }

    [Fact]
    public void ShouldNotUseRetPtrForSinglePrimitiveReturn()
    {
        var func = new WitFuncType(
            new EquatableArray<WitFuncParameter>(Array.Empty<WitFuncParameter>()),
            new EquatableArray<WitType>(new[] { WitType.S32 })
        );
        Assert.False(CanonicalAbi.ShouldUseRetPtr(func));
    }

    [Fact]
    public void ShouldUseRetPtrForStringReturn()
    {
        // string flattens to [i32, i32] = 2 > MaxFlatResults(1)
        var func = new WitFuncType(
            new EquatableArray<WitFuncParameter>(Array.Empty<WitFuncParameter>()),
            new EquatableArray<WitType>(new[] { WitType.String })
        );
        Assert.True(CanonicalAbi.ShouldUseRetPtr(func));
    }

    [Fact]
    public void ShouldUseRetPtrForRecordReturn()
    {
        var record = new WitRecordType(
            TestPackage,
            "point",
            new EquatableArray<WitField>(new[]
            {
                new WitField("x", WitType.S32),
                new WitField("y", WitType.S32),
            })
        );
        var func = new WitFuncType(
            new EquatableArray<WitFuncParameter>(Array.Empty<WitFuncParameter>()),
            new EquatableArray<WitType>(new WitType[] { record })
        );
        Assert.True(CanonicalAbi.ShouldUseRetPtr(func));
    }

    #endregion

    #region ShouldSpillParams

    [Fact]
    public void ShouldNotSpillFewParams()
    {
        var func = new WitFuncType(
            new EquatableArray<WitFuncParameter>(new[]
            {
                new WitFuncParameter("a", WitType.S32),
                new WitFuncParameter("b", WitType.S32),
            }),
            new EquatableArray<WitType>(Array.Empty<WitType>())
        );
        Assert.False(CanonicalAbi.ShouldSpillParams(func));
    }

    [Fact]
    public void ShouldSpillManyParams()
    {
        // 17 i32 params > MAX_FLAT_PARAMS(16)
        var parameters = new WitFuncParameter[17];
        for (int i = 0; i < 17; i++)
            parameters[i] = new WitFuncParameter($"p{i}", WitType.S32);

        var func = new WitFuncType(
            new EquatableArray<WitFuncParameter>(parameters),
            new EquatableArray<WitType>(Array.Empty<WitType>())
        );
        Assert.True(CanonicalAbi.ShouldSpillParams(func));
    }

    [Fact]
    public void ShouldSpillStringsCountAsTwoSlots()
    {
        // 9 string params = 18 flat slots > 16
        var parameters = new WitFuncParameter[9];
        for (int i = 0; i < 9; i++)
            parameters[i] = new WitFuncParameter($"s{i}", WitType.String);

        var func = new WitFuncType(
            new EquatableArray<WitFuncParameter>(parameters),
            new EquatableArray<WitType>(Array.Empty<WitType>())
        );
        Assert.True(CanonicalAbi.ShouldSpillParams(func));
    }

    #endregion

    #region MemorySize

    [Theory]
    [InlineData(WitTypeKind.Bool, 1)]
    [InlineData(WitTypeKind.U8, 1)]
    [InlineData(WitTypeKind.S8, 1)]
    [InlineData(WitTypeKind.U16, 2)]
    [InlineData(WitTypeKind.S16, 2)]
    [InlineData(WitTypeKind.U32, 4)]
    [InlineData(WitTypeKind.S32, 4)]
    [InlineData(WitTypeKind.Char, 4)]
    [InlineData(WitTypeKind.F32, 4)]
    [InlineData(WitTypeKind.U64, 8)]
    [InlineData(WitTypeKind.S64, 8)]
    [InlineData(WitTypeKind.F64, 8)]
    public void MemorySizePrimitives(WitTypeKind kind, int expectedSize)
    {
        var type = GetPrimitiveType(kind);
        Assert.Equal(expectedSize, CanonicalAbi.MemorySize(type));
    }

    [Fact]
    public void MemorySizeString()
    {
        Assert.Equal(8, CanonicalAbi.MemorySize(WitType.String)); // ptr(4) + len(4)
    }

    [Fact]
    public void MemorySizeList()
    {
        var list = new WitListType(WitType.U8);
        Assert.Equal(8, CanonicalAbi.MemorySize(list)); // ptr(4) + len(4)
    }

    #endregion

    #region MemoryAlign

    [Theory]
    [InlineData(WitTypeKind.Bool, 1)]
    [InlineData(WitTypeKind.U8, 1)]
    [InlineData(WitTypeKind.S8, 1)]
    [InlineData(WitTypeKind.U16, 2)]
    [InlineData(WitTypeKind.S16, 2)]
    [InlineData(WitTypeKind.U32, 4)]
    [InlineData(WitTypeKind.S32, 4)]
    [InlineData(WitTypeKind.F32, 4)]
    [InlineData(WitTypeKind.U64, 8)]
    [InlineData(WitTypeKind.S64, 8)]
    [InlineData(WitTypeKind.F64, 8)]
    public void MemoryAlignPrimitives(WitTypeKind kind, int expectedAlign)
    {
        var type = GetPrimitiveType(kind);
        Assert.Equal(expectedAlign, CanonicalAbi.MemoryAlign(type));
    }

    [Fact]
    public void MemoryAlignString()
    {
        Assert.Equal(4, CanonicalAbi.MemoryAlign(WitType.String));
    }

    #endregion

    #region AlignTo

    [Theory]
    [InlineData(0, 4, 0)]
    [InlineData(1, 4, 4)]
    [InlineData(4, 4, 4)]
    [InlineData(5, 4, 8)]
    [InlineData(7, 8, 8)]
    [InlineData(8, 8, 8)]
    [InlineData(9, 8, 16)]
    public void AlignToValues(int value, int align, int expected)
    {
        Assert.Equal(expected, CanonicalAbi.AlignTo(value, align));
    }

    #endregion

    #region WitTypeToCS

    [Theory]
    [InlineData(WitTypeKind.Bool, "bool")]
    [InlineData(WitTypeKind.U8, "byte")]
    [InlineData(WitTypeKind.U16, "ushort")]
    [InlineData(WitTypeKind.U32, "uint")]
    [InlineData(WitTypeKind.U64, "ulong")]
    [InlineData(WitTypeKind.S8, "sbyte")]
    [InlineData(WitTypeKind.S16, "short")]
    [InlineData(WitTypeKind.S32, "int")]
    [InlineData(WitTypeKind.S64, "long")]
    [InlineData(WitTypeKind.F32, "float")]
    [InlineData(WitTypeKind.F64, "double")]
    [InlineData(WitTypeKind.String, "string")]
    public void WitTypeToCSPrimitives(WitTypeKind kind, string expected)
    {
        var type = GetPrimitiveType(kind);
        Assert.Equal(expected, CanonicalAbi.WitTypeToCS(type));
    }

    [Fact]
    public void WitTypeToCSOption()
    {
        var option = new WitOptionType(WitType.S32);
        Assert.Equal("int?", CanonicalAbi.WitTypeToCS(option));
    }

    [Fact]
    public void WitTypeToCSList()
    {
        var list = new WitListType(WitType.U8);
        Assert.Equal("System.Collections.Generic.List<byte>", CanonicalAbi.WitTypeToCS(list));
    }

    #endregion

    #region CoreTypeToCS

    [Theory]
    [InlineData(CoreWasmType.I32, "int")]
    [InlineData(CoreWasmType.I64, "long")]
    [InlineData(CoreWasmType.F32, "float")]
    [InlineData(CoreWasmType.F64, "double")]
    public void CoreTypeToCSValues(CoreWasmType coreType, string expected)
    {
        Assert.Equal(expected, CanonicalAbi.CoreTypeToCS(coreType));
    }

    #endregion

    private static WitType GetPrimitiveType(WitTypeKind kind) => kind switch
    {
        WitTypeKind.Bool => WitType.Bool,
        WitTypeKind.U8 => WitType.U8,
        WitTypeKind.U16 => WitType.U16,
        WitTypeKind.U32 => WitType.U32,
        WitTypeKind.U64 => WitType.U64,
        WitTypeKind.S8 => WitType.S8,
        WitTypeKind.S16 => WitType.S16,
        WitTypeKind.S32 => WitType.S32,
        WitTypeKind.S64 => WitType.S64,
        WitTypeKind.F32 => WitType.F32,
        WitTypeKind.F64 => WitType.F64,
        WitTypeKind.Char => WitType.Char,
        WitTypeKind.String => WitType.String,
        _ => WitType.S32
    };
}
