using System.Buffers.Binary;
using System.Reflection;
using SettingsProbe.Usn;

namespace SettingsProbe.Tests;

public class UsnParsingTests
{
    [Fact]
    public void Parses_a_hand_written_v2_record()
    {
        // One V2 record for "a.txt", written out by hand from the USN_RECORD_V2 layout so the
        // parser isn't only checked against the same builder the other tests use.
        string hex =
            "4800000002000000" +     // RecordLength 72, Major 2, Minor 0
            "1100000000000500" +     // FileReferenceNumber 0x0005000000000011
            "2200000000000300" +     // ParentFileReferenceNumber 0x0003000000000022
            "0010000000000000" +     // Usn 4096
            "0080A3D7E54FD801" +     // TimeStamp (a FILETIME)
            "02010080" +             // Reason DATA_EXTEND | FILE_CREATE | CLOSE
            "00000000" +             // SourceInfo
            "00000000" +             // SecurityId
            "20000000" +             // FileAttributes ARCHIVE
            "0A00" + "3C00" +        // FileNameLength 10, FileNameOffset 60
            "61002E00740078007400" + // "a.txt"
            "0000";                  // padding to 72
        byte[] record = Convert.FromHexString(hex);

        UsnRecord? r = UsnRecordParser.ParseRecord(record);

        Assert.NotNull(r);
        Assert.Equal(2, r!.MajorVersion);
        Assert.Equal(new UInt128(0, 0x0005000000000011), r.FileId);
        Assert.Equal(new UInt128(0, 0x0003000000000022), r.ParentId);
        Assert.Equal(4096, r.Usn);
        Assert.Equal(UsnReason.DataExtend | UsnReason.FileCreate | UsnReason.Close, r.Reason);
        Assert.False(r.IsDirectory);
        Assert.Equal("a.txt", r.FileName);
    }

    [Fact]
    public void Walks_a_buffer_of_several_records_and_returns_next_usn()
    {
        byte[] buffer = CannedRecords.Buffer(999,
            CannedRecords.V2(10, 5, 100, UsnReason.FileCreate, "one.json"),
            CannedRecords.V2(11, 5, 200, UsnReason.DataExtend, "two-with-a-longer-name.ini"),
            CannedRecords.V2(12, 5, 300, UsnReason.FileCreate, "dir", directory: true));

        var (next, records) = UsnRecordParser.ParseBuffer(buffer);

        Assert.Equal(999, next);
        Assert.Equal(new[] { "one.json", "two-with-a-longer-name.ini", "dir" }, records.Select(r => r.FileName));
        Assert.Equal(new long[] { 100, 200, 300 }, records.Select(r => r.Usn));
        Assert.True(records[2].IsDirectory);
    }

    [Fact]
    public void Parses_v3_records_with_128_bit_ids()
    {
        byte[] buffer = CannedRecords.Buffer(0,
            CannedRecords.V3(0x0006_0000_0000_0042, 0, 0x0001_0000_0000_0007, 0, 50, UsnReason.RenameNewName, "settings.json"),
            CannedRecords.V3(1, 0xAB, 2, 0xCD, 60, UsnReason.Close, "refs-style"));

        var (_, records) = UsnRecordParser.ParseBuffer(buffer);

        Assert.Equal(2, records.Count);
        Assert.Equal(3, records[0].MajorVersion);
        // An NTFS V3 ID has the 64-bit reference in its low half, so it compares equal to V2's.
        Assert.Equal(new UInt128(0, 0x0006_0000_0000_0042), records[0].FileId);
        Assert.Equal(new UInt128(0, 0x0001_0000_0000_0007), records[0].ParentId);
        Assert.Equal(new UInt128(0xAB, 1), records[1].FileId);
        Assert.Equal(new UInt128(0xCD, 2), records[1].ParentId);
        Assert.Equal("settings.json", records[0].FileName);
    }

    [Fact]
    public void Skips_unknown_record_versions_by_length()
    {
        byte[] unknown = CannedRecords.V2(1, 1, 1, UsnReason.Close, "v4-ish");
        BinaryPrimitives.WriteUInt16LittleEndian(unknown.AsSpan(4), 4);
        byte[] buffer = CannedRecords.Buffer(0, unknown, CannedRecords.V2(2, 1, 2, UsnReason.Close, "kept"));

        var (_, records) = UsnRecordParser.ParseBuffer(buffer);

        Assert.Equal("kept", Assert.Single(records).FileName);
    }

