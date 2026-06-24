using WitBindgen.SourceGenerator.Models;

namespace WitBindgen.SourceGenerator.Generators.Guest;

/// <summary>
/// Generates guest export bindings: partial method declarations and [UnmanagedCallersOnly] trampolines.
/// </summary>
public static class GuestExportWriter
{
    private static int s_memStoreCounter;

    /// <summary>
    /// Writes export bindings for an exported function.
    /// </summary>
    public static void WriteExportFunction(
        IndentedStringBuilder sb,
        string entryPoint,
        string funcName,
        WitFuncType func)
    {
        var csharpFuncName = StringUtils.GetName(funcName);
        var useRetPtr = CanonicalAbi.ShouldUseRetPtr(func);

        // Build high-level parameter list for the partial method
        // Use ReadOnlySpan<T> for blittable list params, T[] for non-blittable
        var highLevelParams = new List<string>();
        foreach (var param in func.Parameters)
        {
            highLevelParams.Add($"{CanonicalAbi.WitTypeToCSParam(param.Type)} {param.CSharpVariableName}");
        }

        // Determine return type
        string returnType;
        if (func.Results.Length == 0)
        {
            returnType = "void";
        }
        else if (func.Results.Length == 1)
        {
            returnType = CanonicalAbi.WitTypeToCS(func.Results[0]);
        }
        else
        {
            returnType = "void";
        }

        // Write the partial method that the user must implement
        sb.AppendLine("/// <summary>User must provide implementation in a separate partial class file.</summary>");
        sb.AppendLine($"public static partial {returnType} {csharpFuncName}({string.Join(", ", highLevelParams)});");
        sb.AppendLine();

        // Write the [UnmanagedCallersOnly] trampoline
        WriteTrampolineMethod(sb, entryPoint, csharpFuncName, func, useRetPtr);

        // Write cabi_post_return if needed (for string/list returns)
        if (func.Results.Length > 0 && NeedsPostReturn(func.Results[0]))
        {
            sb.AppendLine();
            WritePostReturn(sb, entryPoint, func);
        }
    }

    private static void WriteTrampolineMethod(
        IndentedStringBuilder sb,
        string entryPoint,
        string csharpFuncName,
        WitFuncType func,
        bool useRetPtr)
    {
        // Build the flattened core parameter list
        var coreParams = new List<(string type, string name)>();
        foreach (var param in func.Parameters)
        {
            var paramFlat = CanonicalAbi.Flatten(param.Type);
            if (paramFlat.Count == 1)
            {
                coreParams.Add((CanonicalAbi.CoreTypeToCS(paramFlat[0]), param.CSharpVariableName));
            }
            else
            {
                for (int i = 0; i < paramFlat.Count; i++)
                {
                    coreParams.Add((CanonicalAbi.CoreTypeToCS(paramFlat[i]), $"{param.CSharpVariableName}_{i}"));
                }
            }
        }

        // Determine core return type
        string coreReturnType;
        if (useRetPtr)
        {
            coreReturnType = "nint"; // returns retptr
        }
        else if (func.Results.Length == 1)
        {
            var flat = CanonicalAbi.Flatten(func.Results[0]);
            coreReturnType = flat.Count == 1 ? CanonicalAbi.CoreTypeToCS(flat[0]) : "nint";
        }
        else
        {
            coreReturnType = "void";
        }

        var coreParamList = coreParams.Select(p => $"{p.type} {p.name}");
        sb.AppendLine($"[global::System.Runtime.InteropServices.UnmanagedCallersOnly(EntryPoint = \"{entryPoint}\")]");
        sb.AppendLine("[global::System.Runtime.CompilerServices.SkipLocalsInit]");
        sb.AppendLine($"private static unsafe {coreReturnType} __wit_export_{funcName(entryPoint)}({string.Join(", ", coreParamList)})");
        using (sb.Block())
        {
            // Lift parameters from core wasm types to C# types
            var liftedArgs = new List<string>();
            foreach (var param in func.Parameters)
            {
                LiftParam(sb, param, coreParams, liftedArgs);
            }

            // Call the user's implementation, THEN free the WASM memory backing string/list
            // params (the callee owns it per the canonical ABI). The free must come after the
            // call: blittable-primitive list params are lifted as a zero-copy ReadOnlySpan<T>
            // over this very memory (see LiftParam), so freeing before the user reads the span
            // is a use-after-free that returns garbage.
            var callArgs = string.Join(", ", liftedArgs);
            if (func.Results.Length == 0)
            {
                sb.AppendLine($"{csharpFuncName}({callArgs});");
                WriteParamFree(sb, func);
            }
            else
            {
                sb.AppendLine($"var result = {csharpFuncName}({callArgs});");
                WriteParamFree(sb, func);

                // Lower the result
                var resultType = func.Results[0];
                LowerResult(sb, resultType, useRetPtr);
            }
        }
    }

    /// <summary>
    /// Frees the WASM linear memory backing string/list export params. Emitted after the user
    /// call so zero-copy span lifts stay valid for the duration of the call.
    /// </summary>
    private static void WriteParamFree(IndentedStringBuilder sb, WitFuncType func)
    {
        foreach (var param in func.Parameters)
        {
            switch (param.Type.Kind)
            {
                case WitTypeKind.String:
                    sb.AppendLine($"WitBindgen.Runtime.InteropHelpers.Free((byte*){param.CSharpVariableName}_0, {param.CSharpVariableName}_1, 1);");
                    break;
                case WitTypeKind.List when param.Type is WitListType listType:
                    var elemSize = CanonicalAbi.MemorySize(listType.ElementType);
                    var elemAlign = CanonicalAbi.MemoryAlign(listType.ElementType);
                    sb.AppendLine($"WitBindgen.Runtime.InteropHelpers.Free((byte*){param.CSharpVariableName}_0, {param.CSharpVariableName}_1 * {elemSize}, {elemAlign});");
                    break;
            }
        }
    }

    private static string funcName(string entryPoint)
    {
        // Sanitize every WIT entry-point separator that is illegal in a C# identifier.
        // Interface exports produce entry points like "my:pkg/t@1.0.0#get-point" (note '.' and '#').
        return entryPoint
            .Replace("-", "_").Replace(":", "_").Replace("/", "_")
            .Replace("@", "_").Replace(".", "_").Replace("#", "_").Replace("%", "_");
    }

