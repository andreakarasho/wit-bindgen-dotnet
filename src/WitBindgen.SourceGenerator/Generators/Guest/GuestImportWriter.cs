using WitBindgen.SourceGenerator.Models;

namespace WitBindgen.SourceGenerator.Generators.Guest;

/// <summary>
/// Generates guest import bindings: [DllImport][WasmImportLinkage] stubs and high-level wrappers.
/// </summary>
public static class GuestImportWriter
{
    /// <summary>
    /// Writes import bindings for an imported function in a world.
    /// </summary>
    public static void WriteImportFunction(
        IndentedStringBuilder sb,
        string className,
        string moduleName,
        string funcName,
        WitFuncType func)
    {
        var csharpFuncName = StringUtils.GetName(funcName);
        var useRetPtr = CanonicalAbi.ShouldUseRetPtr(func);
        var flatParams = new List<(string type, string name)>();
        var flatResults = func.Results.Length > 0
            ? CanonicalAbi.Flatten(func.Results[0])
            : new List<CoreWasmType>();

        // Build flattened parameter list
        foreach (var param in func.Parameters)
        {
            var paramFlat = CanonicalAbi.Flatten(param.Type);
            if (paramFlat.Count == 1)
            {
                flatParams.Add((CanonicalAbi.CoreTypeToCS(paramFlat[0]), param.CSharpVariableName));
            }
            else
            {
                for (int i = 0; i < paramFlat.Count; i++)
                {
                    flatParams.Add((CanonicalAbi.CoreTypeToCS(paramFlat[i]), $"{param.CSharpVariableName}_{i}"));
                }
            }
        }

        // Write the raw DllImport in a nested class
        sb.AppendLine("private static partial class WasmImports");
        using (sb.Block())
        {
            WriteRawImport(sb, moduleName, funcName, csharpFuncName, flatParams, flatResults, useRetPtr);
        }

        sb.AppendLine();

        // Write the high-level wrapper method
        WriteHighLevelWrapper(sb, funcName, csharpFuncName, func, flatParams, useRetPtr);
    }

    private static void WriteRawImport(
        IndentedStringBuilder sb,
        string moduleName,
        string funcName,
        string csharpFuncName,
        List<(string type, string name)> flatParams,
        List<CoreWasmType> flatResults,
        bool useRetPtr)
    {
        sb.AppendLine($"[global::System.Runtime.InteropServices.DllImport(\"{moduleName}\", EntryPoint = \"{funcName}\")]");
        sb.AppendLine("[global::System.Runtime.InteropServices.WasmImportLinkage]");

        var paramList = new List<string>();
        foreach (var (type, name) in flatParams)
        {
            paramList.Add($"{type} {name}");
        }

        string returnType;
        if (useRetPtr)
        {
            paramList.Add("nint retPtr");
            returnType = "void";
        }
        else if (flatResults.Count == 1)
        {
            returnType = CanonicalAbi.CoreTypeToCS(flatResults[0]);
        }
        else if (flatResults.Count == 0)
        {
            returnType = "void";
        }
        else
        {
            paramList.Add("nint retPtr");
            returnType = "void";
        }

        sb.AppendLine($"internal static extern {returnType} {csharpFuncName}({string.Join(", ", paramList)});");
    }

    private static void WriteHighLevelWrapper(
        IndentedStringBuilder sb,
        string funcName,
        string csharpFuncName,
        WitFuncType func,
        List<(string type, string name)> flatParams,
        bool useRetPtr)
    {
        // Build high-level parameter list (use ReadOnlySpan<T> for blittable list params)
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
            returnType = "void"; // multi-return not directly supported yet
        }

