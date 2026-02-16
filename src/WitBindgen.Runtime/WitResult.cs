using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace WitBindgen.Runtime;

/// <summary>
/// Represents a WIT result type, which is a discriminated union of an ok value or an error value.
/// </summary>
/// <typeparam name="TOk">The type of the ok value.</typeparam>
/// <typeparam name="TErr">The type of the error value.</typeparam>
public readonly struct WitResult<TOk, TErr>
{
    private readonly byte _discriminant;
    private readonly TOk _ok;
    private readonly TErr _err;

    private WitResult(byte discriminant, TOk ok, TErr err)
    {
        _discriminant = discriminant;
        _ok = ok;
        _err = err;
    }

    /// <summary>
    /// Gets whether this result is an ok value.
    /// </summary>
    public bool IsOk
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _discriminant == 0;
    }

    /// <summary>
    /// Gets whether this result is an error value.
    /// </summary>
    public bool IsErr
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _discriminant == 1;
    }

    /// <summary>
    /// Gets the ok value. Throws if this is an error result.
    /// </summary>
    public TOk Ok
    {
        get
        {
            if (!IsOk)
                ThrowInvalidState("ok");
            return _ok;
        }
    }

    /// <summary>
    /// Gets the error value. Throws if this is an ok result.
    /// </summary>
    public TErr Err
    {
        get
        {
            if (!IsErr)
                ThrowInvalidState("err");
            return _err;
        }
    }

    /// <summary>
    /// Tries to get the ok value.
    /// </summary>
    public bool TryGetOk([MaybeNullWhen(false)] out TOk value)
    {
        if (IsOk)
        {
            value = _ok;
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>
    /// Tries to get the error value.
    /// </summary>
    public bool TryGetErr([MaybeNullWhen(false)] out TErr value)
    {
        if (IsErr)
        {
            value = _err;
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>
    /// Creates an ok result.
    /// </summary>
    public static WitResult<TOk, TErr> FromOk(TOk value)
    {
        return new WitResult<TOk, TErr>(0, value, default!);
    }

    /// <summary>
    /// Creates an error result.
    /// </summary>
    public static WitResult<TOk, TErr> FromErr(TErr value)
    {
        return new WitResult<TOk, TErr>(1, default!, value);
    }

    [DoesNotReturn]
    private static void ThrowInvalidState(string expected)
    {
        throw new InvalidOperationException($"Cannot access '{expected}' on a WitResult that is not in that state.");
    }
}