    private static void LiftParam(
        IndentedStringBuilder sb,
        WitFuncParameter param,
        List<(string type, string name)> coreParams,
        List<string> liftedArgs)
    {
        var resolvedType = CanonicalAbi.ResolveType(param.Type);
        switch (resolvedType.Kind)
        {
            case WitTypeKind.Bool:
                liftedArgs.Add($"({param.CSharpVariableName} != 0)");
                break;

            case WitTypeKind.U8:
                liftedArgs.Add($"(byte){param.CSharpVariableName}");
                break;

            case WitTypeKind.U16:
                liftedArgs.Add($"(ushort){param.CSharpVariableName}");
                break;

            case WitTypeKind.U32:
                liftedArgs.Add($"(uint){param.CSharpVariableName}");
                break;

            case WitTypeKind.S8:
                liftedArgs.Add($"(sbyte){param.CSharpVariableName}");
                break;

            case WitTypeKind.S16:
                liftedArgs.Add($"(short){param.CSharpVariableName}");
                break;

            case WitTypeKind.S32:
                liftedArgs.Add(param.CSharpVariableName);
                break;

            case WitTypeKind.Char:
                // High-level char maps to uint; the core param is int.
                liftedArgs.Add($"(uint){param.CSharpVariableName}");
                break;

            case WitTypeKind.Flags:
                liftedArgs.Add($"({CanonicalAbi.WitTypeToCS(resolvedType)}){param.CSharpVariableName}");
                break;

            case WitTypeKind.U64:
                liftedArgs.Add($"(ulong){param.CSharpVariableName}");
                break;

            case WitTypeKind.S64:
                liftedArgs.Add(param.CSharpVariableName);
                break;

            case WitTypeKind.F32:
            case WitTypeKind.F64:
                liftedArgs.Add(param.CSharpVariableName);
                break;

            case WitTypeKind.String:
                var ptrVar = $"{param.CSharpVariableName}_0";
                var lenVar = $"{param.CSharpVariableName}_1";
                var liftedVar = $"{param.CSharpVariableName}Str";
                sb.AppendLine($"var {liftedVar} = global::System.Text.Encoding.UTF8.GetString((byte*){ptrVar}, {lenVar});");
                liftedArgs.Add(liftedVar);
                break;

            case WitTypeKind.List:
                if (resolvedType is WitListType paramListType)
                {
                    var listPtrVar = $"{param.CSharpVariableName}_0";
                    var listCountVar = $"{param.CSharpVariableName}_1";
                    var liftedListVar = $"{param.CSharpVariableName}List";
                    var elemType = CanonicalAbi.ResolveType(paramListType.ElementType);
                    var elemSize = CanonicalAbi.MemorySize(paramListType.ElementType);

                    if (CanonicalAbi.IsBlittablePrimitive(elemType))
                    {
                        // Zero-copy: create ReadOnlySpan<T> directly over WASM linear memory
                        var elemCsType = CanonicalAbi.WitListElementTypeToCS(paramListType);
                        sb.AppendLine($"var {liftedListVar} = new global::System.ReadOnlySpan<{elemCsType}>((void*){listPtrVar}, {listCountVar});");
                    }
                    else
                    {
                        // Non-blittable: copy into T[]
                        var elemCsType = CanonicalAbi.WitListElementTypeToCS(paramListType);
                        sb.AppendLine($"var {liftedListVar} = new {elemCsType}[{listCountVar}];");
                        sb.AppendLine($"for (int {param.CSharpVariableName}LiftIdx = 0; {param.CSharpVariableName}LiftIdx < {listCountVar}; {param.CSharpVariableName}LiftIdx++)");
                        using (sb.Block())
                        {
                            var elemBaseVar = $"{param.CSharpVariableName}LiftBase";
                            sb.AppendLine($"var {elemBaseVar} = (byte*){listPtrVar} + {param.CSharpVariableName}LiftIdx * {elemSize};");
                            WriteLiftListElement(sb, paramListType.ElementType, elemBaseVar, liftedListVar, $"{param.CSharpVariableName}LiftIdx");
                        }
                    }
                    liftedArgs.Add(liftedListVar);
                }
                break;

            case WitTypeKind.Resource:
            case WitTypeKind.Borrow:
            {
                var liftedResVar = $"{param.CSharpVariableName}Res";
                // Pass the unresolved param.Type so cross-package resource names stay qualified.
                sb.AppendLine($"var {liftedResVar} = {GetResourceConstructorCall(param.Type, param.CSharpVariableName)};");
                liftedArgs.Add(liftedResVar);
                break;
            }

            case WitTypeKind.Enum:
                liftedArgs.Add($"({CanonicalAbi.WitTypeToCS(resolvedType)}){param.CSharpVariableName}");
                break;

            case WitTypeKind.Option:
                if (resolvedType is WitOptionType optionType)
                {
                    var discVar = $"{param.CSharpVariableName}_0";
                    var optVar = $"{param.CSharpVariableName}Opt";
                    var innerType = CanonicalAbi.ResolveType(optionType.ElementType);
                    var innerFlat = CanonicalAbi.Flatten(innerType);

                    // Use original element type for C# name (preserves full qualification)
                    var origInnerCsType = CanonicalAbi.WitTypeToCS(optionType.ElementType);

                    // Lift inner value from _{1..N} params
                    if (innerType.Kind == WitTypeKind.Resource || innerType.Kind == WitTypeKind.Borrow)
                    {
                        var handleVar = $"{param.CSharpVariableName}_1";
                        // Use the unresolved element type so cross-package resource names stay qualified.
                        sb.AppendLine($"{origInnerCsType}? {optVar} = {discVar} != 0 ? {GetResourceConstructorCall(optionType.ElementType, handleVar)} : null;");
                    }
                    else if (innerType.Kind == WitTypeKind.String)
                    {
                        var innerPtrVar = $"{param.CSharpVariableName}_1";
                        var innerLenVar = $"{param.CSharpVariableName}_2";
                        sb.AppendLine($"string? {optVar} = {discVar} != 0 ? global::System.Text.Encoding.UTF8.GetString((byte*){innerPtrVar}, {innerLenVar}) : null;");
                    }
                    else if (innerFlat.Count == 1)
                    {
                        var payloadVar = $"{param.CSharpVariableName}_1";
                        sb.AppendLine($"{origInnerCsType}? {optVar} = {discVar} != 0 ? ({origInnerCsType}){payloadVar} : null;");
                    }
                    else
                    {
                        // Multi-slot inner type: lift from _{1..N}
                        sb.AppendLine($"{origInnerCsType}? {optVar};");
                        sb.AppendLine($"if ({discVar} != 0)");
                        using (sb.Block())
                        {
                            int slotIdx = 1;
                            var innerExpr = WriteLiftFromFlatParams(sb, innerType, param.CSharpVariableName, ref slotIdx, $"{param.CSharpVariableName}Inner");
                            sb.AppendLine($"{optVar} = {innerExpr};");
                        }
                        sb.AppendLine("else");
                        using (sb.Block())
                        {
                            sb.AppendLine($"{optVar} = null;");
                        }
                    }
                    liftedArgs.Add(optVar);
                }
                break;

            case WitTypeKind.Record:
                if (resolvedType is WitRecordType recordType)
                {
                    var recVar = $"{param.CSharpVariableName}Rec";
                    sb.AppendLine($"var {recVar} = new {CanonicalAbi.WitTypeToCS(resolvedType)}();");
                    int recSlotIdx = 0;
                    foreach (var field in recordType.Fields)
                    {
                        var fieldFlat = CanonicalAbi.Flatten(field.Type);
                        if (fieldFlat.Count == 1)
                        {
                            var flatParamName = fieldFlat.Count == 1 && CanonicalAbi.FlatCount(resolvedType) == 1
                                ? param.CSharpVariableName
                                : $"{param.CSharpVariableName}_{recSlotIdx}";
                            var fieldExpr = WriteLiftFlatParamToExpr(field.Type, flatParamName);
                            sb.AppendLine($"{recVar}.{field.CSharpName} = {fieldExpr};");
                            recSlotIdx++;
                        }
                        else
                        {
                            var fieldExpr = WriteLiftFromFlatParams(sb, field.Type, param.CSharpVariableName, ref recSlotIdx, $"{param.CSharpVariableName}{field.CSharpName}");
                            sb.AppendLine($"{recVar}.{field.CSharpName} = {fieldExpr};");
                        }
                    }
                    liftedArgs.Add(recVar);
                }
                break;

            case WitTypeKind.Tuple:
                if (resolvedType is WitTupleType tupleParamType)
                {
                    var tupVar = $"{param.CSharpVariableName}Tup";
                    sb.AppendLine($"{CanonicalAbi.WitTypeToCS(resolvedType)} {tupVar} = default;");
                    int tupSlotIdx = 0;
                    for (int ti = 0; ti < tupleParamType.ElementTypes.Length; ti++)
                    {
                        var et = tupleParamType.ElementTypes[ti];
                        var fieldFlat = CanonicalAbi.Flatten(et);
                        if (fieldFlat.Count == 1)
                        {
                            var flatParamName = CanonicalAbi.FlatCount(resolvedType) == 1
                                ? param.CSharpVariableName
                                : $"{param.CSharpVariableName}_{tupSlotIdx}";
                            var fieldExpr = WriteLiftFlatParamToExpr(et, flatParamName);
                            sb.AppendLine($"{tupVar}.Item{ti + 1} = {fieldExpr};");
                            tupSlotIdx++;
                        }
                        else
                        {
                            var fieldExpr = WriteLiftFromFlatParams(sb, et, param.CSharpVariableName, ref tupSlotIdx, $"{param.CSharpVariableName}Item{ti + 1}");
                            sb.AppendLine($"{tupVar}.Item{ti + 1} = {fieldExpr};");
                        }
                    }
                    liftedArgs.Add(tupVar);
                }
                break;

            case WitTypeKind.Variant:
                if (resolvedType is WitVariantType variantParamType)
                {
                    if (CanonicalAbi.FlatCount(resolvedType) == 1)
                    {
                        // Payloadless variant flattens to a single core slot -> the param is unsuffixed.
                        var csN = CanonicalAbi.WitTypeToCS(resolvedType);
                        var vv = $"{param.CSharpVariableName}Var";
                        sb.AppendLine($"{csN} {vv} = default;");
                        sb.AppendLine($"{vv}.Discriminant = ({csN}.Case){param.CSharpVariableName};");
                        liftedArgs.Add(vv);
                    }
                    else
                    {
                        int vSlot = 0;
                        var expr = LiftVariantFromFlatParams(sb, variantParamType, param.CSharpVariableName, ref vSlot, param.CSharpVariableName);
                        liftedArgs.Add(expr);
                    }
                }
                break;

            case WitTypeKind.Result:
                if (CanonicalAbi.FlatCount(resolvedType) == 1)
                {
                    // Payloadless result (bare `result`) flattens to a single core slot.
                    var csR = CanonicalAbi.WitTypeToCS(resolvedType);
                    var rv = $"{param.CSharpVariableName}Res";
                    sb.AppendLine($"{csR} {rv} = {param.CSharpVariableName} == 0 ? {csR}.FromOk(default) : {csR}.FromErr(default);");
                    liftedArgs.Add(rv);
                }
                else
                {
                    int rSlot = 0;
                    var expr = LiftResultFromFlatParams(sb, resolvedType, param.CSharpVariableName, ref rSlot, param.CSharpVariableName);
                    liftedArgs.Add(expr);
                }
                break;

            default:
                liftedArgs.Add(param.CSharpVariableName);
                break;
        }
    }

