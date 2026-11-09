using System.Buffers.Binary;
using System.Text;

namespace SettingsProbe.Etw;

/// <summary>
/// Payload layouts for the Microsoft-Windows-Kernel-File events this tool reads, taken from the
/// provider's manifest. Version 1 is what Windows 10 and 11 send; version 0 is kept for older
/// builds. Pointers are 4 or 8 bytes depending on the event header's flags.
/// </summary>
internal static class KernelFileEvents
{
    public static readonly Guid ProviderId = new("edd08927-9cc4-4e65-b970-c2560fb5c289");

    public const ushort Create = 12;
    public const ushort Write = 16;
    public const ushort DeletePath = 26;
    public const ushort RenamePath = 27;
    public const ushort CreateNewFile = 30;

    public const ulong KeywordCreate = 0x80;
    public const ulong KeywordWrite = 0x200;
    public const ulong KeywordDeletePath = 0x400;
    public const ulong KeywordRenamePath = 0x800;
    public const ulong KeywordCreateNewFile = 0x1000;

    public const ulong Keywords = KeywordCreate | KeywordWrite | KeywordDeletePath | KeywordRenamePath | KeywordCreateNewFile;

    /// <summary>Create and CreateNewFile: the file object and the NT path being opened.</summary>
    internal static (ulong FileObject, uint Disposition, string FileName)? ParseCreate(ReadOnlySpan<byte> p, int version, int pointerSize)
    {
        // v1: Irp, FileObject, IssuingThreadId(u32), CreateOptions, CreateAttributes, ShareAccess, FileName
        // v0: Irp, ThreadId(ptr), FileObject, CreateOptions, CreateAttributes, ShareAccess, FileName
        int fileObjectAt = version >= 1 ? pointerSize : 2 * pointerSize;
        int optionsAt = version >= 1 ? 2 * pointerSize + 4 : 3 * pointerSize;
        int nameAt = optionsAt + 12;
        if (p.Length < nameAt) return null;
        ulong fileObject = ReadPointer(p[fileObjectAt..], pointerSize);
        uint options = BinaryPrimitives.ReadUInt32LittleEndian(p[optionsAt..]);
        return (fileObject, options >> 24, ReadUnicodeZ(p[nameAt..]));
    }

    /// <summary>Write: only the file object, matched against an earlier Create for the name.</summary>
    internal static (ulong FileObject, uint IoSize)? ParseWrite(ReadOnlySpan<byte> p, int version, int pointerSize)
    {
        // v1: ByteOffset(u64), Irp, FileObject, FileKey, IssuingThreadId, IOSize, IOFlags, ExtraFlags
        // v0: ByteOffset(u64), Irp, ThreadId(ptr), FileObject, FileKey, IOSize, IOFlags
        int fileObjectAt = 8 + (version >= 1 ? pointerSize : 2 * pointerSize);
        int sizeAt = version >= 1 ? 8 + 3 * pointerSize + 4 : 8 + 4 * pointerSize;
        if (p.Length < sizeAt + 4) return null;
        return (ReadPointer(p[fileObjectAt..], pointerSize), BinaryPrimitives.ReadUInt32LittleEndian(p[sizeAt..]));
    }

    /// <summary>DeletePath and RenamePath: the path is in the event itself.</summary>
    internal static string? ParsePathEvent(ReadOnlySpan<byte> p, int version, int pointerSize)
    {
        // v1: Irp, FileObject, FileKey, ExtraInformation, IssuingThreadId(u32), InfoClass(u32), FilePath
        // v0: Irp, ThreadId, FileObject, FileKey, ExtraInformation, InfoClass(u32), FilePath
        int nameAt = version >= 1 ? 4 * pointerSize + 8 : 5 * pointerSize + 4;
        if (p.Length < nameAt) return null;
        return ReadUnicodeZ(p[nameAt..]);
    }

    private static ulong ReadPointer(ReadOnlySpan<byte> p, int size)
        => size == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(p) : BinaryPrimitives.ReadUInt32LittleEndian(p);

    internal static string ReadUnicodeZ(ReadOnlySpan<byte> p)
    {
        int length = 0;
        while (length + 1 < p.Length && (p[length] != 0 || p[length + 1] != 0)) length += 2;
        return Encoding.Unicode.GetString(p[..length]);
    }
}

/// <summary>
/// Turns NT device paths (\Device\HarddiskVolume3\...) into drive paths, and decides whether a
/// path is under the scratch folder. Everything outside it is only ever counted.
/// </summary>
internal sealed class NtPathScope
{
    private readonly string _ntRoot;

    /// <param name="deviceName">What QueryDosDevice returns for the drive, e.g. \Device\HarddiskVolume3.</param>
    /// <param name="scopeFullPath">The scratch folder as a drive path, e.g. C:\x\.probe-scratch.</param>
    public NtPathScope(string deviceName, string scopeFullPath)
    {
        string trimmed = scopeFullPath.TrimEnd('\\');
        if (trimmed.Length < 3 || trimmed[1] != ':') throw new ArgumentException("Expected a drive path.", nameof(scopeFullPath));
        _ntRoot = deviceName.TrimEnd('\\') + trimmed[2..];
    }

    /// <summary>The path relative to the scope, or null if it's outside (or is the scope itself).</summary>
    public string? Relative(string ntPath)
    {
        if (ntPath.Length <= _ntRoot.Length + 1) return null;
        if (!ntPath.StartsWith(_ntRoot, StringComparison.OrdinalIgnoreCase)) return null;
        if (ntPath[_ntRoot.Length] != '\\') return null;
        string rest = ntPath[(_ntRoot.Length + 1)..];
        // A stream name (file:stream) or a trailing slash is still the same file for our purposes.
        return rest.TrimEnd('\\');
    }
}
