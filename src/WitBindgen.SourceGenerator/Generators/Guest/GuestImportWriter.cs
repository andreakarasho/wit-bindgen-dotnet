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
        sb.AppendLine($"[System.Runtime.InteropServices.DllImport(\"{moduleName}\", EntryPoint = \"{funcName}\")]");
        sb.AppendLine("[System.Runtime.InteropServices.WasmImportLinkage]");

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
        // Build high-level parameter list
        var highLevelParams = new List<string>();
        foreach (var param in func.Parameters)
        {
            highLevelParams.Add($"{CanonicalAbi.WitTypeToCS(param.Type)} {param.CSharpVariableName}");
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

        sb.AppendLine("[System.Runtime.CompilerServices.SkipLocalsInit]");
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
                    retFlatCount += CanonicalAbi.FlatCount(r);

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
                var resultType = func.Results[0];
                if (resultType.Kind == WitTypeKind.String)
                {
                    // String returns need special handling even with 1 flat result
                    sb.AppendLine($"int* retArea = stackalloc int[2];");
                    sb.AppendLine($"WasmImports.{csharpFuncName}({(callArgs.Count > 0 ? callArgsStr + ", " : "")}(nint)retArea);");
                    WriteParamCleanup(sb, func);
                    sb.AppendLine("var resultPtr = (byte*)retArea[0];");
                    sb.AppendLine("var resultLen = retArea[1];");
                    sb.AppendLine("var result = System.Text.Encoding.UTF8.GetString(resultPtr, resultLen);");
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
                    sb.AppendLine($"WasmImports.{csharpFuncName}({callArgsStr});");
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
                var resultType = func.Results[0];
                if (resultType.Kind == WitTypeKind.String)
                {
                    sb.AppendLine("var resultPtr = (byte*)retArea[0];");
                    sb.AppendLine("var resultLen = retArea[1];");
                    sb.AppendLine("var result = System.Text.Encoding.UTF8.GetString(resultPtr, resultLen);");
                    sb.AppendLine("WitBindgen.Runtime.InteropHelpers.Free(resultPtr, resultLen, 1);");
                    sb.AppendLine("return result;");
                }
                else if (resultType.Kind == WitTypeKind.List && resultType is WitListType resultListType)
                {
                    var elemSize = CanonicalAbi.MemorySize(resultListType.ElementType);
                    var elemAlign = CanonicalAbi.MemoryAlign(resultListType.ElementType);

                    sb.AppendLine("var resultListPtr = (byte*)retArea[0];");
                    sb.AppendLine("var resultListCount = retArea[1];");
                    sb.AppendLine($"var resultList = new {CanonicalAbi.WitTypeToCS(resultType)}(resultListCount);");
                    sb.AppendLine("for (int resultIdx = 0; resultIdx < resultListCount; resultIdx++)");
                    using (sb.Block())
                    {
                        sb.AppendLine($"var resultElemBase = resultListPtr + resultIdx * {elemSize};");
                        WriteLiftListElement(sb, resultListType.ElementType, "resultElemBase", "resultList");
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
                else if (IsSimplePrimitive(resultType))
                {
                    sb.AppendLine($"return ({CanonicalAbi.WitTypeToCS(resultType)})retArea[0];");
                }
                else
                {
                    sb.AppendLine($"return default; // TODO: lift complex result type");
                }
            }
        }
    }

    private static void WriteLowerParam(IndentedStringBuilder sb, WitFuncParameter param, List<string> callArgs)
    {
        var type = param.Type;
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
                sb.AppendLine($"var {byteLenVar} = System.Text.Encoding.UTF8.GetByteCount({varName});");
                sb.AppendLine($"byte[]? {rentedVar} = null;");
                sb.AppendLine($"Span<byte> {bufVar} = {byteLenVar} <= 512");
                sb.AppendLine($"    ? stackalloc byte[{byteLenVar}]");
                sb.AppendLine($"    : new Span<byte>({rentedVar} = System.Buffers.ArrayPool<byte>.Shared.Rent({byteLenVar}), 0, {byteLenVar});");
                sb.AppendLine($"System.Text.Encoding.UTF8.GetBytes({varName}, {bufVar});");
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

                    sb.AppendLine($"var {countVar} = {varName}.Count;");
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
                            sb.AppendLine($"{byteCountsVar}[{varName}Idx] = System.Text.Encoding.UTF8.GetByteCount({varName}[{varName}Idx]);");
                            sb.AppendLine($"{totalBytesVar} += {byteCountsVar}[{varName}Idx];");
                        }

                        // stackalloc/ArrayPool for list metadata buffer
                        sb.AppendLine($"byte[]? {listRentedVar} = null;");
                        sb.AppendLine($"Span<byte> {listBufVar} = {varName}ListBufSize <= 512");
                        sb.AppendLine($"    ? stackalloc byte[{varName}ListBufSize]");
                        sb.AppendLine($"    : new Span<byte>({listRentedVar} = System.Buffers.ArrayPool<byte>.Shared.Rent({varName}ListBufSize), 0, {varName}ListBufSize);");

                        // Single batch buffer for ALL string bytes
                        sb.AppendLine($"byte[]? {stringsRentedVar} = null;");
                        sb.AppendLine($"Span<byte> {stringsBufVar} = {totalBytesVar} <= 1024");
                        sb.AppendLine($"    ? stackalloc byte[Math.Max({totalBytesVar}, 1)]");
                        sb.AppendLine($"    : new Span<byte>({stringsRentedVar} = System.Buffers.ArrayPool<byte>.Shared.Rent({totalBytesVar}), 0, Math.Max({totalBytesVar}, 1));");

                        sb.AppendLine($"byte* {listPtrVar} = WitBindgen.Runtime.InteropHelpers.SpanToPointer({listBufVar});");
                        sb.AppendLine($"byte* {stringsPtrVar} = WitBindgen.Runtime.InteropHelpers.SpanToPointer({stringsBufVar});");

                        // Write elements using offsets into the batch buffer
                        sb.AppendLine($"var {varName}StringOffset = 0;");
                        sb.AppendLine($"for (int {varName}Idx = 0; {varName}Idx < {countVar}; {varName}Idx++)");
                        using (sb.Block())
                        {
                            var idxVar = $"{varName}Idx";
                            sb.AppendLine($"var elemByteLen = {byteCountsVar}[{idxVar}];");
                            sb.AppendLine($"System.Text.Encoding.UTF8.GetBytes({varName}[{idxVar}], {stringsBufVar}.Slice({varName}StringOffset, elemByteLen));");
                            sb.AppendLine($"*(int*)({listPtrVar} + {idxVar} * {elemSize}) = (int)(nint)({stringsPtrVar} + {varName}StringOffset);");
                            sb.AppendLine($"*(int*)({listPtrVar} + {idxVar} * {elemSize} + 4) = elemByteLen;");
                            sb.AppendLine($"{varName}StringOffset += elemByteLen;");
                        }
                    }
                    else
                    {
                        // stackalloc/ArrayPool for list metadata buffer (primitive elements)
                        sb.AppendLine($"byte[]? {listRentedVar} = null;");
                        sb.AppendLine($"Span<byte> {listBufVar} = {varName}ListBufSize <= 512");
                        sb.AppendLine($"    ? stackalloc byte[{varName}ListBufSize]");
                        sb.AppendLine($"    : new Span<byte>({listRentedVar} = System.Buffers.ArrayPool<byte>.Shared.Rent({varName}ListBufSize), 0, {varName}ListBufSize);");
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

            default:
                callArgs.Add(varName);
                break;
        }
    }

    private static void WriteListElementLower(IndentedStringBuilder sb, WitType elemType, string elemExpr, string baseVar)
    {
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
            case WitTypeKind.Enum:
                sb.AppendLine($"*(int*){baseVar} = (int){elemExpr};");
                break;
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
                sb.AppendLine($"var elemByteLen = System.Text.Encoding.UTF8.GetByteCount({elemExpr});");
                sb.AppendLine($"var elemPtr = WitBindgen.Runtime.InteropHelpers.Alloc(elemByteLen, 1);");
                sb.AppendLine($"System.Text.Encoding.UTF8.GetBytes({elemExpr}, new Span<byte>((void*)elemPtr, elemByteLen));");
                sb.AppendLine($"*(int*){baseVar} = (int)elemPtr;");
                sb.AppendLine($"*(int*)({baseVar} + 4) = elemByteLen;");
                break;
            default:
                sb.AppendLine($"// TODO: lower list element of type {elemType.Kind}");
                break;
        }
    }

    private static void WriteLiftResult(IndentedStringBuilder sb, WitType type, string rawVar)
    {
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
                    sb.AppendLine($"if ({varName}Rented != null) System.Buffers.ArrayPool<byte>.Shared.Return({varName}Rented);");
                    break;

                case WitTypeKind.List:
                    if (type is WitListType listType)
                    {
                        if (listType.ElementType.Kind == WitTypeKind.String)
                        {
                            sb.AppendLine($"if ({varName}StringsRented != null) System.Buffers.ArrayPool<byte>.Shared.Return({varName}StringsRented);");
                        }

                        sb.AppendLine($"if ({varName}ListRented != null) System.Buffers.ArrayPool<byte>.Shared.Return({varName}ListRented);");
                    }
                    break;
            }
        }
    }

    private static void WriteLiftListElement(IndentedStringBuilder sb, WitType elemType, string baseVar, string listVar)
    {
        switch (elemType.Kind)
        {
            case WitTypeKind.Bool:
                sb.AppendLine($"{listVar}.Add(*{baseVar} != 0);");
                break;
            case WitTypeKind.U8:
                sb.AppendLine($"{listVar}.Add(*{baseVar});");
                break;
            case WitTypeKind.S8:
                sb.AppendLine($"{listVar}.Add((sbyte)*{baseVar});");
                break;
            case WitTypeKind.U16:
                sb.AppendLine($"{listVar}.Add(*(ushort*){baseVar});");
                break;
            case WitTypeKind.S16:
                sb.AppendLine($"{listVar}.Add(*(short*){baseVar});");
                break;
            case WitTypeKind.U32:
            case WitTypeKind.Char:
                sb.AppendLine($"{listVar}.Add(*(uint*){baseVar});");
                break;
            case WitTypeKind.S32:
                sb.AppendLine($"{listVar}.Add(*(int*){baseVar});");
                break;
            case WitTypeKind.U64:
                sb.AppendLine($"{listVar}.Add(*(ulong*){baseVar});");
                break;
            case WitTypeKind.S64:
                sb.AppendLine($"{listVar}.Add(*(long*){baseVar});");
                break;
            case WitTypeKind.F32:
                sb.AppendLine($"{listVar}.Add(*(float*){baseVar});");
                break;
            case WitTypeKind.F64:
                sb.AppendLine($"{listVar}.Add(*(double*){baseVar});");
                break;
            case WitTypeKind.String:
                sb.AppendLine($"var elemStrPtr = (byte*)*(int*){baseVar};");
                sb.AppendLine($"var elemStrLen = *(int*)({baseVar} + 4);");
                sb.AppendLine($"{listVar}.Add(System.Text.Encoding.UTF8.GetString(elemStrPtr, elemStrLen));");
                break;
            default:
                sb.AppendLine($"// TODO: lift list element of type {elemType.Kind}");
                sb.AppendLine($"{listVar}.Add(default);");
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
        var className = resource.CSharpName;
        var witName = resource.Name;

        sb.AppendLine($"public class {className} : System.IDisposable");
        using (sb.Block())
        {
            // Handle property and private constructor
            sb.AppendLine("internal int Handle { get; }");
            sb.AppendLine($"private {className}(int handle) {{ Handle = handle; }}");
            sb.AppendLine();

            // Public constructors
            foreach (var ctor in resource.Constructors)
            {
                WriteResourceConstructor(sb, moduleName, witName, className, ctor);
                sb.AppendLine();
            }

            // Instance methods
            foreach (var method in resource.Methods)
            {
                if (method.Type is WitFuncType funcType)
                {
                    WriteResourceMethod(sb, moduleName, witName, className, method.Name, funcType, isStatic: false);
                    sb.AppendLine();
                }
            }

            // Static methods
            foreach (var method in resource.StaticMethods)
            {
                if (method.Type is WitFuncType funcType)
                {
                    WriteResourceMethod(sb, moduleName, witName, className, method.Name, funcType, isStatic: true);
                    sb.AppendLine();
                }
            }

            // Dispose
            sb.AppendLine("public void Dispose()");
            using (sb.Block())
            {
                sb.AppendLine("WasmImports.ResourceDrop(Handle);");
            }
            sb.AppendLine();

            // WasmImports nested class
            WriteResourceWasmImports(sb, moduleName, witName, className, resource);
        }
    }

    private static void WriteResourceConstructor(
        IndentedStringBuilder sb,
        string moduleName,
        string witName,
        string className,
        WitResourceConstructor ctor)
    {
        var isFallible = ctor.ReturnType != null;

        if (isFallible)
        {
            // Fallible constructor -> static factory method
            var highLevelParams = new List<string>();
            foreach (var param in ctor.Parameters)
            {
                highLevelParams.Add($"{CanonicalAbi.WitTypeToCS(param.Type)} {param.CSharpVariableName}");
            }

            sb.AppendLine("[System.Runtime.CompilerServices.SkipLocalsInit]");
            sb.AppendLine($"public static unsafe {className} Create({string.Join(", ", highLevelParams)})");
            using (sb.Block())
            {
                sb.AppendLine($"// TODO: fallible constructor returns {CanonicalAbi.WitTypeToCS(ctor.ReturnType!)} — full result lifting not yet implemented");
                sb.AppendLine($"throw new System.NotImplementedException(\"Fallible resource constructors are not yet supported.\");");
            }
        }
        else
        {
            // Infallible constructor -> public constructor
            var highLevelParams = new List<string>();
            foreach (var param in ctor.Parameters)
            {
                highLevelParams.Add($"{CanonicalAbi.WitTypeToCS(param.Type)} {param.CSharpVariableName}");
            }

            sb.AppendLine("[System.Runtime.CompilerServices.SkipLocalsInit]");
            sb.AppendLine($"public unsafe {className}({string.Join(", ", highLevelParams)})");
            using (sb.Block())
            {
                // Lower parameters
                var callArgs = new List<string>();
                foreach (var param in ctor.Parameters)
                {
                    WriteLowerParam(sb, param, callArgs);
                }

                sb.AppendLine($"Handle = WasmImports.Constructor({string.Join(", ", callArgs)});");

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
        bool isStatic)
    {
        var csharpMethodName = StringUtils.GetName(methodName);
        var staticModifier = isStatic ? "static " : "";

        // Build high-level parameter list
        var highLevelParams = new List<string>();
        foreach (var param in funcType.Parameters)
        {
            highLevelParams.Add($"{CanonicalAbi.WitTypeToCS(param.Type)} {param.CSharpVariableName}");
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
            resultWitType = funcType.Results[0];
            returnType = CanonicalAbi.WitTypeToCS(resultWitType);
        }

        var useRetPtr = CanonicalAbi.ShouldUseRetPtr(funcType);

        sb.AppendLine("[System.Runtime.CompilerServices.SkipLocalsInit]");
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
                    retFlatCount += CanonicalAbi.FlatCount(r);
                sb.AppendLine($"int* retArea = stackalloc int[{Math.Max(retFlatCount, 2)}];");
                callArgs.Add("(nint)retArea");
            }

            // Make the call
            var callArgsStr = string.Join(", ", callArgs);

            if (returnType == "void" && !useRetPtr)
            {
                sb.AppendLine($"WasmImports.{csharpMethodName}({callArgsStr});");
                WriteResourceMethodCleanup(sb, funcType);
            }
            else if (!useRetPtr && funcType.Results.Length == 1)
            {
                if (resultWitType!.Kind == WitTypeKind.String)
                {
                    sb.AppendLine($"int* retArea = stackalloc int[2];");
                    sb.AppendLine($"WasmImports.{csharpMethodName}({(callArgs.Count > 0 ? callArgsStr + ", " : "")}(nint)retArea);");
                    WriteResourceMethodCleanup(sb, funcType);
                    sb.AppendLine("var resultPtr = (byte*)retArea[0];");
                    sb.AppendLine("var resultLen = retArea[1];");
                    sb.AppendLine("var result = System.Text.Encoding.UTF8.GetString(resultPtr, resultLen);");
                    sb.AppendLine("WitBindgen.Runtime.InteropHelpers.Free(resultPtr, resultLen, 1);");
                    sb.AppendLine("return result;");
                }
                else if (IsSimplePrimitive(resultWitType))
                {
                    sb.AppendLine($"var rawResult = WasmImports.{csharpMethodName}({callArgsStr});");
                    WriteResourceMethodCleanup(sb, funcType);
                    WriteLiftResult(sb, resultWitType, "rawResult");
                }
                else if (resultWitType.Kind == WitTypeKind.Resource || resultWitType.Kind == WitTypeKind.User)
                {
                    sb.AppendLine($"var rawResult = WasmImports.{csharpMethodName}({callArgsStr});");
                    WriteResourceMethodCleanup(sb, funcType);
                    sb.AppendLine($"return new {returnType}(rawResult);");
                }
                else
                {
                    sb.AppendLine($"WasmImports.{csharpMethodName}({callArgsStr});");
                    WriteResourceMethodCleanup(sb, funcType);
                }
            }
            else
            {
                sb.AppendLine($"WasmImports.{csharpMethodName}({callArgsStr});");
                WriteResourceMethodCleanup(sb, funcType);

                if (useRetPtr && funcType.Results.Length > 0)
                {
                    if (resultWitType!.Kind == WitTypeKind.String)
                    {
                        sb.AppendLine("var resultPtr = (byte*)retArea[0];");
                        sb.AppendLine("var resultLen = retArea[1];");
                        sb.AppendLine("var result = System.Text.Encoding.UTF8.GetString(resultPtr, resultLen);");
                        sb.AppendLine("WitBindgen.Runtime.InteropHelpers.Free(resultPtr, resultLen, 1);");
                        sb.AppendLine("return result;");
                    }
                    else if (resultWitType.Kind == WitTypeKind.List && resultWitType is WitListType resultListType)
                    {
                        var elemSize = CanonicalAbi.MemorySize(resultListType.ElementType);
                        var elemAlign = CanonicalAbi.MemoryAlign(resultListType.ElementType);

                        sb.AppendLine("var resultListPtr = (byte*)retArea[0];");
                        sb.AppendLine("var resultListCount = retArea[1];");
                        sb.AppendLine($"var resultList = new {CanonicalAbi.WitTypeToCS(resultWitType)}(resultListCount);");
                        sb.AppendLine("for (int resultIdx = 0; resultIdx < resultListCount; resultIdx++)");
                        using (sb.Block())
                        {
                            sb.AppendLine($"var resultElemBase = resultListPtr + resultIdx * {elemSize};");
                            WriteLiftListElement(sb, resultListType.ElementType, "resultElemBase", "resultList");
                        }

                        sb.AppendLine($"WitBindgen.Runtime.InteropHelpers.Free(resultListPtr, resultListCount * {elemSize}, {elemAlign});");
                        sb.AppendLine("return resultList;");
                    }
                    else if (resultWitType.Kind == WitTypeKind.Resource || resultWitType.Kind == WitTypeKind.User)
                    {
                        sb.AppendLine($"return new {returnType}(retArea[0]);");
                    }
                    else if (IsSimplePrimitive(resultWitType))
                    {
                        sb.AppendLine($"return ({CanonicalAbi.WitTypeToCS(resultWitType)})retArea[0];");
                    }
                    else
                    {
                        sb.AppendLine($"return default; // TODO: lift complex result type");
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
                sb.AppendLine($"if ({varName}Rented != null) System.Buffers.ArrayPool<byte>.Shared.Return({varName}Rented);");
                break;
            case WitTypeKind.List:
                if (type is WitListType listType)
                {
                    if (listType.ElementType.Kind == WitTypeKind.String)
                    {
                        sb.AppendLine($"if ({varName}StringsRented != null) System.Buffers.ArrayPool<byte>.Shared.Return({varName}StringsRented);");
                    }
                    sb.AppendLine($"if ({varName}ListRented != null) System.Buffers.ArrayPool<byte>.Shared.Return({varName}ListRented);");
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
        sb.AppendLine("private static partial class WasmImports");
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

                sb.AppendLine($"[System.Runtime.InteropServices.DllImport(\"{moduleName}\", EntryPoint = \"[constructor]{witName}\")]");
                sb.AppendLine("[System.Runtime.InteropServices.WasmImportLinkage]");
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
            sb.AppendLine($"[System.Runtime.InteropServices.DllImport(\"{moduleName}\", EntryPoint = \"[resource-drop]{witName}\")]");
            sb.AppendLine("[System.Runtime.InteropServices.WasmImportLinkage]");
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

        sb.AppendLine($"[System.Runtime.InteropServices.DllImport(\"{moduleName}\", EntryPoint = \"{entryPoint}\")]");
        sb.AppendLine("[System.Runtime.InteropServices.WasmImportLinkage]");
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