    /// <summary>
    /// Returns a simple C# expression to lift a single flat param value to a high-level type.
    /// Only for types that flatten to exactly 1 core value.
    /// </summary>
    private static string WriteLiftFlatParamToExpr(WitType type, string paramName)
    {
        type = CanonicalAbi.ResolveType(type);
        return type.Kind switch
        {
            WitTypeKind.Bool => $"({paramName} != 0)",
            WitTypeKind.U8 => $"(byte){paramName}",
            WitTypeKind.U16 => $"(ushort){paramName}",
            WitTypeKind.U32 => $"(uint){paramName}",
            WitTypeKind.S8 => $"(sbyte){paramName}",
            WitTypeKind.S16 => $"(short){paramName}",
            WitTypeKind.S32 => paramName,
            WitTypeKind.Char => $"(uint){paramName}",
            WitTypeKind.U64 => $"(ulong){paramName}",
            WitTypeKind.S64 => paramName,
            WitTypeKind.F32 or WitTypeKind.F64 => paramName,
            WitTypeKind.Enum or WitTypeKind.Flags => $"({CanonicalAbi.WitTypeToCS(type)}){paramName}",
            WitTypeKind.Resource or WitTypeKind.Borrow => GetResourceConstructorCall(type, paramName),
            _ => paramName,
        };
    }

    /// <summary>
    /// Lifts a complex type from flat export params (named {prefix}_{slotIdx}).
    /// Returns the C# expression for the lifted value.
    /// </summary>
    private static string WriteLiftFromFlatParams(IndentedStringBuilder sb, WitType type, string prefix, ref int slotIdx, string varPrefix)
    {
        type = CanonicalAbi.ResolveType(type);
        switch (type.Kind)
        {
            case WitTypeKind.Bool:
                return $"({prefix}_{slotIdx++} != 0)";

            case WitTypeKind.U8:
                return $"(byte){prefix}_{slotIdx++}";
            case WitTypeKind.U16:
                return $"(ushort){prefix}_{slotIdx++}";
            case WitTypeKind.U32:
                return $"(uint){prefix}_{slotIdx++}";
            case WitTypeKind.S8:
                return $"(sbyte){prefix}_{slotIdx++}";
            case WitTypeKind.S16:
                return $"(short){prefix}_{slotIdx++}";
            case WitTypeKind.S32:
                return $"{prefix}_{slotIdx++}";
            case WitTypeKind.Char:
                return $"(uint){prefix}_{slotIdx++}";
            case WitTypeKind.U64:
                return $"(ulong){prefix}_{slotIdx++}";
            case WitTypeKind.S64:
                return $"{prefix}_{slotIdx++}";
            case WitTypeKind.F32:
            case WitTypeKind.F64:
                return $"{prefix}_{slotIdx++}";
            case WitTypeKind.Enum:
            case WitTypeKind.Flags:
                return $"({CanonicalAbi.WitTypeToCS(type)}){prefix}_{slotIdx++}";
            case WitTypeKind.Resource:
            case WitTypeKind.Borrow:
                return GetResourceConstructorCall(type, $"{prefix}_{slotIdx++}");

            case WitTypeKind.String:
            {
                var pIdx = slotIdx++;
                var lIdx = slotIdx++;
                var sVar = $"{varPrefix}Str";
                sb.AppendLine($"var {sVar} = global::System.Text.Encoding.UTF8.GetString((byte*){prefix}_{pIdx}, {prefix}_{lIdx});");
                return sVar;
            }

            case WitTypeKind.Record:
                if (type is WitRecordType recordType)
                {
                    var recVar = $"{varPrefix}Rec";
                    sb.AppendLine($"var {recVar} = new {CanonicalAbi.WitTypeToCS(type)}();");
                    foreach (var field in recordType.Fields)
                    {
                        var fieldExpr = WriteLiftFromFlatParams(sb, field.Type, prefix, ref slotIdx, $"{varPrefix}{field.CSharpName}");
                        sb.AppendLine($"{recVar}.{field.CSharpName} = {fieldExpr};");
                    }
                    return recVar;
                }
                return "default";

            case WitTypeKind.Tuple:
                if (type is WitTupleType tupParamLift)
                {
                    var tupVar = $"{varPrefix}Tup";
                    sb.AppendLine($"{CanonicalAbi.WitTypeToCS(type)} {tupVar} = default;");
                    for (int ti = 0; ti < tupParamLift.ElementTypes.Length; ti++)
                    {
                        var fieldExpr = WriteLiftFromFlatParams(sb, tupParamLift.ElementTypes[ti], prefix, ref slotIdx, $"{varPrefix}Item{ti + 1}");
                        sb.AppendLine($"{tupVar}.Item{ti + 1} = {fieldExpr};");
                    }
                    return tupVar;
                }
                return "default";

            case WitTypeKind.Variant:
                if (type is WitVariantType varParamLift)
                    return LiftVariantFromFlatParams(sb, varParamLift, prefix, ref slotIdx, varPrefix);
                return "default";

            case WitTypeKind.Result:
                return LiftResultFromFlatParams(sb, type, prefix, ref slotIdx, varPrefix);

            default:
                return $"{prefix}_{slotIdx++}";
        }
    }

