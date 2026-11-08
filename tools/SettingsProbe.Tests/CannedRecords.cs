using System.Buffers.Binary;
using System.Text;
using SettingsProbe.Usn;

namespace SettingsProbe.Tests;

/// <summary>Builds journal records byte for byte from the documented layouts.</summary>
internal static class CannedRecords
{
    public static readonly DateTime Time = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    public static byte[] V2(ulong file, ulong parent, long usn, UsnReason reason, string name, bool directory = false)
    {
        byte[] nameBytes = Encoding.Unicode.GetBytes(name);
        int length = Align8(60 + nameBytes.Length);
        byte[] r = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(r, (uint)length);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(6), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(8), file);
        BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(16), parent);
        BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(24), usn);
        BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(32), Time.ToFileTimeUtc());
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(40), (uint)reason);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(52), directory ? 0x10u : 0x20u);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(56), (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(58), 60);
        nameBytes.CopyTo(r, 60);
        return r;
    }

    public static byte[] V3(ulong fileLow, ulong fileHigh, ulong parentLow, ulong parentHigh, long usn, UsnReason reason, string name)
    {
        byte[] nameBytes = Encoding.Unicode.GetBytes(name);
        int length = Align8(76 + nameBytes.Length);
        byte[] r = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(r, (uint)length);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(8), fileLow);
        BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(16), fileHigh);
        BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(24), parentLow);
        BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(32), parentHigh);
        BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(40), usn);
        BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(48), Time.ToFileTimeUtc());
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(56), (uint)reason);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(68), 0x20);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(72), (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(74), 76);
        nameBytes.CopyTo(r, 76);
        return r;
    }

    public static byte[] Buffer(long next, params byte[][] records)
    {
        var all = new List<byte>(new byte[8]);
        BinaryPrimitives.WriteInt64LittleEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(all), next);
        foreach (byte[] r in records) all.AddRange(r);
        return all.ToArray();
    }

    public static UsnRecord Record(ulong file, ulong parent, UsnReason reason, string name, bool directory = false, long usn = 0)
        => new(2, new UInt128(0, file), new UInt128(0, parent), usn, Time, reason, 0, directory ? 0x10u : 0x20u, name);

    private static int Align8(int n) => (n + 7) & ~7;
}
