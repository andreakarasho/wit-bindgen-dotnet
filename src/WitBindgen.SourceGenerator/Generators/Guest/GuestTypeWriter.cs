using WitBindgen.SourceGenerator.Models;

namespace WitBindgen.SourceGenerator.Generators.Guest;

/// <summary>
/// Generates C# data types from WIT type definitions.
/// </summary>
public static class GuestTypeWriter
{
    public static void WriteRecord(IndentedStringBuilder sb, WitRecord record)
    {
        sb.AppendLine($"public struct {record.CSharpName}");
        using (sb.Block())
        {
            foreach (var field in record.Fields)
            {
                sb.AppendLine($"public {CanonicalAbi.WitTypeToCS(field.Type)} {field.CSharpName};");
            }
        }
    }

    public static void WriteEnum(IndentedStringBuilder sb, WitEnum @enum)
    {
        sb.AppendLine($"public enum {@enum.CSharpName}");
        using (sb.Block())
        {
            for (int i = 0; i < @enum.Values.Length; i++)
            {
                var value = @enum.Values[i];
                var comma = i < @enum.Values.Length - 1 ? "," : "";
                sb.AppendLine($"{value.CSharpName} = {i}{comma}");
            }
        }
    }

    public static void WriteFlags(IndentedStringBuilder sb, WitFlags flags)
    {
        sb.AppendLine("[System.Flags]");
        sb.AppendLine($"public enum {flags.CSharpName}");
        using (sb.Block())
        {
            for (int i = 0; i < flags.Values.Length; i++)
            {
                var value = flags.Values[i];
                var comma = i < flags.Values.Length - 1 ? "," : "";
                sb.AppendLine($"{value.CSharpName} = {1 << i}{comma}");
            }
        }
    }

    public static void WriteVariant(IndentedStringBuilder sb, WitVariant variant)
    {
        var name = StringUtils.GetName(variant.Name);

        sb.AppendLine($"[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]");
        sb.AppendLine($"public struct {name}");
        using (sb.Block())
        {
            // Discriminant field
            sb.AppendLine("[System.Runtime.InteropServices.FieldOffset(0)]");
            sb.AppendLine("public Case Discriminant;");
            sb.AppendLine();

            // Payload fields at offset 4 (overlapping)
            foreach (var @case in variant.Cases)
            {
                if (@case.Type is not null)
                {
                    var caseName = StringUtils.GetName(@case.Name);
                    sb.AppendLine("[System.Runtime.InteropServices.FieldOffset(4)]");
                    sb.AppendLine($"public {CanonicalAbi.WitTypeToCS(@case.Type)} {caseName}Payload;");
                }
            }

            sb.AppendLine();

            // Discriminant enum
            sb.AppendLine("public enum Case");
            using (sb.Block())
            {
                for (int i = 0; i < variant.Cases.Length; i++)
                {
                    var @case = variant.Cases[i];
                    var caseName = StringUtils.GetName(@case.Name);
                    var comma = i < variant.Cases.Length - 1 ? "," : "";
                    sb.AppendLine($"{caseName} = {i}{comma}");
                }
            }

            sb.AppendLine();

            // Factory methods
            foreach (var @case in variant.Cases)
            {
                var caseName = StringUtils.GetName(@case.Name);
                if (@case.Type is not null)
                {
                    var csType = CanonicalAbi.WitTypeToCS(@case.Type);
                    sb.AppendLine($"public static {name} Create{caseName}({csType} val) => new() {{ Discriminant = Case.{caseName}, {caseName}Payload = val }};");
                }
                else
                {
                    sb.AppendLine($"public static {name} Create{caseName}() => new() {{ Discriminant = Case.{caseName} }};");
                }
            }
        }
    }

    /// <summary>
    /// Writes all type definitions from a WitTypeDefinitions collection.
    /// </summary>
    public static void WriteAllTypes(IndentedStringBuilder sb, WitTypeDefinitions definitions, string? moduleName = null)
    {
        foreach (var item in definitions.Items)
        {
            switch (item)
            {
                case WitRecord record:
                    WriteRecord(sb, record);
                    sb.AppendLine();
                    break;
                case WitEnum @enum:
                    WriteEnum(sb, @enum);
                    sb.AppendLine();
                    break;
                case WitFlags flags:
                    WriteFlags(sb, flags);
                    sb.AppendLine();
                    break;
                case WitVariant variant:
                    WriteVariant(sb, variant);
                    sb.AppendLine();
                    break;
                case WitInterface interf:
                    WriteInterface(sb, interf);
                    sb.AppendLine();
                    break;
                case WitResource resource:
                    if (moduleName != null)
                    {
                        GuestImportWriter.WriteImportResource(sb, moduleName, resource);
                    }
                    sb.AppendLine();
                    break;
            }
        }
    }

    private static void WriteInterface(IndentedStringBuilder sb, WitInterface interf)
    {
        sb.AppendLine($"public static partial class {interf.CSharpName}");
        using (sb.Block())
        {
            WriteAllTypes(sb, interf.Definitions);
        }
    }
}