    /// <summary>
    /// Lifts a result from flat export params: discriminant (0=ok, 1=err) in the first slot, the
    /// active arm's payload from the joined payload slots. Mirrors <see cref="LiftVariantFromFlatParams"/>.
    /// </summary>
    private static string LiftResultFromFlatParams(
        IndentedStringBuilder sb, WitType type, string prefix, ref int slotIdx, string varPrefix)
    {
        var (okT, errT) = CanonicalAbi.ResultArms(type);
        var csType = CanonicalAbi.WitTypeToCS(type);
        var rVar = $"{varPrefix}Res";
        var discSlot = slotIdx++;
        var payloadStart = slotIdx;
        var totalPayload = CanonicalAbi.FlatCount(type) - 1;

        sb.AppendLine($"{csType} {rVar};");
        sb.AppendLine($"if ({prefix}_{discSlot} == 0)");
        using (sb.Block())
        {
            if (okT is not null)
            {
                var s = payloadStart;
                var okExpr = WriteLiftFromFlatParams(sb, okT, prefix, ref s, $"{varPrefix}Ok");
                sb.AppendLine($"{rVar} = {csType}.FromOk({okExpr});");
            }
            else
            {
                sb.AppendLine($"{rVar} = {csType}.FromOk(default);");
            }
        }
        sb.AppendLine("else");
        using (sb.Block())
        {
            if (errT is not null)
            {
                var s = payloadStart;
                var errExpr = WriteLiftFromFlatParams(sb, errT, prefix, ref s, $"{varPrefix}Err");
                sb.AppendLine($"{rVar} = {csType}.FromErr({errExpr});");
            }
            else
            {
                sb.AppendLine($"{rVar} = {csType}.FromErr(default);");
            }
        }

        slotIdx = payloadStart + totalPayload;
        return rVar;
    }

    /// <summary>
    /// Lifts a variant from flat export params: the discriminant occupies the first slot, the
    /// payload the next (FlatCount-1) slots (a per-position join across all cases). Each case
    /// reads its own payload from the start of the payload slots (the cases overlap in the union),
    /// and slotIdx is advanced past the full payload width afterwards. Mirrors the variant
    /// lowering in GuestImportWriter.WriteLowerFlat.
    /// ponytail: payloads whose natural core type differs from the joined slot type by float-vs-int
    /// width (e.g. a variant mixing f64 and i64 cases) are not bit-reinterpreted here — the same
    /// gap exists on the lower side; add BitConverter coercion if such a variant param appears.
    /// </summary>
    private static string LiftVariantFromFlatParams(
        IndentedStringBuilder sb, WitVariantType variant, string prefix, ref int slotIdx, string varPrefix)
    {
        var csName = CanonicalAbi.WitTypeToCS(variant);
        var vVar = $"{varPrefix}Var";
        var discSlot = slotIdx++;
        sb.AppendLine($"{csName} {vVar} = default;");
        sb.AppendLine($"{vVar}.Discriminant = ({csName}.Case){prefix}_{discSlot};");

        var payloadSlotStart = slotIdx;
        var totalPayloadSlots = CanonicalAbi.FlatCount(variant) - 1;

        sb.AppendLine($"switch ({vVar}.Discriminant)");
        using (sb.Block())
        {
            foreach (var c in variant.Values)
            {
                if (c.Type is null)
                    continue;
                var caseName = StringUtils.GetName(c.Name);
                sb.AppendLine($"case {csName}.Case.{caseName}:");
                sb.IncrementIndent();
                var caseSlot = payloadSlotStart;
                var payloadExpr = WriteLiftFromFlatParams(sb, c.Type, prefix, ref caseSlot, $"{varPrefix}{caseName}");
                sb.AppendLine($"{vVar}.{caseName}Payload = {payloadExpr};");
                sb.AppendLine("break;");
                sb.DecrementIndent();
            }
        }

        slotIdx = payloadSlotStart + totalPayloadSlots;
        return vVar;
    }

    private static void LowerResult(IndentedStringBuilder sb, WitType resultType, bool useRetPtr)
    {
        resultType = CanonicalAbi.ResolveType(resultType);
        switch (resultType.Kind)
        {
            case WitTypeKind.Bool:
                sb.AppendLine("return result ? 1 : 0;");
                break;

            case WitTypeKind.U8:
            case WitTypeKind.U16:
            case WitTypeKind.U32:
            case WitTypeKind.S8:
            case WitTypeKind.S16:
            case WitTypeKind.S32:
            case WitTypeKind.Char:
            case WitTypeKind.Enum:
            case WitTypeKind.Flags:
                sb.AppendLine("return (int)result;");
                break;

            case WitTypeKind.U64:
            case WitTypeKind.S64:
                sb.AppendLine("return (long)result;");
                break;

            case WitTypeKind.F32:
            case WitTypeKind.F64:
                sb.AppendLine("return result;");
                break;

            case WitTypeKind.String:
                sb.AppendLine("var byteLen = global::System.Text.Encoding.UTF8.GetByteCount(result);");
                sb.AppendLine("var ptr = WitBindgen.Runtime.InteropHelpers.Alloc(byteLen, 1);");
                sb.AppendLine("global::System.Text.Encoding.UTF8.GetBytes(result, new Span<byte>((void*)ptr, byteLen));");
                sb.AppendLine();
                sb.AppendLine("var retArea = WitBindgen.Runtime.InteropHelpers.GetReturnArea();");
                sb.AppendLine("*(int*)retArea = (int)ptr;");
                sb.AppendLine("*(int*)(retArea + 4) = byteLen;");
                sb.AppendLine("return retArea;");
                break;

            case WitTypeKind.List:
                if (resultType is WitListType resultListType)
                {
                    var elemSize = CanonicalAbi.MemorySize(resultListType.ElementType);
                    var elemAlign = CanonicalAbi.MemoryAlign(resultListType.ElementType);

                    sb.AppendLine("var resultCount = result.Length;");
                    sb.AppendLine($"var resultListPtr = WitBindgen.Runtime.InteropHelpers.Alloc(resultCount * {elemSize}, {elemAlign});");
                    sb.AppendLine("for (int resultIdx = 0; resultIdx < resultCount; resultIdx++)");
                    using (sb.Block())
                    {
                        sb.AppendLine($"var resultElemBase = (byte*)resultListPtr + resultIdx * {elemSize};");
                        WriteLowerListElement(sb, resultListType.ElementType, "result[resultIdx]", "resultElemBase");
                    }
                    sb.AppendLine();
                    sb.AppendLine("var retArea = WitBindgen.Runtime.InteropHelpers.GetReturnArea();");
                    sb.AppendLine("*(int*)retArea = (int)resultListPtr;");
                    sb.AppendLine("*(int*)(retArea + 4) = resultCount;");
                    sb.AppendLine("return retArea;");
                }
                else
                {
                    goto default;
                }
                break;

            case WitTypeKind.Resource:
            case WitTypeKind.Borrow:
                sb.AppendLine("return result.Handle;");
                break;

            case WitTypeKind.Result:
                // Payload-bearing results flatten to >1 core value -> the aggregate retptr path
                // (default) stores them in memory. A bare `result` is a single discriminant slot.
                if (useRetPtr)
                    goto default;
                sb.AppendLine("return (int)(result.IsOk ? 0 : 1);");
                break;

            default:
                if (useRetPtr)
                {
                    // Aggregate result: allocate a guest-owned return area sized to the canonical
                    // memory layout, store the value in memory layout, return the pointer.
                    // The area (and any nested string/list heap) is freed in cabi_post_return.
                    var retSize = CanonicalAbi.MemorySize(resultType);
                    var retAlign = CanonicalAbi.MemoryAlign(resultType);
                    sb.AppendLine($"var retArea = WitBindgen.Runtime.InteropHelpers.Alloc({retSize}, {retAlign});");
                    WriteMemoryStore(sb, resultType, "result", "(byte*)retArea", 0);
                    sb.AppendLine("return retArea;");
                }
                else
                {
                    sb.AppendLine("return default;");
                }
                break;
        }
    }

