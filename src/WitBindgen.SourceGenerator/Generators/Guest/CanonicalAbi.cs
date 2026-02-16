using WitBindgen.SourceGenerator.Models;

namespace WitBindgen.SourceGenerator.Generators.Guest;

/// <summary>
/// Core WASM value types used in the canonical ABI flattening.
/// </summary>
public enum CoreWasmType
{
    I32,
    I64,
    F32,
    F64
}

/// <summary>
/// Implements the canonical ABI flattening and memory layout rules
/// for mapping WIT types to core WASM types.
/// </summary>
public static class CanonicalAbi
{
    public const int MaxFlatParams = 16;
    public const int MaxFlatResults = 1;

    /// <summary>
    /// Flattens a WIT type into a sequence of core WASM types.
    /// </summary>
    public static List<CoreWasmType> Flatten(WitType type, ITypeContainerResolver? resolver = null)
    {
        var result = new List<CoreWasmType>();
        FlattenInto(type, result, resolver);
        return result;
    }

    private static void FlattenInto(WitType type, List<CoreWasmType> result, ITypeContainerResolver? resolver)
    {
        switch (type.Kind)
        {
            case WitTypeKind.Bool:
            case WitTypeKind.U8:
            case WitTypeKind.U16:
            case WitTypeKind.U32:
            case WitTypeKind.S8:
            case WitTypeKind.S16:
            case WitTypeKind.S32:
            case WitTypeKind.Char:
            case WitTypeKind.Enum:
            case WitTypeKind.Resource:
            case WitTypeKind.Borrow:
                result.Add(CoreWasmType.I32);
                break;

            case WitTypeKind.U64:
            case WitTypeKind.S64:
                result.Add(CoreWasmType.I64);
                break;

            case WitTypeKind.F32:
                result.Add(CoreWasmType.F32);
                break;

            case WitTypeKind.F64:
                result.Add(CoreWasmType.F64);
                break;

            case WitTypeKind.String:
            case WitTypeKind.List:
                // (ptr, len)
                result.Add(CoreWasmType.I32);
                result.Add(CoreWasmType.I32);
                break;

            case WitTypeKind.Record:
                if (type is WitRecordType recordType)
                {
                    foreach (var field in recordType.Fields)
                    {
                        FlattenInto(field.Type, result, resolver);
                    }
                }
                break;

            case WitTypeKind.Tuple:
                if (type is WitTupleType tupleType)
                {
                    foreach (var elementType in tupleType.ElementTypes)
                    {
                        FlattenInto(elementType, result, resolver);
                    }
                }
                break;

            case WitTypeKind.Option:
                // discriminant + payload
                result.Add(CoreWasmType.I32);
                if (type is WitOptionType optionType)
                {
                    FlattenInto(optionType.ElementType, result, resolver);
                }
                break;

            case WitTypeKind.Result:
                // discriminant + max(ok_flat, err_flat)
                result.Add(CoreWasmType.I32);
                FlattenResultPayload(type, result, resolver);
                break;

            case WitTypeKind.Variant:
                // discriminant + max(case_flat...)
                result.Add(CoreWasmType.I32);
                if (type is WitVariantType variantType)
                {
                    FlattenVariantPayload(variantType, result, resolver);
                }
                break;

            case WitTypeKind.Flags:
                if (type is WitFlagsType flagsType)
                {
                    // Flags use i32 per 32-bit word. For now, assume ≤32 flags = 1 word
                    result.Add(CoreWasmType.I32);
                }
                else
                {
                    result.Add(CoreWasmType.I32);
                }
                break;

            case WitTypeKind.User:
                if (type is WitCustomType customType && resolver != null)
                {
                    var resolved = customType.Resolve(resolver);
                    FlattenInto(resolved, result, resolver);
                }
                else
                {
                    // Fallback: treat as i32
                    result.Add(CoreWasmType.I32);
                }
                break;

            default:
                // Unknown types: default to i32
                result.Add(CoreWasmType.I32);
                break;
        }
    }

