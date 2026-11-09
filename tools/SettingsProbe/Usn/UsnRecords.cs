using System.Buffers.Binary;
using System.Text;

namespace SettingsProbe.Usn;

/// <summary>The reason flags this tool cares about, from winioctl.h.</summary>
[Flags]
internal enum UsnReason : uint
{
    None = 0,
    DataOverwrite = 0x00000001,
    DataExtend = 0x00000002,
    DataTruncation = 0x00000004,
    NamedDataOverwrite = 0x00000010,
    NamedDataExtend = 0x00000020,
    NamedDataTruncation = 0x00000040,
    FileCreate = 0x00000100,
    FileDelete = 0x00000200,
    EaChange = 0x00000400,
    SecurityChange = 0x00000800,
    RenameOldName = 0x00001000,
    RenameNewName = 0x00002000,
    IndexableChange = 0x00004000,
    BasicInfoChange = 0x00008000,
    HardLinkChange = 0x00010000,
    CompressionChange = 0x00020000,
    EncryptionChange = 0x00040000,
    ObjectIdChange = 0x00080000,
    ReparsePointChange = 0x00100000,
    StreamChange = 0x00200000,
    TransactedChange = 0x00400000,
    IntegrityChange = 0x00800000,
    Close = 0x80000000,

    AnyWrite = DataOverwrite | DataExtend | DataTruncation | NamedDataOverwrite | NamedDataExtend | NamedDataTruncation
}

/// <summary>
/// One journal record, V2 or V3. File IDs are kept as 128-bit values so both shapes compare the
/// same way: a V2 record's 64-bit reference sits in the low half, which is also where NTFS puts
/// it in a V3 record.
/// </summary>
internal sealed record UsnRecord(
    ushort MajorVersion,
    UInt128 FileId,
    UInt128 ParentId,
    long Usn,
    DateTime TimeStampUtc,
    UsnReason Reason,
    uint SourceInfo,
    uint FileAttributes,
    string FileName)
{
    public const uint DirectoryAttribute = 0x10;

    public bool IsDirectory => (FileAttributes & DirectoryAttribute) != 0;
}

internal static class UsnRecordParser
{
    private const int V2NameOffsetField = 58;
    private const int V3NameOffsetField = 74;

    /// <summary>
    /// Parses the output buffer of FSCTL_READ_USN_JOURNAL (or FSCTL_ENUM_USN_DATA): an 8-byte
    /// "next" value, then records back to back, each starting with its own length. Unknown record
    /// versions are skipped by length. A broken length stops the walk and returns what parsed,
    /// rather than throwing, since a half-read buffer is still useful.
    /// </summary>
    internal static (long Next, List<UsnRecord> Records) ParseBuffer(ReadOnlySpan<byte> buffer)
    {
        var records = new List<UsnRecord>();
        if (buffer.Length < 8) return (0, records);

        long next = BinaryPrimitives.ReadInt64LittleEndian(buffer);
        int offset = 8;
        while (offset + 8 <= buffer.Length)
        {
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[offset..]);
            if (length < 8 || offset + length > buffer.Length) break;

            UsnRecord? record = ParseRecord(buffer.Slice(offset, length));
            if (record != null) records.Add(record);
            offset += length;
        }
        return (next, records);
    }

    internal static UsnRecord? ParseRecord(ReadOnlySpan<byte> r)
    {
        if (r.Length < 8) return null;
        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(r[4..]);
        return major switch
        {
            2 => ParseV2(r),
            3 => ParseV3(r),
            _ => null
        };
    }

    private static UsnRecord? ParseV2(ReadOnlySpan<byte> r)
    {
        if (r.Length < 60) return null;
        string? name = ReadName(r, V2NameOffsetField);
        if (name == null) return null;
        return new UsnRecord(
            2,
            new UInt128(0, BinaryPrimitives.ReadUInt64LittleEndian(r[8..])),
            new UInt128(0, BinaryPrimitives.ReadUInt64LittleEndian(r[16..])),
            BinaryPrimitives.ReadInt64LittleEndian(r[24..]),
            FromFileTime(BinaryPrimitives.ReadInt64LittleEndian(r[32..])),
            (UsnReason)BinaryPrimitives.ReadUInt32LittleEndian(r[40..]),
            BinaryPrimitives.ReadUInt32LittleEndian(r[44..]),
            BinaryPrimitives.ReadUInt32LittleEndian(r[52..]),
            name);
    }

    private static UsnRecord? ParseV3(ReadOnlySpan<byte> r)
    {
        if (r.Length < 76) return null;
        string? name = ReadName(r, V3NameOffsetField);
        if (name == null) return null;
        return new UsnRecord(
            3,
            ReadId128(r[8..]),
            ReadId128(r[24..]),
            BinaryPrimitives.ReadInt64LittleEndian(r[40..]),
            FromFileTime(BinaryPrimitives.ReadInt64LittleEndian(r[48..])),
            (UsnReason)BinaryPrimitives.ReadUInt32LittleEndian(r[56..]),
            BinaryPrimitives.ReadUInt32LittleEndian(r[60..]),
            BinaryPrimitives.ReadUInt32LittleEndian(r[68..]),
            name);
    }

    // FileNameLength sits two bytes before FileNameOffset in both shapes. The name isn't
    // NUL-terminated, so its length is the only safe bound.
    private static string? ReadName(ReadOnlySpan<byte> r, int offsetField)
    {
        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(r[(offsetField - 2)..]);
        int nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(r[offsetField..]);
        if (nameLength % 2 != 0 || nameOffset + nameLength > r.Length) return null;
        return Encoding.Unicode.GetString(r.Slice(nameOffset, nameLength));
    }

    private static UInt128 ReadId128(ReadOnlySpan<byte> r)
        => new(BinaryPrimitives.ReadUInt64LittleEndian(r[8..]), BinaryPrimitives.ReadUInt64LittleEndian(r));

    private static DateTime FromFileTime(long fileTime)
        => fileTime <= 0 ? DateTime.MinValue : DateTime.FromFileTimeUtc(fileTime);
}

/// <summary>The fields of USN_JOURNAL_DATA_V1 this tool reports. Sizes only, never names.</summary>
internal sealed record UsnJournalInfo(
    ulong JournalId,
    long FirstUsn,
    long NextUsn,
    long LowestValidUsn,
    long MaxUsn,
    ulong MaximumSize,
    ulong AllocationDelta,
    ushort MinSupportedMajorVersion,
    ushort MaxSupportedMajorVersion)
{
    internal static UsnJournalInfo Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < 56) throw new ArgumentException("USN_JOURNAL_DATA is too short.", nameof(b));
        return new UsnJournalInfo(
            BinaryPrimitives.ReadUInt64LittleEndian(b),
            BinaryPrimitives.ReadInt64LittleEndian(b[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(b[16..]),
            BinaryPrimitives.ReadInt64LittleEndian(b[24..]),
            BinaryPrimitives.ReadInt64LittleEndian(b[32..]),
            BinaryPrimitives.ReadUInt64LittleEndian(b[40..]),
            BinaryPrimitives.ReadUInt64LittleEndian(b[48..]),
            b.Length >= 60 ? BinaryPrimitives.ReadUInt16LittleEndian(b[56..]) : (ushort)2,
            b.Length >= 60 ? BinaryPrimitives.ReadUInt16LittleEndian(b[58..]) : (ushort)2);
    }
}
