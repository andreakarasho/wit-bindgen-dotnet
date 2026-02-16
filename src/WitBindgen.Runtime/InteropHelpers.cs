using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WitBindgen.Runtime;

/// <summary>
/// Runtime helpers for guest WASM modules using the Component Model canonical ABI.
/// </summary>
public static unsafe class InteropHelpers
{
    // Static return area for multi-value returns (32 bytes should be enough for most cases)
    private static readonly byte[] ReturnAreaBuffer = new byte[32];

    /// <summary>
    /// The canonical ABI realloc function, exported for the host to allocate memory in the guest.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "cabi_realloc")]
    public static nint CabiRealloc(nint oldPtr, int oldSize, int align, int newSize)
    {
        // For new allocations (oldPtr == 0), just allocate
        // For reallocations, allocate new and copy
        var newPtr = NativeMemory.AlignedAlloc((nuint)newSize, (nuint)align);

        if (oldPtr != 0 && oldSize > 0)
        {
            var copySize = Math.Min(oldSize, newSize);
            Buffer.MemoryCopy((void*)oldPtr, newPtr, newSize, copySize);
            NativeMemory.AlignedFree((void*)oldPtr);
        }

        return (nint)newPtr;
    }

    /// <summary>
    /// Allocates memory with the specified size and alignment using the canonical ABI.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nint Alloc(int size, int align)
    {
        return (nint)NativeMemory.AlignedAlloc((nuint)size, (nuint)align);
    }

    /// <summary>
    /// Frees memory previously allocated via Alloc or cabi_realloc.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Free(void* ptr, int size, int align)
    {
        NativeMemory.AlignedFree(ptr);
    }

    /// <summary>
    /// Gets a byte pointer from a Span, for passing stack/pooled buffers to wasm imports.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte* SpanToPointer(Span<byte> span)
        => (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(span));

    /// <summary>
    /// Gets a pointer to the static return area used for multi-value returns.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nint GetReturnArea()
    {
        return (nint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(ReturnAreaBuffer));
    }
}
