using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SettingsProbe.Usn;

/// <summary>
/// Reads one volume's change journal. Read-only: it opens the volume for reading and sends only
/// FSCTL_QUERY_USN_JOURNAL and FSCTL_READ_USN_JOURNAL (and, as an experiment, the unprivileged
/// read). Opening the volume needs an elevated token.
/// </summary>
internal sealed class UsnJournalReader : IDisposable
{
    private readonly SafeFileHandle _volume;

    private UsnJournalReader(SafeFileHandle volume)
    {
        _volume = volume;
    }

    /// <param name="driveLetter">For example 'C'.</param>
    public static UsnJournalReader Open(char driveLetter)
    {
        SafeFileHandle handle = Native.CreateFileW(
            $@"\\.\{driveLetter}:", Native.GenericRead, Native.FileShareAll, IntPtr.Zero,
            Native.OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw Native.Error("Opening the volume");
        return new UsnJournalReader(handle);
    }

    /// <summary>
    /// A handle to an ordinary folder rather than the volume, opened with only the right to read
    /// attributes, as an unelevated process could. Used to try the unprivileged read.
    /// </summary>
    public static UsnJournalReader OpenOnFolder(string directory)
    {
        SafeFileHandle handle = Native.CreateFileW(
            directory, Native.FileReadAttributes, Native.FileShareAll, IntPtr.Zero,
            Native.OpenExisting, Native.FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) throw Native.Error("Opening the scratch folder");
        return new UsnJournalReader(handle);
    }

    public unsafe UsnJournalInfo Query()
    {
        byte[] output = new byte[64];
        fixed (byte* o = output)
        {
            if (!Native.DeviceIoControl(_volume, FsctlCodes.QueryUsnJournal, null, 0, o, output.Length, out int returned, IntPtr.Zero))
            {
                throw Native.Error("FSCTL_QUERY_USN_JOURNAL");
            }
            return UsnJournalInfo.Parse(output.AsSpan(0, returned));
        }
    }

    /// <summary>
    /// Every record from <paramref name="startUsn"/> up to (not including) <paramref name="endUsn"/>,
    /// handed to <paramref name="onRecord"/> one at a time and never stored here. Records come in
    /// USN order, which ScopeFilter relies on.
    /// </summary>
    public int Read(ulong journalId, long startUsn, long endUsn, Action<UsnRecord> onRecord, bool unprivileged = false, bool stopAfterFirst = false)
    {
        byte[] input = BuildReadInput(startUsn, journalId);
        byte[] output = new byte[64 * 1024];
        int count = 0;
        long next = startUsn;

        while (next < endUsn)
        {
            BinaryPrimitives.WriteInt64LittleEndian(input, next);
            int returned = Call(unprivileged ? FsctlCodes.ReadUnprivilegedUsnJournal : FsctlCodes.ReadUsnJournal, input, output);
            if (returned <= 8) break;

            (long after, List<UsnRecord> records) = UsnRecordParser.ParseBuffer(output.AsSpan(0, returned));
            foreach (UsnRecord record in records)
            {
                if (record.Usn >= endUsn) return count;
                onRecord(record);
                count++;
                if (stopAfterFirst) return count;
            }
            if (after <= next) break;
            next = after;
        }
        return count;
    }

    private unsafe int Call(uint code, byte[] input, byte[] output)
    {
        fixed (byte* i = input)
        fixed (byte* o = output)
        {
            if (Native.DeviceIoControl(_volume, code, i, input.Length, o, output.Length, out int returned, IntPtr.Zero))
            {
                return returned;
            }
            int error = Marshal.GetLastWin32Error();
            if (error == Native.ErrorHandleEof) return 0;
            if (error == Native.ErrorJournalEntryDeleted)
            {
                throw new Win32Exception(error, "The journal wrapped past the start point, so some history is gone.");
            }
            throw new Win32Exception(error, $"Reading the journal failed with Win32 error {error}.");
        }
    }

    /// <summary>READ_USN_JOURNAL_DATA_V1, asking for every reason and for V2 or V3 records.</summary>
    internal static byte[] BuildReadInput(long startUsn, ulong journalId)
    {
        byte[] b = new byte[48];
        BinaryPrimitives.WriteInt64LittleEndian(b, startUsn);                 // StartUsn
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 0xFFFFFFFF);    // ReasonMask
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), 0);            // ReturnOnlyOnClose
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), 0);            // Timeout
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(24), 0);            // BytesToWaitFor
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(32), journalId);    // UsnJournalID
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(40), 2);            // MinMajorVersion
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(42), 3);            // MaxMajorVersion
        return b;
    }

    /// <summary>The 64-bit file reference of a folder, as V2 records show it.</summary>
    public static UInt128 FileIdOf(string directory)
    {
        using SafeFileHandle handle = Native.CreateFileW(
            directory, Native.FileReadAttributes, Native.FileShareAll, IntPtr.Zero,
            Native.OpenExisting, Native.FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) throw Native.Error("Opening the scratch folder");
        if (!Native.GetFileInformationByHandle(handle, out var info)) throw Native.Error("Reading the scratch folder's ID");
        return new UInt128(0, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
    }

    public void Dispose() => _volume.Dispose();
}
