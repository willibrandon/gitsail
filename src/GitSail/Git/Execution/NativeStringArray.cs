using System.Runtime.InteropServices;

namespace GitSail.Git.Execution;

/// <summary>
/// Owns one unmanaged NUL-terminated vector of NUL-terminated native byte strings.
/// </summary>
internal sealed unsafe class NativeStringArray : IDisposable
{
    private byte* _block;

    private NativeStringArray(byte* block)
    {
        _block = block;
    }

    /// <summary>
    /// Gets the native pointer vector while this owner remains undisposed.
    /// </summary>
    internal byte** Pointer
        => (byte**)_block;

    /// <summary>
    /// Allocates one native vector and copies every supplied exact byte string.
    /// </summary>
    /// <param name="values">The non-NUL native byte strings.</param>
    /// <returns>The owner of the allocated vector and string storage.</returns>
    internal static NativeStringArray Create(IReadOnlyList<byte[]> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var dataBytes = 0;
        foreach (var value in values)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.AsSpan().Contains((byte)0))
            {
                throw new ArgumentException("A native string cannot contain NUL.", nameof(values));
            }

            dataBytes = checked(dataBytes + value.Length + 1);
        }

        var pointerBytes = checked((nuint)(values.Count + 1) * (nuint)sizeof(byte*));
        var blockBytes = checked(pointerBytes + (nuint)dataBytes);
        var block = (byte*)NativeMemory.AllocZeroed(blockBytes);
        if (block is null)
        {
            throw new OutOfMemoryException();
        }

        var pointers = new Span<nint>(block, values.Count + 1);
        var data = block + pointerBytes;
        var offset = 0;
        for (var index = 0; index < values.Count; index++)
        {
            pointers[index] = (nint)(data + offset);
            values[index].CopyTo(new Span<byte>(data + offset, values[index].Length));
            offset = checked(offset + values[index].Length + 1);
        }

        return new NativeStringArray(block);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        var block = _block;
        _block = null;
        NativeMemory.Free(block);
    }
}