        sb.AppendLine("[global::System.Runtime.CompilerServices.SkipLocalsInit]");
        sb.AppendLine($"public static unsafe {returnType} {csharpFuncName}({string.Join(", ", highLevelParams)})");
        using (sb.Block())
        {
            // Lower parameters
            var callArgs = new List<string>();
            foreach (var param in func.Parameters)
            {
                WriteLowerParam(sb, param, callArgs);
            }

            // Handle return area
            if (useRetPtr)
            {
                var retFlatCount = 0;
                foreach (var r in func.Results)
                    retFlatCount += (CanonicalAbi.MemorySize(r) + 3) / 4;

                sb.AppendLine($"int* retArea = stackalloc int[{Math.Max(retFlatCount, 2)}];");
                callArgs.Add("(nint)retArea");
            }

            // Make the call
            var callArgsStr = string.Join(", ", callArgs);
            if (returnType == "void" && !useRetPtr)
            {
                sb.AppendLine($"WasmImports.{csharpFuncName}({callArgsStr});");
            }
            else if (!useRetPtr && func.Results.Length == 1)
            {
                var resultType = CanonicalAbi.ResolveType(func.Results[0]);
                if (resultType.Kind == WitTypeKind.String)
                {
                    // String returns need special handling even with 1 flat result
                    sb.AppendLine($"int* retArea = stackalloc int[2];");
                    sb.AppendLine($"WasmImports.{csharpFuncName}({(callArgs.Count > 0 ? callArgsStr + ", " : "")}(nint)retArea);");
                    WriteParamCleanup(sb, func);
                    sb.AppendLine("var resultPtr = (byte*)retArea[0];");
                    sb.AppendLine("var resultLen = retArea[1];");
                    sb.AppendLine("var result = global::System.Text.Encoding.UTF8.GetString(resultPtr, resultLen);");
                    sb.AppendLine("WitBindgen.Runtime.InteropHelpers.Free(resultPtr, resultLen, 1);");
                    sb.AppendLine("return result;");
                    return;
                }
                else if (IsSimplePrimitive(resultType))
                {
                    sb.AppendLine($"var rawResult = WasmImports.{csharpFuncName}({callArgsStr});");
                    WriteParamCleanup(sb, func);
                    WriteLiftResult(sb, resultType, "rawResult");
                    return;
                }
                else
                {
                    // Single-flat non-primitive result (e.g. arity-1 tuple<prim>): capture and lift.
                    sb.AppendLine($"var rawResult = WasmImports.{csharpFuncName}({callArgsStr});");
                    WriteParamCleanup(sb, func);
                    WriteLiftResult(sb, resultType, "rawResult");
                    return;
                }
            }
            else
            {
                sb.AppendLine($"WasmImports.{csharpFuncName}({callArgsStr});");
            }

            // Free parameter allocations
            WriteParamCleanup(sb, func);

            // Lift results
            if (useRetPtr && func.Results.Length > 0)
            {
                var resultType = CanonicalAbi.ResolveType(func.Results[0]);
                if (resultType.Kind == WitTypeKind.String)
                {
                    sb.AppendLine("var resultPtr = (byte*)retArea[0];");
                    sb.AppendLine("var resultLen = retArea[1];");
                    sb.AppendLine("var result = global::System.Text.Encoding.UTF8.GetString(resultPtr, resultLen);");
                    sb.AppendLine("WitBindgen.Runtime.InteropHelpers.Free(resultPtr, resultLen, 1);");
                    sb.AppendLine("return result;");
                }
                else if (resultType.Kind == WitTypeKind.List && resultType is WitListType resultListType)
                {
                    var elemSize = CanonicalAbi.MemorySize(resultListType.ElementType);
                    var elemAlign = CanonicalAbi.MemoryAlign(resultListType.ElementType);
                    var resolvedElemType = CanonicalAbi.ResolveType(resultListType.ElementType);

                    sb.AppendLine("var resultListPtr = (byte*)retArea[0];");
                    sb.AppendLine("var resultListCount = retArea[1];");

                    if (CanonicalAbi.IsBlittable(resolvedElemType) && !CanonicalAbi.IsBlittablePrimitive(resolvedElemType))
                    {
                        // Blittable record: zero-copy OwnedSpan — no ToArray, free deferred to Dispose
                        var elemCsType = CanonicalAbi.WitListElementTypeToCS(resultListType);
                        sb.AppendLine($"return new WitBindgen.Runtime.OwnedSpan<{elemCsType}>(resultListPtr, resultListCount, resultListCount * {elemSize}, {elemAlign});");
                    }
                    else if (CanonicalAbi.IsBlittablePrimitive(resolvedElemType))
                    {
                        // Blittable primitive: bulk copy
                        var elemCsType = CanonicalAbi.WitListElementTypeToCS(resultListType);
                        sb.AppendLine($"var resultList = new Span<{elemCsType}>(resultListPtr, resultListCount).ToArray();");
                        sb.AppendLine($"WitBindgen.Runtime.InteropHelpers.Free(resultListPtr, resultListCount * {elemSize}, {elemAlign});");
                        sb.AppendLine("return resultList;");
                    }
                    else
                    {
                        sb.AppendLine($"var resultList = new {CanonicalAbi.WitListElementTypeToCS(resultListType)}[resultListCount];");
                        sb.AppendLine("for (int resultIdx = 0; resultIdx < resultListCount; resultIdx++)");
                        using (sb.Block())
                        {
                            sb.AppendLine($"var resultElemBase = resultListPtr + resultIdx * {elemSize};");
                            WriteLiftListElement(sb, resultListType.ElementType, "resultElemBase", "resultList", "resultIdx");
                        }

                        // Free host-allocated element buffers (e.g. string data)
                        if (resultListType.ElementType.Kind == WitTypeKind.String || resultListType.ElementType.Kind == WitTypeKind.List)
                        {
                            sb.AppendLine("for (int resultFreeIdx = 0; resultFreeIdx < resultListCount; resultFreeIdx++)");
                            using (sb.Block())
                            {
                                sb.AppendLine($"var resultFreeBase = resultListPtr + resultFreeIdx * {elemSize};");
                                WriteListElementCleanup(sb, resultListType.ElementType, "resultFreeBase");
                            }
                        }

                        sb.AppendLine($"WitBindgen.Runtime.InteropHelpers.Free(resultListPtr, resultListCount * {elemSize}, {elemAlign});");
                        sb.AppendLine("return resultList;");
                    }
                }
                else if (IsSimplePrimitive(resultType))
                {
                    sb.AppendLine($"return ({CanonicalAbi.WitTypeToCS(resultType)})retArea[0];");
                }
                else if (resultType.Kind == WitTypeKind.Resource || resultType.Kind == WitTypeKind.Borrow)
                {
                    sb.AppendLine($"return {GetResourceConstructorCall(resultType, "retArea[0]")};");
                }
                else
                {
                    // Indirect results are stored at retArea in canonical memory layout
                    // (per-field alignment), so read them with WriteMemoryLoad, not a packed slot model.
                    var lifted = WriteMemoryLoad(sb, resultType, "(byte*)retArea", 0, "ret");
                    sb.AppendLine($"return {lifted};");
                }
            }
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

    private static void WriteLowerParam(IndentedStringBuilder sb, WitFuncParameter param, List<string> callArgs)
    {
        var type = CanonicalAbi.ResolveType(param.Type);
        var varName = param.CSharpVariableName;

        switch (type.Kind)
        {
            case WitTypeKind.Bool:
                callArgs.Add($"({varName} ? 1 : 0)");
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
                callArgs.Add($"(int){varName}");
                break;

            case WitTypeKind.U64:
            case WitTypeKind.S64:
                callArgs.Add($"(long){varName}");
                break;

            case WitTypeKind.F32:
                callArgs.Add(varName);
                break;

            case WitTypeKind.F64:
                callArgs.Add(varName);
                break;

            case WitTypeKind.String:
                var byteLenVar = $"{varName}ByteLen";
                var rentedVar = $"{varName}Rented";
                var bufVar = $"{varName}Buf";
                var ptrVar = $"{varName}Ptr";
                sb.AppendLine($"var {byteLenVar} = global::System.Text.Encoding.UTF8.GetByteCount({varName});");
                sb.AppendLine($"byte[]? {rentedVar} = null;");
                sb.AppendLine($"Span<byte> {bufVar} = {byteLenVar} <= 512");
                sb.AppendLine($"    ? stackalloc byte[{byteLenVar}]");
                sb.AppendLine($"    : new Span<byte>({rentedVar} = global::System.Buffers.ArrayPool<byte>.Shared.Rent({byteLenVar}), 0, {byteLenVar});");
                sb.AppendLine($"global::System.Text.Encoding.UTF8.GetBytes({varName}, {bufVar});");
                sb.AppendLine($"var {ptrVar} = WitBindgen.Runtime.InteropHelpers.SpanToPointer({bufVar});");
                callArgs.Add($"(int)(nint){ptrVar}");
                callArgs.Add(byteLenVar);
                break;

            case WitTypeKind.List:
                if (type is WitListType listType)
                {
                    var countVar = $"{varName}Count";
                    var listPtrVar = $"{varName}ListPtr";
                    var listRentedVar = $"{varName}ListRented";
                    var listBufVar = $"{varName}ListBuf";
                    var elemSize = CanonicalAbi.MemorySize(listType.ElementType);
                    var elemAlign = CanonicalAbi.MemoryAlign(listType.ElementType);

                    sb.AppendLine($"var {countVar} = {varName}.Length;");
                    sb.AppendLine($"var {varName}ListBufSize = {countVar} * {elemSize};");

                    if (listType.ElementType.Kind == WitTypeKind.String)
                    {
                        // Batch allocation pattern for list<string>
                        var byteCountsVar = $"{varName}ByteCounts";
                        var totalBytesVar = $"{varName}TotalStringBytes";
                        var stringsRentedVar = $"{varName}StringsRented";
                        var stringsBufVar = $"{varName}StringsBuf";
                        var stringsPtrVar = $"{varName}StringsPtr";

                        // Pre-compute total string bytes
                        sb.AppendLine($"Span<int> {byteCountsVar} = {countVar} <= 128");
                        sb.AppendLine($"    ? stackalloc int[{countVar}]");
                        sb.AppendLine($"    : new int[{countVar}];");
                        sb.AppendLine($"var {totalBytesVar} = 0;");
                        sb.AppendLine($"for (int {varName}Idx = 0; {varName}Idx < {countVar}; {varName}Idx++)");
                        using (sb.Block())
                        {
                            sb.AppendLine($"{byteCountsVar}[{varName}Idx] = global::System.Text.Encoding.UTF8.GetByteCount({varName}[{varName}Idx]);");
                            sb.AppendLine($"{totalBytesVar} += {byteCountsVar}[{varName}Idx];");
                        }

                        // stackalloc/ArrayPool for list metadata buffer
                        sb.AppendLine($"byte[]? {listRentedVar} = null;");
                        sb.AppendLine($"Span<byte> {listBufVar} = {varName}ListBufSize <= 512");
                        sb.AppendLine($"    ? stackalloc byte[{varName}ListBufSize]");
                        sb.AppendLine($"    : new Span<byte>({listRentedVar} = global::System.Buffers.ArrayPool<byte>.Shared.Rent({varName}ListBufSize), 0, {varName}ListBufSize);");

                        // Single batch buffer for ALL string bytes
                        sb.AppendLine($"byte[]? {stringsRentedVar} = null;");
                        sb.AppendLine($"Span<byte> {stringsBufVar} = {totalBytesVar} <= 1024");
                        sb.AppendLine($"    ? stackalloc byte[Math.Max({totalBytesVar}, 1)]");
                        sb.AppendLine($"    : new Span<byte>({stringsRentedVar} = global::System.Buffers.ArrayPool<byte>.Shared.Rent({totalBytesVar}), 0, Math.Max({totalBytesVar}, 1));");

                        sb.AppendLine($"byte* {listPtrVar} = WitBindgen.Runtime.InteropHelpers.SpanToPointer({listBufVar});");
                        sb.AppendLine($"byte* {stringsPtrVar} = WitBindgen.Runtime.InteropHelpers.SpanToPointer({stringsBufVar});");

                        // Write elements using offsets into the batch buffer
                        sb.AppendLine($"var {varName}StringOffset = 0;");
                        sb.AppendLine($"for (int {varName}Idx = 0; {varName}Idx < {countVar}; {varName}Idx++)");
                        using (sb.Block())
                        {
                            var idxVar = $"{varName}Idx";
                            sb.AppendLine($"var elemByteLen = {byteCountsVar}[{idxVar}];");
                            sb.AppendLine($"global::System.Text.Encoding.UTF8.GetBytes({varName}[{idxVar}], {stringsBufVar}.Slice({varName}StringOffset, elemByteLen));");
                            sb.AppendLine($"*(int*)({listPtrVar} + {idxVar} * {elemSize}) = (int)(nint)({stringsPtrVar} + {varName}StringOffset);");
                            sb.AppendLine($"*(int*)({listPtrVar} + {idxVar} * {elemSize} + 4) = elemByteLen;");
                            sb.AppendLine($"{varName}StringOffset += elemByteLen;");
                        }
                    }
                    else if (CanonicalAbi.IsBlittable(CanonicalAbi.ResolveType(listType.ElementType))
                        && !CanonicalAbi.IsBlittablePrimitive(CanonicalAbi.ResolveType(listType.ElementType)))
                    {
                        // Blittable record: zero-copy via MemoryMarshal.Cast
                        var elemCsType = CanonicalAbi.WitListElementTypeToCS(listType);
                        sb.AppendLine($"byte* {listPtrVar} = (byte*)global::System.Runtime.CompilerServices.Unsafe.AsPointer(ref global::System.Runtime.InteropServices.MemoryMarshal.GetReference(global::System.Runtime.InteropServices.MemoryMarshal.AsBytes({varName})));");
                    }
                    else
                    {
                        // stackalloc/ArrayPool for list metadata buffer (primitive elements)
                        sb.AppendLine($"byte[]? {listRentedVar} = null;");
                        sb.AppendLine($"Span<byte> {listBufVar} = {varName}ListBufSize <= 512");
                        sb.AppendLine($"    ? stackalloc byte[{varName}ListBufSize]");
                        sb.AppendLine($"    : new Span<byte>({listRentedVar} = global::System.Buffers.ArrayPool<byte>.Shared.Rent({varName}ListBufSize), 0, {varName}ListBufSize);");
                        sb.AppendLine($"byte* {listPtrVar} = WitBindgen.Runtime.InteropHelpers.SpanToPointer({listBufVar});");

                        sb.AppendLine($"for (int {varName}Idx = 0; {varName}Idx < {countVar}; {varName}Idx++)");
                        using (sb.Block())
                        {
                            var idxVar = $"{varName}Idx";
                            var elemBaseVar = $"{varName}ElemBase";
                            sb.AppendLine($"var {elemBaseVar} = {listPtrVar} + {idxVar} * {elemSize};");
                            WriteListElementLower(sb, listType.ElementType, $"{varName}[{idxVar}]", elemBaseVar);
                        }
                    }

                    callArgs.Add($"(int)(nint){listPtrVar}");
                    callArgs.Add(countVar);
                }
                break;

            case WitTypeKind.Resource:
            case WitTypeKind.Borrow:
                callArgs.Add($"{varName}.Handle");
                break;

            case WitTypeKind.Record:
                if (type is WitRecordType recordType)
                {
                    foreach (var field in recordType.Fields)
                    {
                        WriteLowerFlat(sb, field.Type, $"{varName}.{field.CSharpName}", callArgs);
                    }
                }
                break;

            case WitTypeKind.Tuple:
                if (type is WitTupleType tupleTypeLower)
                {
                    for (int ti = 0; ti < tupleTypeLower.ElementTypes.Length; ti++)
                    {
                        WriteLowerFlat(sb, tupleTypeLower.ElementTypes[ti], $"{varName}.Item{ti + 1}", callArgs);
                    }
                }
                break;

            case WitTypeKind.Variant:
                if (type is WitVariantType variantType)
                {
                    callArgs.Add($"(int){varName}.Discriminant");

                    // Compute max payload flat count (total flat - 1 for discriminant)
                    var payloadFlatCount = CanonicalAbi.FlatCount(type) - 1;
                    if (payloadFlatCount > 0)
                    {
                        // Declare payload temp vars
                        var payloadFlat = CanonicalAbi.Flatten(type);
                        var tempVars = new List<string>();
                        for (int i = 0; i < payloadFlatCount; i++)
                        {
                            var tempType = CanonicalAbi.CoreTypeToCS(payloadFlat[i + 1]); // skip discriminant
                            var tempName = $"{varName}_p{i}";
                            sb.AppendLine($"{tempType} {tempName} = 0;");
                            tempVars.Add(tempName);
                        }

                        // Generate switch to lower active case's payload
                        sb.AppendLine($"switch ({varName}.Discriminant)");
                        using (sb.Block())
                        {
                            foreach (var @case in variantType.Values)
                            {
                                var caseName = StringUtils.GetName(@case.Name);
                                if (@case.Type is not null)
                                {
                                    sb.AppendLine($"case {CanonicalAbi.WitTypeToCS(type)}.Case.{caseName}:");
                                    sb.IncrementIndent();
                                    var caseCallArgs = new List<string>();
                                    WriteLowerFlat(sb, @case.Type, $"{varName}.{caseName}Payload", caseCallArgs);
                                    var caseFlat = CanonicalAbi.Flatten(@case.Type);
                                    for (int i = 0; i < caseCallArgs.Count && i < tempVars.Count; i++)
                                    {
                                        // The slot type is the canonical-ABI join across ALL cases
                                        // (e.g. an f32 case position joined with an i32 case -> i32);
                                        // reinterpret this case's value into the joined slot.
                                        var fromCt = i < caseFlat.Count ? caseFlat[i] : payloadFlat[i + 1];
                                        sb.AppendLine($"{tempVars[i]} = {CoerceFlatToSlot(caseCallArgs[i], fromCt, payloadFlat[i + 1])};");
                                    }
                                    sb.AppendLine("break;");
                                    sb.DecrementIndent();
                                }
                            }
                        }

                        foreach (var tv in tempVars)
                        {
                            callArgs.Add(tv);
                        }
                    }
                }
                break;

            case WitTypeKind.Result:
                WriteLowerResultFlat(sb, type, varName, callArgs);
                break;

            default:
                callArgs.Add(varName);
                break;
        }
    }

    /// <summary>
    /// Lowers a result into flat call args: discriminant (0=ok, 1=err) then the joined payload
    /// slots, filled from whichever arm is active. Mirrors the variant lowering structure.
    /// </summary>
    private static void WriteLowerResultFlat(IndentedStringBuilder sb, WitType type, string expr, List<string> callArgs)
    {
        var (okT, errT) = CanonicalAbi.ResultArms(type);
        callArgs.Add($"(int)({expr}.IsOk ? 0 : 1)");

        var payloadFlatCount = CanonicalAbi.FlatCount(type) - 1;
        if (payloadFlatCount <= 0)
            return;

        var payloadFlat = CanonicalAbi.Flatten(type);
        var uid = expr.Replace(".", "_").Replace("[", "_").Replace("]", "");
        var tempVars = new List<string>();
        for (int i = 0; i < payloadFlatCount; i++)
        {
            var tempType = CanonicalAbi.CoreTypeToCS(payloadFlat[i + 1]);
            var tempName = $"{uid}_rp{i}";
            sb.AppendLine($"{tempType} {tempName} = 0;");
            tempVars.Add(tempName);
        }

        void EmitArm(string condition, WitType armType, string accessor)
        {
            sb.AppendLine(condition);
            using (sb.Block())
            {
                var armArgs = new List<string>();
                WriteLowerFlat(sb, armType, accessor, armArgs);
                var armFlat = CanonicalAbi.Flatten(armType);
                for (int i = 0; i < armArgs.Count && i < tempVars.Count; i++)
                {
                    var fromCt = i < armFlat.Count ? armFlat[i] : payloadFlat[i + 1];
                    sb.AppendLine($"{tempVars[i]} = {CoerceFlatToSlot(armArgs[i], fromCt, payloadFlat[i + 1])};");
                }
            }
        }

        if (okT is not null)
        {
            EmitArm($"if ({expr}.IsOk)", okT, $"{expr}.Ok");
            if (errT is not null)
            {
                sb.AppendLine("else");
                using (sb.Block())
                {
                    var armArgs = new List<string>();
                    WriteLowerFlat(sb, errT, $"{expr}.Err", armArgs);
                    var armFlat = CanonicalAbi.Flatten(errT);
                    for (int i = 0; i < armArgs.Count && i < tempVars.Count; i++)
                    {
                        var fromCt = i < armFlat.Count ? armFlat[i] : payloadFlat[i + 1];
                        sb.AppendLine($"{tempVars[i]} = {CoerceFlatToSlot(armArgs[i], fromCt, payloadFlat[i + 1])};");
                    }
                }
            }
        }
        else if (errT is not null)
        {
            EmitArm($"if (!{expr}.IsOk)", errT, $"{expr}.Err");
        }

        callArgs.AddRange(tempVars);
    }

    /// <summary>
    /// Reinterprets a lowered flat value into a differently-typed join slot. The canonical
    /// ABI flattens a variant/result by joining core types per position across all cases
    /// (e.g. an f32 in one case and an i32 in another collapse to i32). When a case's value
    /// is stored into a wider/retyped slot, a numeric assignment would be wrong (or fail to
    /// compile: float -> int); reinterpret the bits instead. A no-op when the types match.
    /// </summary>
    private static string CoerceFlatToSlot(string expr, CoreWasmType from, CoreWasmType to)
    {
        if (from == to)
            return expr;
        return (from, to) switch
        {
            // float packed into an integer join slot — reinterpret the bit pattern.
            (CoreWasmType.F32, CoreWasmType.I32) => $"global::System.BitConverter.SingleToInt32Bits({expr})",
            (CoreWasmType.F64, CoreWasmType.I64) => $"global::System.BitConverter.DoubleToInt64Bits({expr})",
            (CoreWasmType.F32, CoreWasmType.I64) => $"(long)(uint)global::System.BitConverter.SingleToInt32Bits({expr})",
            // narrower integer widened into an i64 join slot.
            (CoreWasmType.I32, CoreWasmType.I64) => $"(long){expr}",
            _ => expr
        };
    }

    private static void WriteLowerFlat(IndentedStringBuilder sb, WitType type, string expr, List<string> callArgs)
    {
        type = CanonicalAbi.ResolveType(type);
        switch (type.Kind)
        {
            case WitTypeKind.Bool:
                callArgs.Add($"({expr} ? 1 : 0)");
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
                callArgs.Add($"(int){expr}");
                break;

            case WitTypeKind.U64:
            case WitTypeKind.S64:
                callArgs.Add($"(long){expr}");
                break;

            case WitTypeKind.F32:
            case WitTypeKind.F64:
                callArgs.Add(expr);
                break;

            case WitTypeKind.Resource:
            case WitTypeKind.Borrow:
                callArgs.Add($"{expr}.Handle");
                break;

            case WitTypeKind.String:
                var uid = expr.Replace(".", "_").Replace("[", "_").Replace("]", "");
                var byteLenVar = $"{uid}ByteLen";
                var rentedVar = $"{uid}Rented";
                var bufVar = $"{uid}Buf";
                var ptrVar = $"{uid}Ptr";
                sb.AppendLine($"var {byteLenVar} = global::System.Text.Encoding.UTF8.GetByteCount({expr});");
                sb.AppendLine($"byte[]? {rentedVar} = null;");
                sb.AppendLine($"Span<byte> {bufVar} = {byteLenVar} <= 512");
                sb.AppendLine($"    ? stackalloc byte[{byteLenVar}]");
                sb.AppendLine($"    : new Span<byte>({rentedVar} = global::System.Buffers.ArrayPool<byte>.Shared.Rent({byteLenVar}), 0, {byteLenVar});");
                sb.AppendLine($"global::System.Text.Encoding.UTF8.GetBytes({expr}, {bufVar});");
                sb.AppendLine($"var {ptrVar} = WitBindgen.Runtime.InteropHelpers.SpanToPointer({bufVar});");
                callArgs.Add($"(int)(nint){ptrVar}");
                callArgs.Add(byteLenVar);
                break;

            case WitTypeKind.Record:
                if (type is WitRecordType recordType)
                {
                    foreach (var field in recordType.Fields)
                    {
                        WriteLowerFlat(sb, field.Type, $"{expr}.{field.CSharpName}", callArgs);
                    }
                }
                break;

            case WitTypeKind.Tuple:
                if (type is WitTupleType tupleTypeFlat)
                {
                    for (int ti = 0; ti < tupleTypeFlat.ElementTypes.Length; ti++)
                    {
                        WriteLowerFlat(sb, tupleTypeFlat.ElementTypes[ti], $"{expr}.Item{ti + 1}", callArgs);
                    }
                }
                break;

            case WitTypeKind.Variant:
                if (type is WitVariantType variantType)
                {
                    callArgs.Add($"(int){expr}.Discriminant");

                    var payloadFlatCount = CanonicalAbi.FlatCount(type) - 1;
                    if (payloadFlatCount > 0)
                    {
                        var payloadFlat = CanonicalAbi.Flatten(type);
                        var tempVars = new List<string>();
                        var uid2 = expr.Replace(".", "_").Replace("[", "_").Replace("]", "");
                        for (int i = 0; i < payloadFlatCount; i++)
                        {
                            var tempType = CanonicalAbi.CoreTypeToCS(payloadFlat[i + 1]);
                            var tempName = $"{uid2}_vp{i}";
                            sb.AppendLine($"{tempType} {tempName} = 0;");
                            tempVars.Add(tempName);
                        }

                        sb.AppendLine($"switch ({expr}.Discriminant)");
                        using (sb.Block())
                        {
                            foreach (var @case in variantType.Values)
                            {
                                var caseName = StringUtils.GetName(@case.Name);
                                if (@case.Type is not null)
                                {
                                    sb.AppendLine($"case {CanonicalAbi.WitTypeToCS(type)}.Case.{caseName}:");
                                    sb.IncrementIndent();
                                    var caseCallArgs = new List<string>();
                                    WriteLowerFlat(sb, @case.Type, $"{expr}.{caseName}Payload", caseCallArgs);
                                    for (int i = 0; i < caseCallArgs.Count && i < tempVars.Count; i++)
                                    {
                                        sb.AppendLine($"{tempVars[i]} = {caseCallArgs[i]};");
                                    }
                                    sb.AppendLine("break;");
                                    sb.DecrementIndent();
                                }
                            }
                        }

                        foreach (var tv in tempVars)
                        {
                            callArgs.Add(tv);
                        }
                    }
                }
                break;

            case WitTypeKind.Result:
                WriteLowerResultFlat(sb, type, expr, callArgs);
                break;

            default:
                callArgs.Add(expr);
                break;
        }
    }

    private static void WriteListElementLower(IndentedStringBuilder sb, WitType elemType, string elemExpr, string baseVar)
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

    /// <summary>
    /// Writes a memory store for a single value at a byte offset from a base pointer.
    /// Used for record fields and variant payloads in list element lowering.
    /// </summary>
    private static int s_memStoreCounter;

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
            default:
                sb.AppendLine($"*(int*)({offsetExpr}) = (int){expr};");
                break;
        }
    }

    /// <summary>
    /// Reads a value from linear memory at a byte offset. Used for lifting list elements of complex types.
    /// </summary>
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
                // Parenthesize the deref: `(Color)*(byte*)p` parses as multiplication (CS0119);
                // `(Color)(*(byte*)p)` is the cast we mean.
                return $"({CanonicalAbi.WitTypeToCS(type)})(*({enumKw}*)({offsetExpr}))";
            }
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
            case WitTypeKind.Flags:
                return $"({CanonicalAbi.WitTypeToCS(type)})(*(int*)({offsetExpr}))";
            case WitTypeKind.Option:
                if (type is WitOptionType optLoadType)
                {
                    var inner = CanonicalAbi.ResolveType(optLoadType.ElementType);
                    var innerCs = CanonicalAbi.WitTypeToCS(optLoadType.ElementType);
                    var payloadOffset = offset + CanonicalAbi.AlignTo(1, CanonicalAbi.MemoryAlign(inner));
                    var optVar = $"{varPrefix}Opt";
                    sb.AppendLine($"{innerCs}? {optVar};");
                    sb.AppendLine($"if (*(byte*)({offsetExpr}) != 0)");
                    using (sb.Block())
                    {
                        var innerExpr = WriteMemoryLoad(sb, inner, baseVar, payloadOffset, $"{varPrefix}Inner");
                        sb.AppendLine($"{optVar} = {innerExpr};");
                    }
                    sb.AppendLine("else");
                    using (sb.Block())
                    {
                        sb.AppendLine($"{optVar} = null;");
                    }
                    return optVar;
                }
                return "default";
            case WitTypeKind.Variant:
                if (type is WitVariantType varLoadType)
                {
                    var csName = CanonicalAbi.WitTypeToCS(type);
                    var varVar = $"{varPrefix}Var";
                    sb.AppendLine($"{csName} {varVar} = default;");
                    var discKw = CanonicalAbi.IntStoreKeyword(CanonicalAbi.VariantDiscSize(varLoadType));
                    sb.AppendLine($"{varVar}.Discriminant = ({csName}.Case)(*({discKw}*)({offsetExpr}));");
                    var payloadOffset = offset + CanonicalAbi.VariantPayloadOffset(varLoadType);
                    sb.AppendLine($"switch ({varVar}.Discriminant)");
                    using (sb.Block())
                    {
                        foreach (var c in varLoadType.Values)
                        {
                            if (c.Type is not null)
                            {
                                var caseName = StringUtils.GetName(c.Name);
                                sb.AppendLine($"case {csName}.Case.{caseName}:");
                                sb.IncrementIndent();
                                var payloadExpr = WriteMemoryLoad(sb, c.Type, baseVar, payloadOffset, $"{varPrefix}{caseName}");
                                sb.AppendLine($"{varVar}.{caseName}Payload = {payloadExpr};");
                                sb.AppendLine("break;");
                                sb.DecrementIndent();
                            }
                        }
                    }
                    return varVar;
                }
                return "default";
            case WitTypeKind.Result:
            {
                var (okT, errT) = CanonicalAbi.ResultArms(type);
                var csType = CanonicalAbi.WitTypeToCS(type);
                var payOff = offset + CanonicalAbi.ResultPayloadOffset(type);
                var rVar = $"{varPrefix}Res";
                sb.AppendLine($"{csType} {rVar};");
                sb.AppendLine($"if (*(byte*)({offsetExpr}) == 0)");
                using (sb.Block())
                {
                    if (okT is not null)
                    {
                        var okExpr = WriteMemoryLoad(sb, okT, baseVar, payOff, $"{varPrefix}Ok");
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
                        var errExpr = WriteMemoryLoad(sb, errT, baseVar, payOff, $"{varPrefix}Err");
                        sb.AppendLine($"{rVar} = {csType}.FromErr({errExpr});");
                    }
                    else
                    {
                        sb.AppendLine($"{rVar} = {csType}.FromErr(default);");
                    }
                }
                return rVar;
            }
            case WitTypeKind.List:
                if (type is WitListType listLoadType)
                {
                    var elemSize = CanonicalAbi.MemorySize(listLoadType.ElementType);
                    var elemAlign = CanonicalAbi.MemoryAlign(listLoadType.ElementType);
                    var resolvedElem = CanonicalAbi.ResolveType(listLoadType.ElementType);
                    var elemCs = CanonicalAbi.WitListElementTypeToCS(listLoadType);
                    var lvar = $"{varPrefix}List";
                    sb.AppendLine($"var {lvar}Ptr = (byte*)*(int*)({offsetExpr});");
                    sb.AppendLine($"var {lvar}Count = *(int*)({offsetExpr} + 4);");
                    if (CanonicalAbi.IsBlittablePrimitive(resolvedElem))
                    {
                        sb.AppendLine($"var {lvar} = new Span<{elemCs}>({lvar}Ptr, {lvar}Count).ToArray();");
                    }
                    else
                    {
                        sb.AppendLine($"var {lvar} = new {elemCs}[{lvar}Count];");
                        sb.AppendLine($"for (int {lvar}Idx = 0; {lvar}Idx < {lvar}Count; {lvar}Idx++)");
                        using (sb.Block())
                        {
                            sb.AppendLine($"var {lvar}ElemBase = {lvar}Ptr + {lvar}Idx * {elemSize};");
                            WriteLiftListElement(sb, listLoadType.ElementType, $"{lvar}ElemBase", lvar, $"{lvar}Idx");
                        }
                    }
                    sb.AppendLine($"WitBindgen.Runtime.InteropHelpers.Free({lvar}Ptr, {lvar}Count * {elemSize}, {elemAlign});");
                    return lvar;
                }
                return "default";
            default:
                return $"*(int*)({offsetExpr})";
        }
    }

    private static void WriteLiftResult(IndentedStringBuilder sb, WitType type, string rawVar)
    {
        type = CanonicalAbi.ResolveType(type);
        switch (type.Kind)
        {
            case WitTypeKind.Bool:
                sb.AppendLine($"return {rawVar} != 0;");
                break;
            case WitTypeKind.U8:
                sb.AppendLine($"return (byte){rawVar};");
                break;
            case WitTypeKind.U16:
                sb.AppendLine($"return (ushort){rawVar};");
                break;
            case WitTypeKind.U32:
                sb.AppendLine($"return (uint){rawVar};");
                break;
            case WitTypeKind.S8:
                sb.AppendLine($"return (sbyte){rawVar};");
                break;
            case WitTypeKind.S16:
                sb.AppendLine($"return (short){rawVar};");
                break;
            case WitTypeKind.S32:
                sb.AppendLine($"return {rawVar};");
                break;
            case WitTypeKind.U64:
                sb.AppendLine($"return (ulong){rawVar};");
                break;
            case WitTypeKind.S64:
                sb.AppendLine($"return {rawVar};");
                break;
            case WitTypeKind.F32:
                sb.AppendLine($"return {rawVar};");
                break;
            case WitTypeKind.F64:
                sb.AppendLine($"return {rawVar};");
                break;
            case WitTypeKind.Tuple:
                // Only reached when the whole tuple flattened to a single core slot,
                // i.e. exactly one element that is itself a single-slot type. Multi-slot
                // tuples use the retArea path (WriteMemoryLoad) instead.
                if (type is WitTupleType singleTup && singleTup.ElementTypes.Length == 1)
                {
                    var elem = CanonicalAbi.ResolveType(singleTup.ElementTypes[0]);
                    var elemCs = CanonicalAbi.WitTypeToCS(singleTup.ElementTypes[0]);
                    var inner = elem.Kind switch
                    {
                        WitTypeKind.Bool => $"{rawVar} != 0",
                        WitTypeKind.Resource or WitTypeKind.Borrow => GetResourceConstructorCall(elem, rawVar),
                        _ => $"({elemCs}){rawVar}",
                    };
                    sb.AppendLine($"return new {CanonicalAbi.WitTypeToCS(type)}({inner});");
                }
                else
                {
                    sb.AppendLine($"return default;");
                }
                break;
            case WitTypeKind.Result:
            {
                // Reached only for a bare `result` (single discriminant slot); payload-bearing
                // results use the retptr/memory path.
                var csType = CanonicalAbi.WitTypeToCS(type);
                sb.AppendLine($"return {rawVar} == 0 ? {csType}.FromOk(default) : {csType}.FromErr(default);");
                break;
            }
            default:
                sb.AppendLine($"return ({CanonicalAbi.WitTypeToCS(type)}){rawVar};");
                break;
        }
    }

    private static void WriteParamCleanup(IndentedStringBuilder sb, WitFuncType func)
    {
        foreach (var param in func.Parameters)
        {
            var type = param.Type;
            var varName = param.CSharpVariableName;

            switch (type.Kind)
            {
                case WitTypeKind.String:
                    sb.AppendLine($"if ({varName}Rented != null) global::System.Buffers.ArrayPool<byte>.Shared.Return({varName}Rented);");
                    break;

                case WitTypeKind.List:
                    if (type is WitListType listType)
                    {
                        var resolvedElem = CanonicalAbi.ResolveType(listType.ElementType);
                        // Blittable records use MemoryMarshal.Cast (zero-copy) — no buffer to return
                        if (CanonicalAbi.IsBlittable(resolvedElem) && !CanonicalAbi.IsBlittablePrimitive(resolvedElem))
                            break;

                        if (listType.ElementType.Kind == WitTypeKind.String)
                        {
                            sb.AppendLine($"if ({varName}StringsRented != null) global::System.Buffers.ArrayPool<byte>.Shared.Return({varName}StringsRented);");
                        }

                        sb.AppendLine($"if ({varName}ListRented != null) global::System.Buffers.ArrayPool<byte>.Shared.Return({varName}ListRented);");
                    }
                    break;
            }
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
                // Width-correct read: a list's enum stride is its discriminant size (1/2/4),
                // not always 4. Mirror the export side; parenthesize the deref (CS0119).
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

    private static void WriteListElementCleanup(IndentedStringBuilder sb, WitType elemType, string baseVar)
    {
        switch (elemType.Kind)
        {
            case WitTypeKind.String:
                sb.AppendLine($"WitBindgen.Runtime.InteropHelpers.Free((void*)*(int*){baseVar}, *(int*)({baseVar} + 4), 1);");
                break;
            case WitTypeKind.List:
                // TODO: nested list cleanup
                break;
        }
    }

    private static bool IsSimplePrimitive(WitType type)
    {
        type = CanonicalAbi.ResolveType(type);
        return type.Kind switch
        {
            WitTypeKind.Bool or WitTypeKind.U8 or WitTypeKind.U16 or WitTypeKind.U32 or
            WitTypeKind.S8 or WitTypeKind.S16 or WitTypeKind.S32 or
            WitTypeKind.U64 or WitTypeKind.S64 or
            WitTypeKind.F32 or WitTypeKind.F64 or
            WitTypeKind.Char or WitTypeKind.Enum => true,
            _ => false
        };
    }

    /// <summary>
    /// Writes a C# class for an imported WIT resource type.
    /// </summary>
    public static void WriteImportResource(
        IndentedStringBuilder sb,
        string moduleName,
        WitResource resource)
    {
        // Disambiguate against a same-named enclosing interface (CS0542): e.g. the
        // `app` resource inside interface `app` becomes `AppResource`. No-op otherwise.
        var className = StringUtils.ResourceClassName(
            StringUtils.GetName(StringUtils.InterfaceNameFromModule(moduleName)), resource.Name);
        var borrowName = $"{className}Borrow";
        var witName = resource.Name;

        // --- Owned resource: a class with an ownership-guarded Dispose ---
        sb.AppendLine($"public class {className} : global::System.IDisposable");
        using (sb.Block())
        {
            // Handle property, ownership fields, internal constructor.
            sb.AppendLine("internal int Handle { get; }");
            sb.AppendLine("private readonly bool _owned;");
            sb.AppendLine("private bool _dropped;");
            sb.AppendLine($"internal {className}(int handle, bool owned = true) {{ Handle = handle; _owned = owned; _dropped = false; }}");
            sb.AppendLine();

            // Public constructors (own the handle they create).
            foreach (var ctor in resource.Constructors)
            {
                WriteResourceConstructor(sb, moduleName, witName, className, ctor, "WasmImports");
                sb.AppendLine();
            }

            // Instance methods
            foreach (var method in resource.Methods)
            {
                if (method.Type is WitFuncType funcType)
                {
                    WriteResourceMethod(sb, moduleName, witName, className, method.Name, funcType, isStatic: false, "WasmImports");
                    sb.AppendLine();
                }
            }

            // Static methods
            foreach (var method in resource.StaticMethods)
            {
                if (method.Type is WitFuncType funcType)
                {
                    WriteResourceMethod(sb, moduleName, witName, className, method.Name, funcType, isStatic: true, "WasmImports");
                    sb.AppendLine();
                }
            }

            // Ownership-guarded Dispose: only owned handles are dropped, and only once.
            sb.AppendLine("public void Dispose()");
            using (sb.Block())
            {
                sb.AppendLine("if (_owned && !_dropped)");
                using (sb.Block())
                {
                    sb.AppendLine("WasmImports.ResourceDrop(Handle);");
                    sb.AppendLine("_dropped = true;");
                }
            }
            sb.AppendLine();

            // Implicit conversion lets an owned resource be passed where a borrow is expected.
            sb.AppendLine($"public static implicit operator {borrowName}({className} owned) => new {borrowName}(owned.Handle);");
            sb.AppendLine();

            // WasmImports nested class (shared by both the class and the borrow struct).
            WriteResourceWasmImports(sb, moduleName, witName, className, resource);
        }

        sb.AppendLine();

        // --- Borrowed resource: a readonly struct with the same instance methods, no Dispose ---
        sb.AppendLine($"public readonly struct {borrowName}");
        using (sb.Block())
        {
            sb.AppendLine("internal int Handle { get; }");
            sb.AppendLine($"internal {borrowName}(int handle) {{ Handle = handle; }}");
            sb.AppendLine();

            // Instance methods only — identical bodies to the class, calling the class's WasmImports.
            foreach (var method in resource.Methods)
            {
                if (method.Type is WitFuncType funcType)
                {
                    WriteResourceMethod(sb, moduleName, witName, className, method.Name, funcType, isStatic: false, $"{className}.WasmImports");
                    sb.AppendLine();
                }
            }
        }
    }

    private static void WriteResourceConstructor(
        IndentedStringBuilder sb,
        string moduleName,
        string witName,
        string className,
        WitResourceConstructor ctor,
        string wasmImports)
    {
        var isFallible = ctor.ReturnType != null;

        if (isFallible)
        {
            // Fallible constructor -> static factory method
            var highLevelParams = new List<string>();
            foreach (var param in ctor.Parameters)
            {
                highLevelParams.Add($"{CanonicalAbi.WitTypeToCSParam(param.Type)} {param.CSharpVariableName}");
            }

            sb.AppendLine("[global::System.Runtime.CompilerServices.SkipLocalsInit]");
            sb.AppendLine($"public static unsafe {className} Create({string.Join(", ", highLevelParams)})");
            using (sb.Block())
            {
                sb.AppendLine($"// TODO: fallible constructor returns {CanonicalAbi.WitTypeToCS(ctor.ReturnType!)} — full result lifting not yet implemented");
                sb.AppendLine($"throw new global::System.NotImplementedException(\"Fallible resource constructors are not yet supported.\");");
            }
        }
        else
        {
            // Infallible constructor -> public constructor
            var highLevelParams = new List<string>();
            foreach (var param in ctor.Parameters)
            {
                highLevelParams.Add($"{CanonicalAbi.WitTypeToCSParam(param.Type)} {param.CSharpVariableName}");
            }

            sb.AppendLine("[global::System.Runtime.CompilerServices.SkipLocalsInit]");
            sb.AppendLine($"public unsafe {className}({string.Join(", ", highLevelParams)})");
            using (sb.Block())
            {
                // Lower parameters
                var callArgs = new List<string>();
                foreach (var param in ctor.Parameters)
                {
                    WriteLowerParam(sb, param, callArgs);
                }

                sb.AppendLine($"Handle = {wasmImports}.Constructor({string.Join(", ", callArgs)});");
                // A handle created by our constructor is owned and must be dropped on Dispose.
                sb.AppendLine("_owned = true;");
                sb.AppendLine("_dropped = false;");

                // Cleanup
                foreach (var param in ctor.Parameters)
                {
                    WriteResourceParamCleanup(sb, param);
                }
            }
        }
    }

    private static void WriteResourceMethod(
        IndentedStringBuilder sb,
        string moduleName,
        string witName,
        string className,
        string methodName,
        WitFuncType funcType,
        bool isStatic,
        string wasmImports)
    {
        var csharpMethodName = StringUtils.GetName(methodName);
        var staticModifier = isStatic ? "static " : "";

        // Build high-level parameter list (use ReadOnlySpan<T> for blittable list params)
        var highLevelParams = new List<string>();
        foreach (var param in funcType.Parameters)
        {
            highLevelParams.Add($"{CanonicalAbi.WitTypeToCSParam(param.Type)} {param.CSharpVariableName}");
        }

        // Determine return type
        string returnType;
        WitType? resultWitType = null;
        if (funcType.Results.Length == 0)
        {
            returnType = "void";
        }
        else
        {
            resultWitType = CanonicalAbi.ResolveType(funcType.Results[0]);
            returnType = CanonicalAbi.WitTypeToCS(resultWitType);
        }

        var useRetPtr = CanonicalAbi.ShouldUseRetPtr(funcType);

        sb.AppendLine("[global::System.Runtime.CompilerServices.SkipLocalsInit]");
        sb.AppendLine($"public {staticModifier}unsafe {returnType} {csharpMethodName}({string.Join(", ", highLevelParams)})");
        using (sb.Block())
        {
            // Lower parameters
            var callArgs = new List<string>();

            // Instance methods prepend Handle as first arg
            if (!isStatic)
            {
                callArgs.Add("Handle");
            }

            foreach (var param in funcType.Parameters)
            {
                WriteLowerParam(sb, param, callArgs);
            }

            // Handle return area
            if (useRetPtr)
            {
                var retFlatCount = 0;
                foreach (var r in funcType.Results)
                    retFlatCount += (CanonicalAbi.MemorySize(r) + 3) / 4;
                sb.AppendLine($"int* retArea = stackalloc int[{Math.Max(retFlatCount, 2)}];");
                callArgs.Add("(nint)retArea");
            }

            // Make the call
            var callArgsStr = string.Join(", ", callArgs);

            if (returnType == "void" && !useRetPtr)
            {
                sb.AppendLine($"{wasmImports}.{csharpMethodName}({callArgsStr});");
                WriteResourceMethodCleanup(sb, funcType);
            }
            else if (!useRetPtr && funcType.Results.Length == 1)
            {
                if (resultWitType!.Kind == WitTypeKind.String)
                {
                    sb.AppendLine($"int* retArea = stackalloc int[2];");
                    sb.AppendLine($"{wasmImports}.{csharpMethodName}({(callArgs.Count > 0 ? callArgsStr + ", " : "")}(nint)retArea);");
                    WriteResourceMethodCleanup(sb, funcType);
                    sb.AppendLine("var resultPtr = (byte*)retArea[0];");
                    sb.AppendLine("var resultLen = retArea[1];");
                    sb.AppendLine("var result = global::System.Text.Encoding.UTF8.GetString(resultPtr, resultLen);");
                    sb.AppendLine("WitBindgen.Runtime.InteropHelpers.Free(resultPtr, resultLen, 1);");
                    sb.AppendLine("return result;");
                }
                else if (IsSimplePrimitive(resultWitType))
                {
                    sb.AppendLine($"var rawResult = {wasmImports}.{csharpMethodName}({callArgsStr});");
                    WriteResourceMethodCleanup(sb, funcType);
                    WriteLiftResult(sb, resultWitType, "rawResult");
                }
                else if (resultWitType.Kind == WitTypeKind.Resource || resultWitType.Kind == WitTypeKind.Borrow)
                {
                    sb.AppendLine($"var rawResult = {wasmImports}.{csharpMethodName}({callArgsStr});");
                    WriteResourceMethodCleanup(sb, funcType);
                    sb.AppendLine($"return {GetResourceConstructorCall(resultWitType, "rawResult")};");
                }
                else
                {
                    sb.AppendLine($"{wasmImports}.{csharpMethodName}({callArgsStr});");
                    WriteResourceMethodCleanup(sb, funcType);
                }
            }
            else
            {
                sb.AppendLine($"{wasmImports}.{csharpMethodName}({callArgsStr});");
                WriteResourceMethodCleanup(sb, funcType);

                if (useRetPtr && funcType.Results.Length > 0)
                {
                    if (resultWitType!.Kind == WitTypeKind.String)
                    {
                        sb.AppendLine("var resultPtr = (byte*)retArea[0];");
                        sb.AppendLine("var resultLen = retArea[1];");
                        sb.AppendLine("var result = global::System.Text.Encoding.UTF8.GetString(resultPtr, resultLen);");
                        sb.AppendLine("WitBindgen.Runtime.InteropHelpers.Free(resultPtr, resultLen, 1);");
                        sb.AppendLine("return result;");
                    }
                    else if (resultWitType.Kind == WitTypeKind.List && resultWitType is WitListType resultListType)
                    {
                        var elemSize = CanonicalAbi.MemorySize(resultListType.ElementType);
                        var elemAlign = CanonicalAbi.MemoryAlign(resultListType.ElementType);
                        var resolvedElemType = CanonicalAbi.ResolveType(resultListType.ElementType);

                        sb.AppendLine("var resultListPtr = (byte*)retArea[0];");
                        sb.AppendLine("var resultListCount = retArea[1];");

                        if (CanonicalAbi.IsBlittablePrimitive(resolvedElemType))
                        {
                            var elemCsType = CanonicalAbi.WitListElementTypeToCS(resultListType);
                            sb.AppendLine($"var resultList = new Span<{elemCsType}>(resultListPtr, resultListCount).ToArray();");
                        }
                        else
                        {
                            sb.AppendLine($"var resultList = new {CanonicalAbi.WitListElementTypeToCS(resultListType)}[resultListCount];");
                            sb.AppendLine("for (int resultIdx = 0; resultIdx < resultListCount; resultIdx++)");
                            using (sb.Block())
                            {
                                sb.AppendLine($"var resultElemBase = resultListPtr + resultIdx * {elemSize};");
                                WriteLiftListElement(sb, resultListType.ElementType, "resultElemBase", "resultList", "resultIdx");
                            }
                        }

                        sb.AppendLine($"WitBindgen.Runtime.InteropHelpers.Free(resultListPtr, resultListCount * {elemSize}, {elemAlign});");
                        sb.AppendLine("return resultList;");
                    }
                    else if (resultWitType.Kind == WitTypeKind.Resource || resultWitType.Kind == WitTypeKind.Borrow)
                    {
                        sb.AppendLine($"return {GetResourceConstructorCall(resultWitType, "retArea[0]")};");
                    }
                    else if (IsSimplePrimitive(resultWitType))
                    {
                        sb.AppendLine($"return ({CanonicalAbi.WitTypeToCS(resultWitType)})retArea[0];");
                    }
                    else
                    {
                        // Indirect results use canonical memory layout (see WriteMemoryLoad).
                        var lifted = WriteMemoryLoad(sb, resultWitType, "(byte*)retArea", 0, "ret");
                        sb.AppendLine($"return {lifted};");
                    }
                }
            }
        }
    }

    private static void WriteResourceParamCleanup(IndentedStringBuilder sb, WitFuncParameter param)
    {
        var type = param.Type;
        var varName = param.CSharpVariableName;

        switch (type.Kind)
        {
            case WitTypeKind.String:
                sb.AppendLine($"if ({varName}Rented != null) global::System.Buffers.ArrayPool<byte>.Shared.Return({varName}Rented);");
                break;
            case WitTypeKind.List:
                if (type is WitListType listType)
                {
                    if (listType.ElementType.Kind == WitTypeKind.String)
                    {
                        sb.AppendLine($"if ({varName}StringsRented != null) global::System.Buffers.ArrayPool<byte>.Shared.Return({varName}StringsRented);");
                    }
                    sb.AppendLine($"if ({varName}ListRented != null) global::System.Buffers.ArrayPool<byte>.Shared.Return({varName}ListRented);");
                }
                break;
        }
    }

    private static void WriteResourceMethodCleanup(IndentedStringBuilder sb, WitFuncType funcType)
    {
        foreach (var param in funcType.Parameters)
        {
            WriteResourceParamCleanup(sb, param);
        }
    }

    private static void WriteResourceWasmImports(
        IndentedStringBuilder sb,
        string moduleName,
        string witName,
        string className,
        WitResource resource)
    {
        // internal (not private) so the sibling {className}Borrow struct can reuse these imports.
        sb.AppendLine("internal static partial class WasmImports");
        using (sb.Block())
        {
            // Constructor(s)
            foreach (var ctor in resource.Constructors)
            {
                if (ctor.ReturnType != null)
                    continue; // Skip fallible constructors since they're not implemented

                var flatParams = new List<(string type, string name)>();
                int paramIdx = 0;
                foreach (var param in ctor.Parameters)
                {
                    var paramFlat = CanonicalAbi.Flatten(param.Type);
                    for (int i = 0; i < paramFlat.Count; i++)
                    {
                        flatParams.Add((CanonicalAbi.CoreTypeToCS(paramFlat[i]), $"p{paramIdx}"));
                        paramIdx++;
                    }
                }

                var paramListStr = string.Join(", ", flatParams.Select(p => $"{p.type} {p.name}"));

                sb.AppendLine($"[global::System.Runtime.InteropServices.DllImport(\"{moduleName}\", EntryPoint = \"[constructor]{witName}\")]");
                sb.AppendLine("[global::System.Runtime.InteropServices.WasmImportLinkage]");
                sb.AppendLine($"internal static extern int Constructor({paramListStr});");
                sb.AppendLine();
            }

            // Instance methods
            foreach (var method in resource.Methods)
            {
                if (method.Type is WitFuncType funcType)
                {
                    WriteResourceWasmImportMethod(sb, moduleName, witName, method.Name, funcType, isStatic: false);
                    sb.AppendLine();
                }
            }

            // Static methods
            foreach (var method in resource.StaticMethods)
            {
                if (method.Type is WitFuncType funcType)
                {
                    WriteResourceWasmImportMethod(sb, moduleName, witName, method.Name, funcType, isStatic: true);
                    sb.AppendLine();
                }
            }

            // Resource drop
            sb.AppendLine($"[global::System.Runtime.InteropServices.DllImport(\"{moduleName}\", EntryPoint = \"[resource-drop]{witName}\")]");
            sb.AppendLine("[global::System.Runtime.InteropServices.WasmImportLinkage]");
            sb.AppendLine("internal static extern void ResourceDrop(int handle);");
        }
    }

    private static void WriteResourceWasmImportMethod(
        IndentedStringBuilder sb,
        string moduleName,
        string witName,
        string methodName,
        WitFuncType funcType,
        bool isStatic)
    {
        var csharpMethodName = StringUtils.GetName(methodName);
        var entryPointPrefix = isStatic ? "[static]" : "[method]";
        var entryPoint = $"{entryPointPrefix}{witName}.{methodName}";

        var useRetPtr = CanonicalAbi.ShouldUseRetPtr(funcType);
        var flatResults = funcType.Results.Length > 0
            ? CanonicalAbi.Flatten(funcType.Results[0])
            : new List<CoreWasmType>();

        var flatParams = new List<(string type, string name)>();

        // Instance methods prepend self handle
        if (!isStatic)
        {
            flatParams.Add(("int", "self"));
        }

        int paramIdx = 0;
        foreach (var param in funcType.Parameters)
        {
            var paramFlat = CanonicalAbi.Flatten(param.Type);
            for (int i = 0; i < paramFlat.Count; i++)
            {
                flatParams.Add((CanonicalAbi.CoreTypeToCS(paramFlat[i]), $"p{paramIdx}"));
                paramIdx++;
            }
        }

        string returnType;
        var paramList = flatParams.Select(p => $"{p.type} {p.name}").ToList();

        if (useRetPtr)
        {
            paramList.Add("nint retPtr");
            returnType = "void";
        }
        else if (flatResults.Count == 1)
        {
            returnType = CanonicalAbi.CoreTypeToCS(flatResults[0]);
        }
        else if (flatResults.Count == 0)
        {
            returnType = "void";
        }
        else
        {
            paramList.Add("nint retPtr");
            returnType = "void";
        }

        sb.AppendLine($"[global::System.Runtime.InteropServices.DllImport(\"{moduleName}\", EntryPoint = \"{entryPoint}\")]");
        sb.AppendLine("[global::System.Runtime.InteropServices.WasmImportLinkage]");
        sb.AppendLine($"internal static extern {returnType} {csharpMethodName}({string.Join(", ", paramList)});");
    }

    /// <summary>
    /// Writes import bindings for all functions in an imported interface.
    /// </summary>
    public static void WriteImportInterface(
        IndentedStringBuilder sb,
        string moduleName,
        WitInterface interf)
    {
        sb.AppendLine($"public static unsafe partial class {interf.CSharpName}");
        using (sb.Block())
        {
            // Write types defined in the interface (pass moduleName for resource generation)
            GuestTypeWriter.WriteAllTypes(sb, interf.Definitions, moduleName);

            // Write function imports
            foreach (var field in interf.Fields)
            {
                if (field.Type is WitFuncType funcType)
                {
                    WriteImportFunction(sb, interf.CSharpName, moduleName, field.Name, funcType);
                    sb.AppendLine();
                }
            }
        }
    }
}
