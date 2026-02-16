using WitBindgen.SourceGenerator.Models;

namespace WitBindgen.SourceGenerator.Generators.Guest;

/// <summary>
/// Generates guest export bindings: partial method declarations and [UnmanagedCallersOnly] trampolines.
/// </summary>
public static class GuestExportWriter
{
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

            // Free WASM memory for lifted string/list parameters (callee owns the memory per canonical ABI)
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

            // Call the user's implementation
            var callArgs = string.Join(", ", liftedArgs);
            if (func.Results.Length == 0)
            {
                sb.AppendLine($"{csharpFuncName}({callArgs});");
            }
            else
            {
                sb.AppendLine($"var result = {csharpFuncName}({callArgs});");

                // Lower the result
                var resultType = func.Results[0];
                LowerResult(sb, resultType, useRetPtr);
            }
        }
    }

    private static string funcName(string entryPoint)
    {
        return entryPoint.Replace("-", "_").Replace(":", "_").Replace("/", "_").Replace("@", "_");
    }

    private static void LiftParam(
        IndentedStringBuilder sb,
        WitFuncParameter param,
        List<(string type, string name)> coreParams,
        List<string> liftedArgs)
    {
        switch (param.Type.Kind)
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
            case WitTypeKind.Char:
                liftedArgs.Add(param.CSharpVariableName);
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
                if (param.Type is WitListType paramListType)
                {
                    var listPtrVar = $"{param.CSharpVariableName}_0";
                    var listCountVar = $"{param.CSharpVariableName}_1";
                    var liftedListVar = $"{param.CSharpVariableName}List";
                    var elemSize = CanonicalAbi.MemorySize(paramListType.ElementType);

                    sb.AppendLine($"var {liftedListVar} = new {CanonicalAbi.WitTypeToCS(param.Type)}({listCountVar});");
                    sb.AppendLine($"for (int {param.CSharpVariableName}LiftIdx = 0; {param.CSharpVariableName}LiftIdx < {listCountVar}; {param.CSharpVariableName}LiftIdx++)");
                    using (sb.Block())
                    {
                        var elemBaseVar = $"{param.CSharpVariableName}LiftBase";
                        sb.AppendLine($"var {elemBaseVar} = (byte*){listPtrVar} + {param.CSharpVariableName}LiftIdx * {elemSize};");
                        WriteLiftListElement(sb, paramListType.ElementType, elemBaseVar, liftedListVar);
                    }
                    liftedArgs.Add(liftedListVar);
                }
                break;

            default:
                liftedArgs.Add(param.CSharpVariableName);
                break;
        }
    }

    private static void LowerResult(IndentedStringBuilder sb, WitType resultType, bool useRetPtr)
    {
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

                    sb.AppendLine("var resultCount = result.Count;");
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

            default:
                if (useRetPtr)
                {
                    sb.AppendLine("return WitBindgen.Runtime.InteropHelpers.GetReturnArea(); // TODO: lower complex result");
                }
                else
                {
                    sb.AppendLine("return default;");
                }
                break;
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
                sb.AppendLine($"{listVar}.Add(global::System.Text.Encoding.UTF8.GetString(elemStrPtr, elemStrLen));");
                break;
            default:
                sb.AppendLine($"// TODO: lift list element of type {elemType.Kind}");
                sb.AppendLine($"{listVar}.Add(default);");
                break;
        }
    }

    private static void WriteLowerListElement(IndentedStringBuilder sb, WitType elemType, string elemExpr, string baseVar)
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
                sb.AppendLine($"var elemByteLen = global::System.Text.Encoding.UTF8.GetByteCount({elemExpr});");
                sb.AppendLine($"var elemPtr = WitBindgen.Runtime.InteropHelpers.Alloc(elemByteLen, 1);");
                sb.AppendLine($"global::System.Text.Encoding.UTF8.GetBytes({elemExpr}, new Span<byte>((void*)elemPtr, elemByteLen));");
                sb.AppendLine($"*(int*){baseVar} = (int)elemPtr;");
                sb.AppendLine($"*(int*)({baseVar} + 4) = elemByteLen;");
                break;
            default:
                sb.AppendLine($"// TODO: lower list element of type {elemType.Kind}");
                break;
        }
    }

    private static bool NeedsPostReturn(WitType type)
    {
        return type.Kind == WitTypeKind.String || type.Kind == WitTypeKind.List;
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
                var resultType = func.Results[0];
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
