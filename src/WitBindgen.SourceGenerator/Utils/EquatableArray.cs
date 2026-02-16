using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace WitBindgen.SourceGenerator;

/// <summary>
/// An immutable, equatable array. This is equivalent to <see cref="ImmutableArray"/> but with value equality support.
/// </summary>
/// <typeparam name="T">The type of values in the array.</typeparam>
[ExcludeFromCodeCoverage]
public readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
{
    private readonly T[]? array;

    public EquatableArray(T[] array)
    {
        this.array = array;
    }

    public EquatableArray(ImmutableArray<T> array)
    {
        this.array = Unsafe.As<ImmutableArray<T>, T[]?>(ref array);
    }

    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => AsImmutableArray().IsEmpty;
    }

    public int Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => AsImmutableArray().Length;
    }

    public ref readonly T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref AsImmutableArray().ItemRef(index);
    }

    public static implicit operator EquatableArray<T>(T[] array)
    {
        return new(array);
    }

    public static implicit operator EquatableArray<T>(ImmutableArray<T> array)
    {
        return FromImmutableArray(array);
    }

    public static implicit operator ImmutableArray<T>(EquatableArray<T> array)
    {
        return array.AsImmutableArray();
    }

    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right)
    {
        return !left.Equals(right);
    }

    public static EquatableArray<T> FromImmutableArray(ImmutableArray<T> array)
    {
        return new(array);
    }

    /// <inheritdoc/>
    public bool Equals(EquatableArray<T> array)
    {
        var left = this.array.AsSpan();
        var right = array.array.AsSpan();

        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            var leftItem = left[i];
            var rightItem = right[i];

            if (leftItem is IEquatable<T> equatable)
            {
                if (!equatable.Equals(rightItem))
                {
                    return false;
                }
            }
            else if (leftItem is null)
            {
                if (rightItem is not null)
                {
                    return false;
                }
            }
            else if (!leftItem.Equals(rightItem))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return obj is EquatableArray<T> array && Equals(this, array);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        if (this.array is not T[] array)
        {
            return 0;
        }

        HashCode hashCode = default;

        foreach (T item in array)
        {
            hashCode.Add(item);
        }

        return hashCode.ToHashCode();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ImmutableArray<T> AsImmutableArray()
    {
        return Unsafe.As<T[]?, ImmutableArray<T>>(ref Unsafe.AsRef(in array));
    }

    public ReadOnlySpan<T> AsSpan()
    {
        return AsImmutableArray().AsSpan();
    }

    public T[] ToArray()
    {
        return AsImmutableArray().ToArray();
    }

    public T[] GetUnsafeArray()
    {
        return array ?? [];
    }

    public ImmutableArray<T>.Enumerator GetEnumerator()
    {
        return AsImmutableArray().GetEnumerator();
    }

    /// <inheritdoc/>
    IEnumerator<T> IEnumerable<T>.GetEnumerator()
    {
        return ((IEnumerable<T>)AsImmutableArray()).GetEnumerator();
    }

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator()
    {
        return ((IEnumerable)AsImmutableArray()).GetEnumerator();
    }
}

[ExcludeFromCodeCoverage]
internal static class EquatableArray
{
    public static EquatableArray<T> Combine<T>(EquatableArray<T> left, EquatableArray<T> right)
    {
        var result = new T[left.Length + right.Length];
        left.AsSpan().CopyTo(result);
        right.AsSpan().CopyTo(result.AsSpan(left.Length));
        return new EquatableArray<T>(result);
    }

    public static EquatableArray<T> Combine<T>(EquatableArray<T> left, T right)
    {
        var result = new T[left.Length + 1];
        left.AsSpan().CopyTo(result);
        result[^1] = right;
        return new EquatableArray<T>(result);
    }

    public static EquatableArray<T> AsEquatableArray<T>(this ImmutableArray<T> array)
        where T : IEquatable<T>
    {
        return new(array);
    }

    public static EquatableArray<T> FromEnumerable<T>(IEnumerable<T> enumerable)
        where T : IEquatable<T>
    {
        return new(enumerable.ToImmutableArray());
    }
}