    private static void FlattenResultPayload(WitType type, List<CoreWasmType> result, ITypeContainerResolver? resolver)
    {
        List<CoreWasmType> okFlat, errFlat;

        switch (type)
        {
            case WitResultType resultType:
                okFlat = Flatten(resultType.OkType, resolver);
                errFlat = Flatten(resultType.ErrType, resolver);
                break;
            case WitResultNoErrorType noError:
                okFlat = Flatten(noError.OkType, resolver);
                errFlat = new List<CoreWasmType>();
                break;
            case WitResultNoResultType noResult:
                okFlat = new List<CoreWasmType>();
                errFlat = Flatten(noResult.ErrType, resolver);
                break;
            default:
                // Empty result
                return;
        }

        PadToMax(okFlat, errFlat, result);
    }

    private static void FlattenVariantPayload(WitVariantType variant, List<CoreWasmType> result, ITypeContainerResolver? resolver)
    {
        var maxFlat = new List<CoreWasmType>();

        foreach (var @case in variant.Values)
        {
            if (@case.Type is not null)
            {
                var caseFlat = Flatten(@case.Type, resolver);
                if (caseFlat.Count > maxFlat.Count)
                {
                    // Pad the new max to cover the old max, then swap
                    var padded = new List<CoreWasmType>(caseFlat);
                    maxFlat = padded;
                }
            }
        }

        result.AddRange(maxFlat);
    }

    /// <summary>
    /// Pads two flattened type lists to the same length (taking the max at each position),
    /// and appends the result.
    /// </summary>
    private static void PadToMax(List<CoreWasmType> a, List<CoreWasmType> b, List<CoreWasmType> output)
    {
        var maxLen = Math.Max(a.Count, b.Count);
        for (int i = 0; i < maxLen; i++)
        {
            var aType = i < a.Count ? a[i] : CoreWasmType.I32;
            var bType = i < b.Count ? b[i] : CoreWasmType.I32;
            // Use the "wider" type: i64 > f64 > f32 > i32
            output.Add(MaxType(aType, bType));
        }
    }

    private static CoreWasmType MaxType(CoreWasmType a, CoreWasmType b)
    {
        return (CoreWasmType)Math.Max((int)a, (int)b);
    }

    /// <summary>
    /// Returns the number of core WASM values a WIT type flattens to.
    /// </summary>
    public static int FlatCount(WitType type, ITypeContainerResolver? resolver = null)
    {
        return Flatten(type, resolver).Count;
    }