    private static void WriteLiftListElement(IndentedStringBuilder sb, WitType elemType, string baseVar, string listVar, string idxVar)
    {
        elemType = CanonicalAbi.ResolveType(elemType);
        switch (elemType.Kind)
        {
            case WitTypeKind.Bool:
                sb.AppendLine($"{listVar}[{idxVar}] = *{baseVar} != 0;");
                break;
            case WitTypeKind.U8:
                sb.AppendLine($"{listVar}[{idxVar}] = *{baseVar};");
                break;
            case WitTypeKind.S8:
                sb.AppendLine($"{listVar}[{idxVar}] = (sbyte)*{baseVar};");
                break;
            case WitTypeKind.U16:
                sb.AppendLine($"{listVar}[{idxVar}] = *(ushort*){baseVar};");
                break;
            case WitTypeKind.S16:
                sb.AppendLine($"{listVar}[{idxVar}] = *(short*){baseVar};");
                break;
            case WitTypeKind.U32:
            case WitTypeKind.Char:
                sb.AppendLine($"{listVar}[{idxVar}] = *(uint*){baseVar};");
                break;
            case WitTypeKind.S32:
                sb.AppendLine($"{listVar}[{idxVar}] = *(int*){baseVar};");
                break;
            case WitTypeKind.U64:
                sb.AppendLine($"{listVar}[{idxVar}] = *(ulong*){baseVar};");
                break;
            case WitTypeKind.S64:
                sb.AppendLine($"{listVar}[{idxVar}] = *(long*){baseVar};");
                break;
            case WitTypeKind.F32:
                sb.AppendLine($"{listVar}[{idxVar}] = *(float*){baseVar};");
                break;
            case WitTypeKind.F64:
                sb.AppendLine($"{listVar}[{idxVar}] = *(double*){baseVar};");
                break;
            case WitTypeKind.String:
                sb.AppendLine($"var elemStrPtr = (byte*)*(int*){baseVar};");
                sb.AppendLine($"var elemStrLen = *(int*)({baseVar} + 4);");
                sb.AppendLine($"{listVar}[{idxVar}] = global::System.Text.Encoding.UTF8.GetString(elemStrPtr, elemStrLen);");
                break;
            case WitTypeKind.Enum:
            {
                var enumKw = CanonicalAbi.IntStoreKeyword(CanonicalAbi.EnumSizeOf(elemType));
                sb.AppendLine($"{listVar}[{idxVar}] = ({CanonicalAbi.WitTypeToCS(elemType)})(*({enumKw}*){baseVar});");
                break;
            }
            case WitTypeKind.Resource:
            case WitTypeKind.Borrow:
                sb.AppendLine($"{listVar}[{idxVar}] = {GetResourceConstructorCall(elemType, $"*(int*){baseVar}")};");
                break;
            case WitTypeKind.Record:
                if (elemType is WitRecordType liftRecType)
                {
                    var liftedExpr = WriteMemoryLoad(sb, elemType, baseVar, 0, "elem");
                    sb.AppendLine($"{listVar}[{idxVar}] = {liftedExpr};");
                }
                else
                {
                    sb.AppendLine($"{listVar}[{idxVar}] = default;");
                }
                break;
            case WitTypeKind.Tuple:
                {
                    var liftedTupleExpr = WriteMemoryLoad(sb, elemType, baseVar, 0, "elem");
                    sb.AppendLine($"{listVar}[{idxVar}] = {liftedTupleExpr};");
                }
                break;
            default:
                sb.AppendLine($"// TODO: lift list element of type {elemType.Kind}");
                sb.AppendLine($"{listVar}[{idxVar}] = default;");
                break;
        }
    }

    private static void WriteLowerListElement(IndentedStringBuilder sb, WitType elemType, string elemExpr, string baseVar)
    {
        elemType = CanonicalAbi.ResolveType(elemType);
        switch (elemType.Kind)
        {
            case WitTypeKind.Bool:
                sb.AppendLine($"*{baseVar} = (byte)({elemExpr} ? 1 : 0);");
                break;
            case WitTypeKind.U8:
            case WitTypeKind.S8:
                sb.AppendLine($"*{baseVar} = (byte){elemExpr};");
                break;
            case WitTypeKind.U16:
            case WitTypeKind.S16:
                sb.AppendLine($"*(short*){baseVar} = (short){elemExpr};");
                break;
            case WitTypeKind.U32:
            case WitTypeKind.S32:
            case WitTypeKind.Char:
                sb.AppendLine($"*(int*){baseVar} = (int){elemExpr};");
                break;
            case WitTypeKind.Enum:
            {
                var enumKw = CanonicalAbi.IntStoreKeyword(CanonicalAbi.EnumSizeOf(elemType));
                sb.AppendLine($"*({enumKw}*){baseVar} = ({enumKw}){elemExpr};");
                break;
            }
            case WitTypeKind.U64:
            case WitTypeKind.S64:
                sb.AppendLine($"*(long*){baseVar} = (long){elemExpr};");
                break;
            case WitTypeKind.F32:
                sb.AppendLine($"*(float*){baseVar} = {elemExpr};");
                break;
            case WitTypeKind.F64:
                sb.AppendLine($"*(double*){baseVar} = {elemExpr};");
                break;
            case WitTypeKind.String:
                sb.AppendLine($"var elemByteLen = global::System.Text.Encoding.UTF8.GetByteCount({elemExpr});");
                sb.AppendLine($"var elemPtr = WitBindgen.Runtime.InteropHelpers.Alloc(elemByteLen, 1);");
                sb.AppendLine($"global::System.Text.Encoding.UTF8.GetBytes({elemExpr}, new Span<byte>((void*)elemPtr, elemByteLen));");
                sb.AppendLine($"*(int*){baseVar} = (int)elemPtr;");
                sb.AppendLine($"*(int*)({baseVar} + 4) = elemByteLen;");
                break;
            case WitTypeKind.Resource:
            case WitTypeKind.Borrow:
                sb.AppendLine($"*(int*){baseVar} = {elemExpr}.Handle;");
                break;
            case WitTypeKind.Record:
                if (elemType is WitRecordType recType)
                {
                    int offset = 0;
                    foreach (var field in recType.Fields)
                    {
                        var fieldAlign = CanonicalAbi.MemoryAlign(field.Type);
                        offset = CanonicalAbi.AlignTo(offset, fieldAlign);
                        WriteMemoryStore(sb, field.Type, $"{elemExpr}.{field.CSharpName}", baseVar, offset);
                        offset += CanonicalAbi.MemorySize(field.Type);
                    }
                }
                break;
            case WitTypeKind.Tuple:
                if (elemType is WitTupleType tupElemType)
                {
                    int offset = 0;
                    for (int ti = 0; ti < tupElemType.ElementTypes.Length; ti++)
                    {
                        var et = tupElemType.ElementTypes[ti];
                        offset = CanonicalAbi.AlignTo(offset, CanonicalAbi.MemoryAlign(et));
                        WriteMemoryStore(sb, et, $"{elemExpr}.Item{ti + 1}", baseVar, offset);
                        offset += CanonicalAbi.MemorySize(et);
                    }
                }
                break;
            case WitTypeKind.Variant:
                if (elemType is WitVariantType varType)
                {
                    var discKw = CanonicalAbi.IntStoreKeyword(CanonicalAbi.VariantDiscSize(varType));
                    sb.AppendLine($"*({discKw}*){baseVar} = ({discKw}){elemExpr}.Discriminant;");
                    var payloadOffset = CanonicalAbi.VariantPayloadOffset(varType);
                    sb.AppendLine($"switch ({elemExpr}.Discriminant)");
                    using (sb.Block())
                    {
                        foreach (var @case in varType.Values)
                        {
                            if (@case.Type is not null)
                            {
                                var caseName = StringUtils.GetName(@case.Name);
                                sb.AppendLine($"case {CanonicalAbi.WitTypeToCS(elemType)}.Case.{caseName}:");
                                sb.IncrementIndent();
                                WriteMemoryStore(sb, @case.Type, $"{elemExpr}.{caseName}Payload", baseVar, payloadOffset);
                                sb.AppendLine("break;");
                                sb.DecrementIndent();
                            }
                        }
                    }
                }
                break;
            default:
                sb.AppendLine($"// TODO: lower list element of type {elemType.Kind}");
                break;
        }
    }

