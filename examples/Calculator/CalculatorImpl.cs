namespace Wit.Example.Calculator;


public static partial class Calculator
{
    public static partial double Add(double a, double b)
    {
        Logger.Log((uint)Types.TraceLevel.Info, $"Adding {a} + {b}");
        return a + b;
    }

    public static partial double Calculate(uint op, double a, double b)
    {
        var precision = Imports.GetPrecision();
        Logger.Log((uint)Types.TraceLevel.Info, $"Calculate op={op} a={a} b={b} precision={precision}");

        double result = op switch
        {
            0 => a + b,
            1 => a - b,
            2 => a * b,
            3 => b != 0 ? a / b : 0,
            _ => 0,
        };

        return Math.Round(result, (int)precision);
    }
}
