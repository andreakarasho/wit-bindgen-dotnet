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
        resolver ??= s_resolver;
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
        // Canonical ABI flatten_variant: per-position join across ALL cases (not just the longest).
        var flat = new List<CoreWasmType>();
        foreach (var @case in variant.Values)
        {
            if (@case.Type is null)
                continue;

            var caseFlat = Flatten(@case.Type, resolver);
            for (int i = 0; i < caseFlat.Count; i++)
            {
                if (i < flat.Count)
                    flat[i] = Join(flat[i], caseFlat[i]);
                else
                    flat.Add(caseFlat[i]);
            }
        }

        result.AddRange(flat);
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
            output.Add(Join(aType, bType));
        }
    }

    /// <summary>
    /// Canonical ABI core-type join: equal types stay; (i32,f32) collapses to i32;
    /// any other mismatch widens to i64. Used when flattening variants/results whose
    /// cases flatten to differently-typed core values at the same position.
    /// </summary>
    private static CoreWasmType Join(CoreWasmType a, CoreWasmType b)
    {
        if (a == b)
            return a;
        if ((a == CoreWasmType.I32 && b == CoreWasmType.F32) ||
            (a == CoreWasmType.F32 && b == CoreWasmType.I32))
            return CoreWasmType.I32;
        return CoreWasmType.I64;
    }

    /// <summary>
    /// Returns the number of core WASM values a WIT type flattens to.
    /// </summary>
    public static int FlatCount(WitType type, ITypeContainerResolver? resolver = null)
    {
        resolver ??= s_resolver;
        return Flatten(type, resolver).Count;
    }

    /// <summary>
    /// Determines if function parameters should be spilled to linear memory.
    /// </summary>
    public static bool ShouldSpillParams(WitFuncType func, ITypeContainerResolver? resolver = null)
    {
        resolver ??= s_resolver;
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
        resolver ??= s_resolver;
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
        resolver ??= s_resolver;
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
            case WitTypeKind.Resource:
            case WitTypeKind.Borrow:
                return 4;
            case WitTypeKind.Enum:
                return EnumSizeOf(type);
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
                    int discSize = VariantDiscSize(variantType);
                    int maxPayload = 0;
                    int maxCaseAlign = 1;
                    foreach (var c in variantType.Values)
                    {
                        if (c.Type is not null)
                        {
                            maxPayload = Math.Max(maxPayload, MemorySize(c.Type, resolver));
                            maxCaseAlign = Math.Max(maxCaseAlign, MemoryAlign(c.Type, resolver));
                        }
                    }
                    var align = Math.Max(discSize, maxCaseAlign);
                    var payloadOffset = AlignTo(discSize, maxCaseAlign);
                    return AlignTo(payloadOffset + maxPayload, align);
                }
                return 1;
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
        var discSize = DiscriminantSize(2); // result has exactly 2 cases (ok, err) -> u8
        var align = Math.Max(discSize, payloadAlign);
        var offset = AlignTo(discSize, payloadAlign);
        return AlignTo(offset + payloadSize, align);
    }

    /// <summary>
    /// Returns the alignment of a WIT type in linear memory.
    /// </summary>
    public static int MemoryAlign(WitType type, ITypeContainerResolver? resolver = null)
    {
        resolver ??= s_resolver;
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
            case WitTypeKind.Resource:
            case WitTypeKind.Borrow:
            case WitTypeKind.Flags:
            case WitTypeKind.String:
            case WitTypeKind.List:
                return 4;
            case WitTypeKind.Enum:
                return EnumSizeOf(type);
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
                    int maxAlign = VariantDiscSize(variantType);
                    foreach (var c in variantType.Values)
                    {
                        if (c.Type is not null)
                            maxAlign = Math.Max(maxAlign, MemoryAlign(c.Type, resolver));
                    }
                    return maxAlign;
                }
                return 1;
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

        return Math.Max(DiscriminantSize(2), Math.Max(okAlign, errAlign));
    }

    /// <summary>
    /// Aligns a value up to the specified alignment.
    /// </summary>
    public static int AlignTo(int value, int align)
    {
        return (value + align - 1) & ~(align - 1);
    }

    /// <summary>
    /// Byte size of the discriminant for a variant/enum with the given number of cases,
    /// per the canonical ABI discriminant_type: u8 for ≤256 cases, u16 for ≤65536, else u32.
    /// </summary>
    public static int DiscriminantSize(int caseCount)
        => caseCount <= (1 << 8) ? 1 : caseCount <= (1 << 16) ? 2 : 4;

    /// <summary>
    /// C# pointer-cast keyword for an unsigned little-endian integer load/store of the given byte width.
    /// </summary>
    public static string IntStoreKeyword(int byteSize) => byteSize switch
    {
        1 => "byte",
        2 => "ushort",
        4 => "int",
        8 => "long",
        _ => "int"
    };

    /// <summary>Byte size of an enum's discriminant in linear memory (1/2/4 by label count).</summary>
    public static int EnumSizeOf(WitType type)
        => ResolveType(type) is WitEnumType et ? DiscriminantSize(et.LabelCount) : 1;

    /// <summary>Byte size of a variant's discriminant in linear memory (1/2/4 by case count).</summary>
    public static int VariantDiscSize(WitVariantType variant) => DiscriminantSize(variant.Values.Length);

    /// <summary>
    /// The ok/err payload types of a result, or null for an absent arm
    /// (<c>result&lt;t&gt;</c> has no err; <c>result&lt;_, e&gt;</c> has no ok).
    /// </summary>
    public static (WitType? ok, WitType? err) ResultArms(WitType type) => type switch
    {
        WitResultType rt => (rt.OkType, rt.ErrType),
        WitResultNoErrorType noe => (noe.OkType, null),
        WitResultNoResultType nor => (null, nor.ErrType),
        _ => (null, null)
    };

    /// <summary>
    /// Byte offset of a result's payload: the 1-byte discriminant aligned up to the max
    /// alignment of its present arms. Mirrors the variant payload-offset rule.
    /// </summary>
    public static int ResultPayloadOffset(WitType type, ITypeContainerResolver? resolver = null)
    {
        resolver ??= s_resolver;
        var (ok, err) = ResultArms(type);
        int align = DiscriminantSize(2);
        if (ok is not null) align = Math.Max(align, MemoryAlign(ok, resolver));
        if (err is not null) align = Math.Max(align, MemoryAlign(err, resolver));
        return AlignTo(DiscriminantSize(2), align);
    }

    /// <summary>Max alignment among a variant's case payloads (minimum 1).</summary>
    public static int VariantMaxCaseAlign(WitVariantType variant, ITypeContainerResolver? resolver = null)
    {
        resolver ??= s_resolver;
        int a = 1;
        foreach (var c in variant.Values)
            if (c.Type is not null)
                a = Math.Max(a, MemoryAlign(c.Type, resolver));
        return a;
    }

    /// <summary>
    /// Byte offset of a variant's payload: the discriminant size aligned up to the max case alignment.
    /// </summary>
    public static int VariantPayloadOffset(WitVariantType variant, ITypeContainerResolver? resolver = null)
        => AlignTo(VariantDiscSize(variant), VariantMaxCaseAlign(variant, resolver));

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

    private static Dictionary<string, string>? s_typeNameMap;
    private static ITypeContainerResolver? s_resolver;

    /// <summary>
    /// Sets a type name resolution map for the current generation scope.
    /// Maps WIT type names to their resolved C# type names (handling type aliases and cross-package references).
    /// </summary>
    public static void SetTypeNameMap(Dictionary<string, string>? map) => s_typeNameMap = map;

    /// <summary>
    /// Sets the type container resolver for the current generation scope.
    /// Used to resolve WitCustomType (User) references to their concrete underlying types.
    /// </summary>
    public static void SetResolver(ITypeContainerResolver? resolver) => s_resolver = resolver;

    /// <summary>
    /// Resolves a WitType, unwrapping User (WitCustomType) references to their concrete types.
    /// Returns the type unchanged if it's not a User type or if no resolver is available.
    /// </summary>
    public static WitType ResolveType(WitType type)
    {
        if (type.Kind == WitTypeKind.User && type is WitCustomType customType && s_resolver != null)
        {
            try
            {
                var resolved = customType.Resolve(s_resolver);
                // Resolve may return another WitAliasType (Kind=User); recurse until concrete.
                if (resolved.Kind == WitTypeKind.User && resolved != type)
                    return ResolveType(resolved);
                if (resolved.Kind != WitTypeKind.User)
                    return resolved;
            }
            catch
            {
                // Intentionally fall through to brute-force below.
            }

            // Standard resolution failed or returned unresolved User type.
            // Brute-force: search all interfaces in all packages for this type name.
            var found = BruteForceResolve(customType.Name);
            if (found != null)
                return found;
        }
        return type;
    }

    private static WitType? BruteForceResolve(string typeName)
    {
        if (s_resolver is not ProjectTypeContainerResolver projectResolver)
            return null;

        foreach (var pkg in projectResolver.Packages.Values)
        {
            foreach (var ver in pkg.Versions.Values)
            {
                foreach (var item in ver.Definitions.Items)
                {
                    if (item is WitInterface interf &&
                        interf.Definitions.TryGetType(typeName, out var type))
                    {
                        return type;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Returns true if the WIT element type is blittable — i.e., its WASM linear memory layout
    /// matches the C# in-memory layout and can be bulk-copied with MemoryMarshal.Cast or Buffer.MemoryCopy.
    /// Excludes bool (1 byte in WASM but may differ in C#) and enum (layout-dependent).
    /// </summary>
    public static bool IsBlittablePrimitive(WitType type)
    {
        type = ResolveType(type);
        return type.Kind switch
        {
            WitTypeKind.U8 or WitTypeKind.S8 or
            WitTypeKind.U16 or WitTypeKind.S16 or
            WitTypeKind.U32 or WitTypeKind.S32 or
            WitTypeKind.U64 or WitTypeKind.S64 or
            WitTypeKind.F32 or WitTypeKind.F64 => true,
            _ => false
        };
    }

    /// <summary>
    /// Returns true if the type is blittable — either a blittable primitive or a record
    /// whose fields are all blittable. Blittable records can use MemoryMarshal.Cast for
    /// zero-copy list serialization since canonical ABI memory layout matches C# Sequential layout.
    /// </summary>
    public static bool IsBlittable(WitType type)
    {
        type = ResolveType(type);
        if (IsBlittablePrimitive(type)) return true;
        if (type is WitRecordType rt && rt.Fields.Length > 0)
        {
            foreach (var field in rt.Fields)
            {
                if (!IsBlittable(field.Type)) return false;
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// Returns the C# type name for a WIT type (for high-level API).
    /// Lists map to T[] (arrays) by default.
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
            WitTypeKind.List => type is WitListType lt
                ? (IsBlittable(ResolveType(lt.ElementType)) && !IsBlittablePrimitive(ResolveType(lt.ElementType))
                    ? $"WitBindgen.Runtime.OwnedSpan<{WitTypeToCS(lt.ElementType)}>"
                    : $"{WitTypeToCS(lt.ElementType)}[]")
                : "object",
            WitTypeKind.Record => type is WitRecordType rt ? rt.CSharpName : "object",
            WitTypeKind.Enum => type is WitEnumType et ? et.CSharpName : "int",
            WitTypeKind.Flags => type is WitFlagsType ft ? ft.CSharpName : "int",
            WitTypeKind.Variant => type is WitVariantType vt ? vt.CSharpName : "object",
            WitTypeKind.Option => type is WitOptionType ot ? $"{WitTypeToCS(ot.ElementType)}?" : "object",
            WitTypeKind.Result => ResultToCS(type),
            WitTypeKind.Tuple => type is WitTupleType tplt ? TupleToCS(tplt) : "object",
            WitTypeKind.Resource => type is WitResourceType rt ? rt.CSharpName : "int",
            WitTypeKind.Borrow => type is WitBorrowType bt
                ? (bt.ElementType is WitResourceType brt ? brt.CSharpName + "Borrow"
                    : bt.ElementType is WitCustomType bct ? ResolveCustomTypeName(bct.Name) + "Borrow"
                    : "int")
                : "int",
            WitTypeKind.User => type is WitCustomType ct ? UserTypeToCS(ct) : "object",
            _ => "object"
        };
    }

    /// <summary>
    /// Returns the C# type for a WIT list type when used as an input parameter.
    /// For blittable element types, returns ReadOnlySpan&lt;T&gt; to allow zero-copy.
    /// For non-blittable element types (string, record, etc.), returns T[].
    /// For non-list types, delegates to WitTypeToCS.
    /// </summary>
    public static string WitTypeToCSParam(WitType type)
    {
        if (type.Kind == WitTypeKind.List && type is WitListType lt)
        {
            var elemType = ResolveType(lt.ElementType);
            if (IsBlittable(elemType))
            {
                return $"global::System.ReadOnlySpan<{WitTypeToCS(lt.ElementType)}>";
            }
            return $"{WitTypeToCS(lt.ElementType)}[]";
        }
        return WitTypeToCS(type);
    }

    /// <summary>
    /// Returns the element C# type for a WIT list type.
    /// </summary>
    public static string WitListElementTypeToCS(WitListType lt)
    {
        return WitTypeToCS(lt.ElementType);
    }

    private static string ResolveCustomTypeName(string name)
    {
        if (s_typeNameMap != null && s_typeNameMap.TryGetValue(name, out var mapped))
            return mapped;
        return StringUtils.GetName(name);
    }

    /// <summary>
    /// C# type name for a WIT named-type reference. A type alias to a structural type
    /// (e.g. `type my-len = u32`, `type name = string`, `type bytes = list&lt;u8&gt;`) has NO C#
    /// declaration, so it must resolve to the underlying type at the use-site. Named types
    /// (record/variant/enum/flags/resource) and unresolved/cross-package references keep their
    /// mapped name (preserving fully-qualified names from the type-name map).
    /// </summary>
    private static string UserTypeToCS(WitCustomType ct)
    {
        var resolved = ResolveType(ct);
        switch (resolved.Kind)
        {
            case WitTypeKind.User:
            case WitTypeKind.Record:
            case WitTypeKind.Variant:
            case WitTypeKind.Enum:
            case WitTypeKind.Flags:
            case WitTypeKind.Resource:
                return ResolveCustomTypeName(ct.Name);
            default:
                // Alias to a structural type — emit the underlying C# type.
                return WitTypeToCS(resolved);
        }
    }

    /// <summary>
    /// Maps a WIT result to <c>WitBindgen.Runtime.WitResult&lt;TOk, TErr&gt;</c>. Absent arms
    /// (result&lt;t&gt; / result&lt;_, e&gt;) use a <c>byte</c> placeholder for the missing type
    /// param — that arm carries no data and is never read.
    /// </summary>
    private static string ResultToCS(WitType type)
    {
        var (ok, err) = ResultArms(type);
        var okCs = ok is not null ? WitTypeToCS(ok) : "byte";
        var errCs = err is not null ? WitTypeToCS(err) : "byte";
        return $"global::WitBindgen.Runtime.WitResult<{okCs}, {errCs}>";
    }

    /// <summary>
    /// Maps a WIT tuple to a C# ValueTuple. Arity 1 uses System.ValueTuple&lt;T&gt; (since
    /// "(T)" is just a parenthesized expression, not a 1-tuple); arity >= 2 uses "(T1, T2, ...)".
    /// Elements are accessed via .Item1, .Item2, ... in the generated lift/lower code.
    /// </summary>
    private static string TupleToCS(WitTupleType tuple)
    {
        var elems = tuple.ElementTypes;
        if (elems.Length == 0) return "global::System.ValueTuple";
        if (elems.Length == 1) return $"global::System.ValueTuple<{WitTypeToCS(elems[0])}>";
        var parts = new List<string>(elems.Length);
        foreach (var e in elems) parts.Add(WitTypeToCS(e));
        return "(" + string.Join(", ", parts) + ")";
    }
}