    /// <summary>
    /// Determines if function parameters should be spilled to linear memory.
    /// </summary>
    public static bool ShouldSpillParams(WitFuncType func, ITypeContainerResolver? resolver = null)
    {
        int count = 0;
        foreach (var param in func.Parameters)
        {
            count += FlatCount(param.Type, resolver);
            if (count > MaxFlatParams)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Determines if function results need a return pointer.
    /// </summary>
    public static bool ShouldUseRetPtr(WitFuncType func, ITypeContainerResolver? resolver = null)
    {
        if (func.Results.Length == 0)
            return false;

        int count = 0;
        foreach (var result in func.Results)
        {
            count += FlatCount(result, resolver);
            if (count > MaxFlatResults)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns the byte size of a WIT type in linear memory.
    /// </summary>
    public static int MemorySize(WitType type, ITypeContainerResolver? resolver = null)
    {
        switch (type.Kind)
        {
            case WitTypeKind.Bool:
            case WitTypeKind.U8:
            case WitTypeKind.S8:
                return 1;
            case WitTypeKind.U16:
            case WitTypeKind.S16:
                return 2;
            case WitTypeKind.U32:
            case WitTypeKind.S32:
            case WitTypeKind.Char:
            case WitTypeKind.F32:
            case WitTypeKind.Enum:
            case WitTypeKind.Resource:
            case WitTypeKind.Borrow:
                return 4;
            case WitTypeKind.U64:
            case WitTypeKind.S64:
            case WitTypeKind.F64:
                return 8;
            case WitTypeKind.String:
            case WitTypeKind.List:
                return 8; // ptr(4) + len(4)
            case WitTypeKind.Flags:
                return 4; // Assume ≤32 flags
            case WitTypeKind.Record:
                if (type is WitRecordType recordType)
                {
                    int size = 0;
                    foreach (var field in recordType.Fields)
                    {
                        var fieldAlign = MemoryAlign(field.Type, resolver);
                        size = AlignTo(size, fieldAlign);
                        size += MemorySize(field.Type, resolver);
                    }
                    if (recordType.Fields.Length > 0)
                        size = AlignTo(size, MemoryAlign(type, resolver));
                    return size;
                }
                return 0;
            case WitTypeKind.Tuple:
                if (type is WitTupleType tupleType)
                {
                    int size = 0;
                    foreach (var elem in tupleType.ElementTypes)
                    {
                        var elemAlign = MemoryAlign(elem, resolver);
                        size = AlignTo(size, elemAlign);
                        size += MemorySize(elem, resolver);
                    }
                    if (tupleType.ElementTypes.Length > 0)
                        size = AlignTo(size, MemoryAlign(type, resolver));
                    return size;
                }
                return 0;
            case WitTypeKind.Option:
                if (type is WitOptionType optionType)
                {
                    var payloadSize = MemorySize(optionType.ElementType, resolver);
                    var payloadAlign = MemoryAlign(optionType.ElementType, resolver);
                    // discriminant (1 byte) + padding + payload
                    var offset = AlignTo(1, payloadAlign);
                    return AlignTo(offset + payloadSize, Math.Max(1, payloadAlign));
                }
                return 1;
            case WitTypeKind.Result:
                return MemorySizeResult(type, resolver);
            case WitTypeKind.Variant:
                if (type is WitVariantType variantType)
                {
                    int maxPayload = 0;
                    int maxAlign = 4; // discriminant is i32
                    foreach (var c in variantType.Values)
                    {
                        if (c.Type is not null)
                        {
                            maxPayload = Math.Max(maxPayload, MemorySize(c.Type, resolver));
                            maxAlign = Math.Max(maxAlign, MemoryAlign(c.Type, resolver));
                        }
                    }
                    var discOffset = AlignTo(4, maxAlign);
                    return AlignTo(discOffset + maxPayload, maxAlign);
                }
                return 4;
            case WitTypeKind.User:
                if (type is WitCustomType customType && resolver != null)
                {
                    return MemorySize(customType.Resolve(resolver), resolver);
                }
                return 4;
            default:
                return 4;
        }
    }

    private static int MemorySizeResult(WitType type, ITypeContainerResolver? resolver)
    {
        int okSize = 0, errSize = 0;
        int okAlign = 1, errAlign = 1;

        switch (type)
        {
            case WitResultType rt:
                okSize = MemorySize(rt.OkType, resolver);
                errSize = MemorySize(rt.ErrType, resolver);
                okAlign = MemoryAlign(rt.OkType, resolver);
                errAlign = MemoryAlign(rt.ErrType, resolver);
                break;
            case WitResultNoErrorType noe:
                okSize = MemorySize(noe.OkType, resolver);
                okAlign = MemoryAlign(noe.OkType, resolver);
                break;
            case WitResultNoResultType nor:
                errSize = MemorySize(nor.ErrType, resolver);
                errAlign = MemoryAlign(nor.ErrType, resolver);
                break;
        }

        var payloadAlign = Math.Max(okAlign, errAlign);
        var payloadSize = Math.Max(okSize, errSize);
        var discAlign = Math.Max(4, payloadAlign);
        var offset = AlignTo(4, payloadAlign);
        return AlignTo(offset + payloadSize, discAlign);
    }

    /// <summary>
    /// Returns the alignment of a WIT type in linear memory.
    /// </summary>
    public static int MemoryAlign(WitType type, ITypeContainerResolver? resolver = null)
    {
        switch (type.Kind)
        {
            case WitTypeKind.Bool:
            case WitTypeKind.U8:
            case WitTypeKind.S8:
                return 1;
            case WitTypeKind.U16:
            case WitTypeKind.S16:
                return 2;
            case WitTypeKind.U32:
            case WitTypeKind.S32:
            case WitTypeKind.Char:
            case WitTypeKind.F32:
            case WitTypeKind.Enum:
            case WitTypeKind.Resource:
            case WitTypeKind.Borrow:
            case WitTypeKind.Flags:
            case WitTypeKind.String:
            case WitTypeKind.List:
                return 4;
            case WitTypeKind.U64:
            case WitTypeKind.S64:
            case WitTypeKind.F64:
                return 8;
            case WitTypeKind.Record:
                if (type is WitRecordType recordType)
                {
                    int maxAlign = 1;
                    foreach (var field in recordType.Fields)
                        maxAlign = Math.Max(maxAlign, MemoryAlign(field.Type, resolver));
                    return maxAlign;
                }
                return 1;
            case WitTypeKind.Tuple:
                if (type is WitTupleType tupleType)
                {
                    int maxAlign = 1;
                    foreach (var elem in tupleType.ElementTypes)
                        maxAlign = Math.Max(maxAlign, MemoryAlign(elem, resolver));
                    return maxAlign;
                }
                return 1;
            case WitTypeKind.Option:
                if (type is WitOptionType optionType)
                    return Math.Max(1, MemoryAlign(optionType.ElementType, resolver));
                return 1;
            case WitTypeKind.Result:
                return MemoryAlignResult(type, resolver);
            case WitTypeKind.Variant:
                if (type is WitVariantType variantType)
                {
                    int maxAlign = 4;
                    foreach (var c in variantType.Values)
                    {
                        if (c.Type is not null)
                            maxAlign = Math.Max(maxAlign, MemoryAlign(c.Type, resolver));
                    }
                    return maxAlign;
                }
                return 4;
            case WitTypeKind.User:
                if (type is WitCustomType customType && resolver != null)
                    return MemoryAlign(customType.Resolve(resolver), resolver);
                return 4;
            default:
                return 4;
        }
    }

    private static int MemoryAlignResult(WitType type, ITypeContainerResolver? resolver)
    {
        int okAlign = 1, errAlign = 1;

        switch (type)
        {
            case WitResultType rt:
                okAlign = MemoryAlign(rt.OkType, resolver);
                errAlign = MemoryAlign(rt.ErrType, resolver);
                break;
            case WitResultNoErrorType noe:
                okAlign = MemoryAlign(noe.OkType, resolver);
                break;
            case WitResultNoResultType nor:
                errAlign = MemoryAlign(nor.ErrType, resolver);
                break;
        }

        return Math.Max(4, Math.Max(okAlign, errAlign));
    }

    /// <summary>
    /// Aligns a value up to the specified alignment.
    /// </summary>
    public static int AlignTo(int value, int align)
    {
        return (value + align - 1) & ~(align - 1);
    }

    /// <summary>
    /// Returns the C# type string for a core WASM type used in DllImport signatures.
    /// </summary>
    public static string CoreTypeToCS(CoreWasmType type)
    {
        return type switch
        {
            CoreWasmType.I32 => "int",
            CoreWasmType.I64 => "long",
            CoreWasmType.F32 => "float",
            CoreWasmType.F64 => "double",
            _ => "int"
        };
    }

    /// <summary>
    /// Returns the C# type name for a WIT type (for high-level API).
    /// </summary>
    public static string WitTypeToCS(WitType type)
    {
        return type.Kind switch
        {
            WitTypeKind.Bool => "bool",
            WitTypeKind.U8 => "byte",
            WitTypeKind.U16 => "ushort",
            WitTypeKind.U32 => "uint",
            WitTypeKind.U64 => "ulong",
            WitTypeKind.S8 => "sbyte",
            WitTypeKind.S16 => "short",
            WitTypeKind.S32 => "int",
            WitTypeKind.S64 => "long",
            WitTypeKind.F32 => "float",
            WitTypeKind.F64 => "double",
            WitTypeKind.Char => "uint",
            WitTypeKind.String => "string",
            WitTypeKind.List => type is WitListType lt ? $"System.Collections.Generic.List<{WitTypeToCS(lt.ElementType)}>" : "object",
            WitTypeKind.Record => type is WitRecordType rt ? rt.CSharpName : "object",
            WitTypeKind.Enum => type is WitEnumType et ? et.CSharpName : "int",
            WitTypeKind.Flags => type is WitFlagsType ft ? ft.CSharpName : "int",
            WitTypeKind.Variant => type is WitVariantType vt ? vt.CSharpName : "object",
            WitTypeKind.Option => type is WitOptionType ot ? $"{WitTypeToCS(ot.ElementType)}?" : "object",
            WitTypeKind.Resource => type is WitResourceType rt ? rt.CSharpName : "int",
            WitTypeKind.Borrow => type is WitBorrowType bt
                ? (bt.ElementType is WitResourceType brt ? brt.CSharpName
                    : bt.ElementType is WitCustomType bct ? StringUtils.GetName(bct.Name)
                    : "int")
                : "int",
            WitTypeKind.User => type is WitCustomType ct ? StringUtils.GetName(ct.Name) : "object",
            _ => "object"
        };
    }
}