    [Fact]
    public void A_broken_length_stops_the_walk_and_keeps_what_parsed()
    {
        byte[] good = CannedRecords.V2(1, 1, 1, UsnReason.Close, "good");
        byte[] bad = CannedRecords.V2(2, 1, 2, UsnReason.Close, "bad");
        BinaryPrimitives.WriteUInt32LittleEndian(bad, 10_000);
        byte[] buffer = CannedRecords.Buffer(0, good, bad);

        var (_, records) = UsnRecordParser.ParseBuffer(buffer);

        Assert.Equal("good", Assert.Single(records).FileName);
    }

    [Fact]
    public void A_zero_length_stops_the_walk_instead_of_looping()
    {
        byte[] buffer = CannedRecords.Buffer(0, new byte[64]);
        var (_, records) = UsnRecordParser.ParseBuffer(buffer);
        Assert.Empty(records);
    }

    [Fact]
    public void A_name_running_past_the_record_is_rejected()
    {
        byte[] r = CannedRecords.V2(1, 1, 1, UsnReason.Close, "abc");
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(56), 200);
        Assert.Null(UsnRecordParser.ParseRecord(r));
    }

    [Fact]
    public void A_buffer_shorter_than_the_next_usn_is_empty()
    {
        var (next, records) = UsnRecordParser.ParseBuffer(new byte[4]);
        Assert.Equal(0, next);
        Assert.Empty(records);
    }

    [Fact]
    public void Journal_data_v1_parses()
    {
        byte[] b = new byte[64];
        BinaryPrimitives.WriteUInt64LittleEndian(b, 0x01D9_0000_1234);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(8), 1_000);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(16), 5_000);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(24), 0);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(32), long.MaxValue);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(40), 0x2000000);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(48), 0x800000);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(56), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(58), 4);

        UsnJournalInfo info = UsnJournalInfo.Parse(b);

        Assert.Equal(0x01D9_0000_1234UL, info.JournalId);
        Assert.Equal(1_000, info.FirstUsn);
        Assert.Equal(5_000, info.NextUsn);
        Assert.Equal(0x2000000UL, info.MaximumSize);
        Assert.Equal(0x800000UL, info.AllocationDelta);
        Assert.Equal(4, info.MaxSupportedMajorVersion);
    }

    [Fact]
    public void Read_input_is_read_usn_journal_data_v1()
    {
        byte[] b = UsnJournalReader.BuildReadInput(0x1234, 0xFEED);

        Assert.Equal(48, b.Length);
        Assert.Equal(0x1234, BinaryPrimitives.ReadInt64LittleEndian(b));
        Assert.Equal(0xFFFFFFFFu, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(12)));
        Assert.Equal(0xFEEDUL, BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(32)));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(40)));
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(42)));
    }

    [Fact]
    public void Control_codes_match_winioctl()
    {
        Assert.Equal(0x000900F4u, FsctlCodes.QueryUsnJournal);
        Assert.Equal(0x000900BBu, FsctlCodes.ReadUsnJournal);
        Assert.Equal(0x000900B3u, FsctlCodes.EnumUsnData);
        Assert.Equal(0x000903ABu, FsctlCodes.ReadUnprivilegedUsnJournal);
    }

    [Fact]
    public void Only_read_only_control_codes_exist()
    {
        // FSCTL_CREATE_USN_JOURNAL (0x000900E7) and FSCTL_DELETE_USN_JOURNAL (0x000900F8) change
        // the journal. The tool must never be able to send them.
        string[] fields = typeof(FsctlCodes)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(uint) && !f.IsLiteral)
            .Select(f => f.Name)
            .OrderBy(n => n)
            .ToArray();

        Assert.Equal(new[] { "EnumUsnData", "QueryUsnJournal", "ReadUnprivilegedUsnJournal", "ReadUsnJournal" }, fields);
        uint[] values = typeof(FsctlCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(uint))
            .Select(f => (uint)f.GetValue(null)!)
            .ToArray();
        Assert.DoesNotContain(0x000900E7u, values);
        Assert.DoesNotContain(0x000900F8u, values);
    }
}