    private static void WriteMemoryStore(IndentedStringBuilder sb, WitType type, string expr, string baseVar, int offset)
    {
        type = CanonicalAbi.ResolveType(type);
        var offsetExpr = offset > 0 ? $"{baseVar} + {offset}" : baseVar;
        switch (type.Kind)
        {
            case WitTypeKind.Bool:
                sb.AppendLine($"*({offsetExpr}) = (byte)({expr} ? 1 : 0);");
                break;
            case WitTypeKind.U8:
            case WitTypeKind.S8:
                sb.AppendLine($"*({offsetExpr}) = (byte){expr};");
                break;
            case WitTypeKind.U16:
            case WitTypeKind.S16:
                sb.AppendLine($"*(short*)({offsetExpr}) = (short){expr};");
                break;
            case WitTypeKind.U32:
            case WitTypeKind.S32:
            case WitTypeKind.Char:
                sb.AppendLine($"*(int*)({offsetExpr}) = (int){expr};");
                break;
            case WitTypeKind.Enum:
            {
                var enumKw = CanonicalAbi.IntStoreKeyword(CanonicalAbi.EnumSizeOf(type));
                sb.AppendLine($"*({enumKw}*)({offsetExpr}) = ({enumKw}){expr};");
                break;
            }
            case WitTypeKind.U64:
            case WitTypeKind.S64:
                sb.AppendLine($"*(long*)({offsetExpr}) = (long){expr};");
                break;
            case WitTypeKind.F32:
                sb.AppendLine($"*(float*)({offsetExpr}) = {expr};");
                break;
            case WitTypeKind.F64:
                sb.AppendLine($"*(double*)({offsetExpr}) = {expr};");
                break;
            case WitTypeKind.Resource:
            case WitTypeKind.Borrow:
                sb.AppendLine($"*(int*)({offsetExpr}) = {expr}.Handle;");
                break;
            case WitTypeKind.String:
                var uid = s_memStoreCounter++;
                sb.AppendLine($"var sMem{uid}ByteLen = global::System.Text.Encoding.UTF8.GetByteCount({expr});");
                sb.AppendLine($"var sMem{uid}Ptr = WitBindgen.Runtime.InteropHelpers.Alloc(sMem{uid}ByteLen, 1);");
                sb.AppendLine($"global::System.Text.Encoding.UTF8.GetBytes({expr}, new Span<byte>((void*)sMem{uid}Ptr, sMem{uid}ByteLen));");
                sb.AppendLine($"*(int*)({offsetExpr}) = (int)sMem{uid}Ptr;");
                sb.AppendLine($"*(int*)({offsetExpr} + 4) = sMem{uid}ByteLen;");
                break;
            case WitTypeKind.Record:
                if (type is WitRecordType recType)
                {
                    int fieldOffset = offset;
                    foreach (var field in recType.Fields)
                    {
                        var fAlign = CanonicalAbi.MemoryAlign(field.Type);
                        fieldOffset = CanonicalAbi.AlignTo(fieldOffset, fAlign);
                        WriteMemoryStore(sb, field.Type, $"{expr}.{field.CSharpName}", baseVar, fieldOffset);
                        fieldOffset += CanonicalAbi.MemorySize(field.Type);
                    }
                }
                break;
            case WitTypeKind.Tuple:
                if (type is WitTupleType tupStoreType)
                {
                    int fieldOffset = offset;
                    for (int ti = 0; ti < tupStoreType.ElementTypes.Length; ti++)
                    {
                        var et = tupStoreType.ElementTypes[ti];
                        fieldOffset = CanonicalAbi.AlignTo(fieldOffset, CanonicalAbi.MemoryAlign(et));
                        WriteMemoryStore(sb, et, $"{expr}.Item{ti + 1}", baseVar, fieldOffset);
                        fieldOffset += CanonicalAbi.MemorySize(et);
                    }
                }
                break;
            case WitTypeKind.Option:
                if (type is WitOptionType optStoreType)
                {
                    var inner = CanonicalAbi.ResolveType(optStoreType.ElementType);
                    var innerCs = CanonicalAbi.WitTypeToCS(optStoreType.ElementType);
                    var payloadOffset = offset + CanonicalAbi.AlignTo(1, CanonicalAbi.MemoryAlign(inner));
                    sb.AppendLine($"if ({expr} != null)");
                    using (sb.Block())
                    {
                        sb.AppendLine($"*(byte*)({offsetExpr}) = 1;");
                        WriteMemoryStore(sb, inner, $"(({innerCs}){expr})", baseVar, payloadOffset);
                    }
                    sb.AppendLine("else");
                    using (sb.Block())
                    {
                        sb.AppendLine($"*(byte*)({offsetExpr}) = 0;");
                    }
                }
                break;
            case WitTypeKind.Variant:
                if (type is WitVariantType varStoreType)
                {
                    var csName = CanonicalAbi.WitTypeToCS(type);
                    var discKw = CanonicalAbi.IntStoreKeyword(CanonicalAbi.VariantDiscSize(varStoreType));
                    sb.AppendLine($"*({discKw}*)({offsetExpr}) = ({discKw}){expr}.Discriminant;");
                    var payloadOffset = offset + CanonicalAbi.VariantPayloadOffset(varStoreType);
                    sb.AppendLine($"switch ({expr}.Discriminant)");
                    using (sb.Block())
                    {
                        foreach (var c in varStoreType.Values)
                        {
                            if (c.Type is not null)
                            {
                                var caseName = StringUtils.GetName(c.Name);
                                sb.AppendLine($"case {csName}.Case.{caseName}:");
                                sb.IncrementIndent();
                                WriteMemoryStore(sb, c.Type, $"{expr}.{caseName}Payload", baseVar, payloadOffset);
                                sb.AppendLine("break;");
                                sb.DecrementIndent();
                            }
                        }
                    }
                }
                break;
            case WitTypeKind.Result:
                if (true)
                {
                    var (okT, errT) = CanonicalAbi.ResultArms(type);
                    var payOff = offset + CanonicalAbi.ResultPayloadOffset(type);
                    sb.AppendLine($"*(byte*)({offsetExpr}) = (byte)({expr}.IsOk ? 0 : 1);");
                    if (okT is not null)
                    {
                        sb.AppendLine($"if ({expr}.IsOk)");
                        using (sb.Block())
                            WriteMemoryStore(sb, okT, $"{expr}.Ok", baseVar, payOff);
                        if (errT is not null)
                        {
                            sb.AppendLine("else");
                            using (sb.Block())
                                WriteMemoryStore(sb, errT, $"{expr}.Err", baseVar, payOff);
                        }
                    }
                    else if (errT is not null)
                    {
                        sb.AppendLine($"if (!{expr}.IsOk)");
                        using (sb.Block())
                            WriteMemoryStore(sb, errT, $"{expr}.Err", baseVar, payOff);
                    }
                }
                break;
            case WitTypeKind.List:
                if (type is WitListType listStoreType)
                {
                    var elemSize = CanonicalAbi.MemorySize(listStoreType.ElementType);
                    var elemAlign = CanonicalAbi.MemoryAlign(listStoreType.ElementType);
                    var luid = s_memStoreCounter++;
                    sb.AppendLine($"var lstCnt{luid} = {expr}.Length;");
                    sb.AppendLine($"var lstPtr{luid} = WitBindgen.Runtime.InteropHelpers.Alloc(lstCnt{luid} * {elemSize}, {elemAlign});");
                    sb.AppendLine($"for (int lstIdx{luid} = 0; lstIdx{luid} < lstCnt{luid}; lstIdx{luid}++)");
                    using (sb.Block())
                    {
                        sb.AppendLine($"var lstElem{luid} = (byte*)lstPtr{luid} + lstIdx{luid} * {elemSize};");
                        WriteLowerListElement(sb, listStoreType.ElementType, $"{expr}[lstIdx{luid}]", $"lstElem{luid}");
                    }
                    sb.AppendLine($"*(int*)({offsetExpr}) = (int)lstPtr{luid};");
                    sb.AppendLine($"*(int*)({offsetExpr} + 4) = lstCnt{luid};");
                }
                break;
            default:
                sb.AppendLine($"*(int*)({offsetExpr}) = (int){expr};");
                break;
        }
    }

