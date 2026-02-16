namespace Wit.Example.Hello;

public static partial class HelloWorld
{
    public static partial string Run()
    {
        var name = Imports.GetName();
        Console.Log($"Hello, {name}!");
        return $"Hello, {name}!";
    }
}
