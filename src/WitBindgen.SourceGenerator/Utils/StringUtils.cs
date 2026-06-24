using System.Text.RegularExpressions;

namespace WitBindgen.SourceGenerator;

public class StringUtils
{
    private static readonly Regex SeparatorRegex = new(@"[-.\[\]](.)?", RegexOptions.Compiled);

    /// <summary>
    /// Change the name to a valid C# name.
    /// </summary>
    /// <param name="name">WIT name</param>
    /// <param name="uppercaseFirst">If <c>true</c>, the first character will be uppercased.</param>
    /// <returns>C# name</returns>
    public static string GetName(string name, bool uppercaseFirst = true)
    {
        // Replace ABI prefixes before regex processing
        if (name.StartsWith("[constructor]"))
        {
            name = "ctor-" + name.Substring("[constructor]".Length);
        }
        else if (name.StartsWith("[method]"))
        {
            name = name.Substring("[method]".Length);
        }
        else if (name.StartsWith("[resource-drop]"))
        {
            name = "drop-" + name.Substring("[resource-drop]".Length);
        }

        var result = SeparatorRegex.Replace(name, static m =>
        {
            var g = m.Groups[1];
            return g.Success ? g.Value.ToUpperInvariant() : string.Empty;
        });

        if (string.IsNullOrEmpty(result))
        {
            return "_";
        }

        if (!uppercaseFirst)
        {
            return result;
        }

        return char.ToUpperInvariant(result[0]) + result.Substring(1);
    }

    /// <summary>
    /// C# class name for a resource, disambiguated against its enclosing interface.
    /// A WIT interface may contain a resource of the same name (e.g. <c>interface app
    /// { resource app }</c>); both map to PascalCase <c>App</c>, but a nested class
    /// cannot share its enclosing type's name (CS0542). When they collide we suffix the
    /// resource with "Resource". Non-colliding resources keep their plain name, so this
    /// is a no-op for every interface that doesn't contain a same-named resource.
    /// </summary>
    public static string ResourceClassName(string interfaceCSharpName, string resourceWitName)
    {
        var name = GetName(resourceWitName);
        return name == interfaceCSharpName ? name + "Resource" : name;
    }

    /// <summary>
    /// Extracts the interface name from an import module string
    /// (e.g. "tinyecs:modding/app@0.1.0" -> "app"). Returns the input unchanged if it
    /// carries no "namespace:pkg/interface" shape.
    /// </summary>
    public static string InterfaceNameFromModule(string moduleName)
    {
        var name = moduleName;
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
            name = name.Substring(slash + 1);
        var at = name.IndexOf('@');
        if (at >= 0)
            name = name.Substring(0, at);
        return name;
    }
}