    private static string WriteMemoryLoad(IndentedStringBuilder sb, WitType type, string baseVar, int offset, string varPrefix)
    {
        type = CanonicalAbi.ResolveType(type);
        var offsetExpr = offset > 0 ? $"{baseVar} + {offset}" : baseVar;
        switch (type.Kind)
        {
            case WitTypeKind.Bool:
                return $"(*({offsetExpr}) != 0)";
            case WitTypeKind.U8:
                return $"*({offsetExpr})";
            case WitTypeKind.S8:
                return $"(sbyte)*({offsetExpr})";
            case WitTypeKind.U16:
                return $"*(ushort*)({offsetExpr})";
            case WitTypeKind.S16:
                return $"*(short*)({offsetExpr})";
            case WitTypeKind.U32:
                return $"*(uint*)({offsetExpr})";
            case WitTypeKind.S32:
                return $"*(int*)({offsetExpr})";
            case WitTypeKind.Char:
                return $"*(uint*)({offsetExpr})";
            case WitTypeKind.Enum:
            {
                var enumKw = CanonicalAbi.IntStoreKeyword(CanonicalAbi.EnumSizeOf(type));
                // Parenthesize the deref: `(Color)*(byte*)p` parses as multiplication (CS0119).
                return $"({CanonicalAbi.WitTypeToCS(type)})(*({enumKw}*)({offsetExpr}))";
            }
            case WitTypeKind.Flags:
                return $"({CanonicalAbi.WitTypeToCS(type)})(*(int*)({offsetExpr}))";
            case WitTypeKind.U64:
                return $"*(ulong*)({offsetExpr})";
            case WitTypeKind.S64:
                return $"*(long*)({offsetExpr})";
            case WitTypeKind.F32:
                return $"*(float*)({offsetExpr})";
            case WitTypeKind.F64:
                return $"*(double*)({offsetExpr})";
            case WitTypeKind.Resource:
            case WitTypeKind.Borrow:
                return GetResourceConstructorCall(type, $"*(int*)({offsetExpr})");
            case WitTypeKind.String:
            {
                var sVar = $"{varPrefix}Str";
                sb.AppendLine($"var {sVar}Ptr = (byte*)*(int*)({offsetExpr});");
                sb.AppendLine($"var {sVar}Len = *(int*)({offsetExpr} + 4);");
                sb.AppendLine($"var {sVar} = global::System.Text.Encoding.UTF8.GetString({sVar}Ptr, {sVar}Len);");
                return sVar;
            }
            case WitTypeKind.Record:
                if (type is WitRecordType recType)
                {
                    var recVar = $"{varPrefix}Rec";
                    sb.AppendLine($"var {recVar} = new {CanonicalAbi.WitTypeToCS(type)}();");
                    int fieldOffset = offset;
                    foreach (var field in recType.Fields)
                    {
                        var fAlign = CanonicalAbi.MemoryAlign(field.Type);
                        fieldOffset = CanonicalAbi.AlignTo(fieldOffset, fAlign);
                        var fieldExpr = WriteMemoryLoad(sb, field.Type, baseVar, fieldOffset, $"{varPrefix}{field.CSharpName}");
                        sb.AppendLine($"{recVar}.{field.CSharpName} = {fieldExpr};");
                        fieldOffset += CanonicalAbi.MemorySize(field.Type);
                    }
                    return recVar;
                }
                return "default";
            case WitTypeKind.Tuple:
                if (type is WitTupleType tupLoadType)
                {
                    var tupVar = $"{varPrefix}Tup";
                    sb.AppendLine($"{CanonicalAbi.WitTypeToCS(type)} {tupVar} = default;");
                    int fieldOffset = offset;
                    for (int ti = 0; ti < tupLoadType.ElementTypes.Length; ti++)
                    {
                        var et = tupLoadType.ElementTypes[ti];
                        var fAlign = CanonicalAbi.MemoryAlign(et);
                        fieldOffset = CanonicalAbi.AlignTo(fieldOffset, fAlign);
                        var fieldExpr = WriteMemoryLoad(sb, et, baseVar, fieldOffset, $"{varPrefix}Item{ti + 1}");
                        sb.AppendLine($"{tupVar}.Item{ti + 1} = {fieldExpr};");
                        fieldOffset += CanonicalAbi.MemorySize(et);
                    }
                    return tupVar;
                }
                return "default";
            default:
                return $"*(int*)({offsetExpr})";
        }
    }

    /// <summary>
    /// Builds the C# constructor-call expression that lifts a resource/borrow handle into its
    /// wrapper. Owned resources (own&lt;T&gt;) construct the class with owned:true so Dispose drops
    /// the handle; borrows (borrow&lt;T&gt;) construct the readonly struct TBorrow (never dropped).
    /// </summary>
    private static string GetResourceConstructorCall(WitType resourceType, string handleExpr)
    {
        // WitTypeToCS on the (possibly unresolved) type preserves cross-package qualification,
        // while the resolved kind decides whether this is an owned class or a borrow struct.
        var csType = CanonicalAbi.WitTypeToCS(resourceType);
        var resolvedKind = CanonicalAbi.ResolveType(resourceType).Kind;
        return resolvedKind == WitTypeKind.Resource
            ? $"new {csType}({handleExpr}, owned: true)"
            : $"new {csType}({handleExpr})";
    }

