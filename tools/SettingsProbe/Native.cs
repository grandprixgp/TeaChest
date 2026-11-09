using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SettingsProbe;

/// <summary>
/// The only control codes this tool may send. Read-only by design: the codes that create,
/// delete or resize the journal are deliberately absent, and a test checks this list.
/// </summary>
internal static class FsctlCodes
{
    private const uint FileDeviceFileSystem = 0x00000009;
    private const uint MethodBuffered = 0;
    private const uint MethodNeither = 3;
    private const uint FileAnyAccess = 0;

    internal static uint CtlCode(uint deviceType, uint function, uint method, uint access)
        => (deviceType << 16) | (access << 14) | (function << 2) | method;

    public static readonly uint QueryUsnJournal = CtlCode(FileDeviceFileSystem, 61, MethodBuffered, FileAnyAccess);
    public static readonly uint ReadUsnJournal = CtlCode(FileDeviceFileSystem, 46, MethodNeither, FileAnyAccess);
    public static readonly uint EnumUsnData = CtlCode(FileDeviceFileSystem, 44, MethodNeither, FileAnyAccess);
    public static readonly uint ReadUnprivilegedUsnJournal = CtlCode(FileDeviceFileSystem, 234, MethodNeither, FileAnyAccess);
}

internal static class Native
{
    public const uint GenericRead = 0x80000000;
    public const uint FileReadAttributes = 0x80;
    public const uint FileShareAll = 0x1 | 0x2 | 0x4;
    public const uint OpenExisting = 3;
    public const uint FileFlagBackupSemantics = 0x02000000;

    public const int ErrorHandleEof = 38;
    public const int ErrorJournalDeleteInProgress = 1178;
    public const int ErrorJournalNotActive = 1179;
    public const int ErrorJournalEntryDeleted = 1181;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern unsafe bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode, void* inBuffer, int inBufferSize,
        void* outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);

    // FILETIME is two DWORDs, so the struct is 4-byte aligned. Without Pack = 4 the longs below
    // would get padding in front of them and every later field would be read from the wrong place.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int QueryDosDeviceW(string deviceName, char[] targetPath, int max);

    /// <summary>
    /// A Win32 error as an exception, with a message that never includes a path: callers pass a
    /// fixed description of what they were doing.
    /// </summary>
    public static Win32Exception Error(string doing)
    {
        int code = Marshal.GetLastWin32Error();
        return new Win32Exception(code, $"{doing} failed with Win32 error {code}.");
    }
}