    private static bool NeedsPostReturn(WitType type)
    {
        type = CanonicalAbi.ResolveType(type);
        switch (type.Kind)
        {
            // String/List free their heap data; aggregates additionally free the Alloc'd return area.
            case WitTypeKind.String:
            case WitTypeKind.List:
            case WitTypeKind.Record:
            case WitTypeKind.Tuple:
            case WitTypeKind.Option:
            case WitTypeKind.Variant:
            case WitTypeKind.Result:
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Whether a type transitively contains heap allocations (string/list) that must be freed.
    /// </summary>
    private static bool ContainsHeap(WitType type)
    {
        type = CanonicalAbi.ResolveType(type);
        switch (type.Kind)
        {
            case WitTypeKind.String:
            case WitTypeKind.List:
                return true;
            case WitTypeKind.Result:
            {
                var (ok, err) = CanonicalAbi.ResultArms(type);
                return (ok is not null && ContainsHeap(ok)) || (err is not null && ContainsHeap(err));
            }
            case WitTypeKind.Record:
                if (type is WitRecordType rt)
                    foreach (var f in rt.Fields) if (ContainsHeap(f.Type)) return true;
                return false;
            case WitTypeKind.Tuple:
                if (type is WitTupleType tt)
                    foreach (var e in tt.ElementTypes) if (ContainsHeap(e)) return true;
                return false;
            case WitTypeKind.Option:
                return type is WitOptionType ot && ContainsHeap(ot.ElementType);
            case WitTypeKind.Variant:
                if (type is WitVariantType vt)
                    foreach (var c in vt.Values) if (c.Type is not null && ContainsHeap(c.Type)) return true;
                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// Recursively frees string/list heap inside a value stored at baseVar+offset in memory layout.
    /// Mirrors WriteMemoryStore's layout; used by cabi_post_return for aggregate results.
    /// </summary>
    private static void WriteMemoryFree(IndentedStringBuilder sb, WitType type, string baseVar, int offset)
    {
        type = CanonicalAbi.ResolveType(type);
        var offsetExpr = offset > 0 ? $"{baseVar} + {offset}" : baseVar;
        switch (type.Kind)
        {
            case WitTypeKind.String:
                sb.AppendLine($"WitBindgen.Runtime.InteropHelpers.Free((void*)*(int*)({offsetExpr}), *(int*)({offsetExpr} + 4), 1);");
                break;
            case WitTypeKind.List:
                if (type is WitListType lt)
                {
                    var elemSize = CanonicalAbi.MemorySize(lt.ElementType);
                    var elemAlign = CanonicalAbi.MemoryAlign(lt.ElementType);
                    var uid = s_memStoreCounter++;
                    sb.AppendLine($"var frPtr{uid} = (byte*)*(int*)({offsetExpr});");
                    sb.AppendLine($"var frCnt{uid} = *(int*)({offsetExpr} + 4);");
                    if (ContainsHeap(lt.ElementType))
                    {
                        sb.AppendLine($"for (int frIdx{uid} = 0; frIdx{uid} < frCnt{uid}; frIdx{uid}++)");
                        using (sb.Block())
                        {
                            sb.AppendLine($"var frElem{uid} = frPtr{uid} + frIdx{uid} * {elemSize};");
                            WriteMemoryFree(sb, lt.ElementType, $"frElem{uid}", 0);
                        }
                    }
                    sb.AppendLine($"WitBindgen.Runtime.InteropHelpers.Free(frPtr{uid}, frCnt{uid} * {elemSize}, {elemAlign});");
                }
                break;
            case WitTypeKind.Record:
                if (type is WitRecordType rt)
                {
                    int fo = offset;
                    foreach (var f in rt.Fields)
                    {
                        fo = CanonicalAbi.AlignTo(fo, CanonicalAbi.MemoryAlign(f.Type));
                        if (ContainsHeap(f.Type)) WriteMemoryFree(sb, f.Type, baseVar, fo);
                        fo += CanonicalAbi.MemorySize(f.Type);
                    }
                }
                break;
            case WitTypeKind.Tuple:
                if (type is WitTupleType tt)
                {
                    int fo = offset;
                    for (int i = 0; i < tt.ElementTypes.Length; i++)
                    {
                        var et = tt.ElementTypes[i];
                        fo = CanonicalAbi.AlignTo(fo, CanonicalAbi.MemoryAlign(et));
                        if (ContainsHeap(et)) WriteMemoryFree(sb, et, baseVar, fo);
                        fo += CanonicalAbi.MemorySize(et);
                    }
                }
                break;
            case WitTypeKind.Option:
                if (type is WitOptionType ot && ContainsHeap(ot.ElementType))
                {
                    var inner = CanonicalAbi.ResolveType(ot.ElementType);
                    var po = offset + CanonicalAbi.AlignTo(1, CanonicalAbi.MemoryAlign(inner));
                    sb.AppendLine($"if (*(byte*)({offsetExpr}) != 0)");
                    using (sb.Block()) { WriteMemoryFree(sb, inner, baseVar, po); }
                }
                break;
            case WitTypeKind.Result:
                if (type.Kind == WitTypeKind.Result)
                {
                    var (okT, errT) = CanonicalAbi.ResultArms(type);
                    var payOff = offset + CanonicalAbi.ResultPayloadOffset(type);
                    var okHeap = okT is not null && ContainsHeap(okT);
                    var errHeap = errT is not null && ContainsHeap(errT);
                    if (okHeap || errHeap)
                    {
                        sb.AppendLine($"if (*(byte*)({offsetExpr}) == 0)");
                        using (sb.Block())
                        {
                            if (okHeap) WriteMemoryFree(sb, okT!, baseVar, payOff);
                        }
                        if (errHeap)
                        {
                            sb.AppendLine("else");
                            using (sb.Block())
                            {
                                WriteMemoryFree(sb, errT!, baseVar, payOff);
                            }
                        }
                    }
                }
                break;
            case WitTypeKind.Variant:
                if (type is WitVariantType vt)
                {
                    var po = offset + CanonicalAbi.VariantPayloadOffset(vt);
                    var csName = CanonicalAbi.WitTypeToCS(type);
                    var discKw = CanonicalAbi.IntStoreKeyword(CanonicalAbi.VariantDiscSize(vt));
                    sb.AppendLine($"switch (({csName}.Case)(*({discKw}*)({offsetExpr})))");
                    using (sb.Block())
                    {
                        foreach (var c in vt.Values)
                        {
                            if (c.Type is not null && ContainsHeap(c.Type))
                            {
                                var caseName = StringUtils.GetName(c.Name);
                                sb.AppendLine($"case {csName}.Case.{caseName}:");
                                sb.IncrementIndent();
                                WriteMemoryFree(sb, c.Type, baseVar, po);
                                sb.AppendLine("break;");
                                sb.DecrementIndent();
                            }
                        }
                    }
                }
                break;
        }
    }

    private static void WritePostReturn(IndentedStringBuilder sb, string entryPoint, WitFuncType func)
    {
        var postEntryPoint = $"cabi_post_{entryPoint}";
        var safeName = funcName(postEntryPoint);

        sb.AppendLine($"[global::System.Runtime.InteropServices.UnmanagedCallersOnly(EntryPoint = \"{postEntryPoint}\")]");
        sb.AppendLine($"private static unsafe void __wit_post_return_{funcName(entryPoint)}(nint retPtr)");
        using (sb.Block())
        {
            if (func.Results.Length > 0)
            {
                var resultType = CanonicalAbi.ResolveType(func.Results[0]);
                if (resultType.Kind == WitTypeKind.String)
                {
                    sb.AppendLine("var ptr = (byte*)*(int*)retPtr;");
                    sb.AppendLine("var len = *(int*)(retPtr + 4);");
                    sb.AppendLine("WitBindgen.Runtime.InteropHelpers.Free(ptr, len, 1);");
                }
                else if (resultType.Kind == WitTypeKind.List && resultType is WitListType postListType)
                {
                    var elemSize = CanonicalAbi.MemorySize(postListType.ElementType);
                    var elemAlign = CanonicalAbi.MemoryAlign(postListType.ElementType);

                    sb.AppendLine("var listPtr = (byte*)*(int*)retPtr;");
                    sb.AppendLine("var listCount = *(int*)(retPtr + 4);");

                    if (postListType.ElementType.Kind == WitTypeKind.String)
                    {
                        sb.AppendLine("for (int freeIdx = 0; freeIdx < listCount; freeIdx++)");
                        using (sb.Block())
                        {
                            sb.AppendLine($"var elemBase = listPtr + freeIdx * {elemSize};");
                            sb.AppendLine("WitBindgen.Runtime.InteropHelpers.Free((void*)*(int*)elemBase, *(int*)(elemBase + 4), 1);");
                        }
                    }

                    sb.AppendLine($"WitBindgen.Runtime.InteropHelpers.Free(listPtr, listCount * {elemSize}, {elemAlign});");
                }
                else
                {
                    // Aggregate result: free any nested string/list heap, then the Alloc'd return area.
                    if (ContainsHeap(resultType))
                        WriteMemoryFree(sb, resultType, "(byte*)retPtr", 0);
                    var size = CanonicalAbi.MemorySize(resultType);
                    var align = CanonicalAbi.MemoryAlign(resultType);
                    sb.AppendLine($"WitBindgen.Runtime.InteropHelpers.Free((void*)retPtr, {size}, {align});");
                }
            }
        }
    }

    /// <summary>
    /// Writes export bindings for all functions in an exported interface.
    /// </summary>
    public static void WriteExportInterface(
        IndentedStringBuilder sb,
        string moduleName,
        WitInterface interf)
    {
        sb.AppendLine($"public static unsafe partial class {interf.CSharpName}");
        using (sb.Block())
        {
            GuestTypeWriter.WriteAllTypes(sb, interf.Definitions);

            foreach (var field in interf.Fields)
            {
                if (field.Type is WitFuncType funcType)
                {
                    var entryPoint = $"{moduleName}#{field.Name}";
                    WriteExportFunction(sb, entryPoint, field.Name, funcType);
                    sb.AppendLine();
                }
            }
        }
    }
}
