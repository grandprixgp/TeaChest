using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SettingsProbe.Etw;

/// <summary>One file event from the watched process, already reduced to what we may keep.</summary>
internal sealed record FileEvent(ushort EventId, uint ProcessId, ulong FileObject, string? NtPath, uint IoSize);

/// <summary>
/// A real-time ETW session on Microsoft-Windows-Kernel-File, filtered to one process.
///
/// An ETW session outlives the process that starts it, so this class is careful: it uses a fixed
/// name, stops any leftover session with that name before starting, and stops its own in Dispose.
/// The callback only keeps events from the target process ID; everything else is counted and
/// dropped on the spot.
/// </summary>
internal sealed unsafe class KernelFileSession : IDisposable
{
    public const string SessionName = "TeaChest-SettingsProbe";

    private const uint WnodeFlagTracedGuid = 0x00020000;
    private const uint EventTraceRealTimeMode = 0x00000100;
    private const uint EventTraceControlStop = 1;
    private const uint EventControlCodeEnableProvider = 1;
    private const uint ProcessTraceModeRealTime = 0x00000100;
    private const uint ProcessTraceModeEventRecord = 0x10000000;
    private const uint EventFilterTypePid = 0x80000004;
    private const ushort EventHeaderFlag32BitHeader = 0x0020;
    private const int ErrorAlreadyExists = 183;
    private const int PropertiesSize = 120;
    private const int NameBytes = 1024;
    private static readonly ulong InvalidProcessTraceHandle = ulong.MaxValue;

    private readonly uint _targetPid;
    private readonly List<FileEvent> _events = new();
    private readonly object _gate = new();
    private readonly EventRecordCallback _callback;
    private ulong _sessionHandle;
    private ulong _traceHandle = InvalidProcessTraceHandle;
    private Thread? _pump;
    private bool _stopped;

    public long OtherProcessEvents;
    public long TargetEvents;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void EventRecordCallback(IntPtr eventRecord);

    private KernelFileSession(uint targetPid)
    {
        _targetPid = targetPid;
        _callback = OnEvent;
    }

    public bool PidFilterAccepted { get; private set; }

    /// <summary>Starts the session and begins pumping events for <paramref name="targetPid"/>.</summary>
    public static KernelFileSession Start(uint targetPid)
    {
        var session = new KernelFileSession(targetPid);
        try
        {
            session.StartSession();
            session.EnableProvider();
            session.StartPump();
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>Stops a session with our name if one was left behind. True if there was one.</summary>
    public static bool StopLeftover()
    {
        IntPtr props = AllocProperties();
        try
        {
            return ControlTraceW(0, SessionName, props, EventTraceControlStop) == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(props);
        }
    }

    private void StartSession()
    {
        StopLeftover();
        IntPtr props = AllocProperties();
        try
        {
            int status = StartTraceW(out _sessionHandle, SessionName, props);
            if (status == ErrorAlreadyExists)
            {
                StopLeftover();
                status = StartTraceW(out _sessionHandle, SessionName, props);
            }
            if (status != 0) throw new Win32Exception(status, $"Starting the ETW session failed with Win32 error {status}.");
        }
        finally
        {
            Marshal.FreeHGlobal(props);
        }
    }

    private void EnableProvider()
    {
        Guid provider = KernelFileEvents.ProviderId;

        // Ask ETW to filter by process ID first. If the provider doesn't honour it, OnEvent still
        // drops everything from other processes, and the counters show which happened.
        uint pid = _targetPid;
        var filter = new EventFilterDescriptor { Ptr = (ulong)(&pid), Size = sizeof(uint), Type = EventFilterTypePid };
        var parameters = new EnableTraceParameters
        {
            Version = 2,
            EnableFilterDesc = (IntPtr)(&filter),
            FilterDescCount = 1
        };

        int status = EnableTraceEx2(_sessionHandle, ref provider, EventControlCodeEnableProvider, 5,
            KernelFileEvents.Keywords, 0, 0, (IntPtr)(&parameters));
        if (status == 0)
        {
            PidFilterAccepted = true;
            return;
        }

        status = EnableTraceEx2(_sessionHandle, ref provider, EventControlCodeEnableProvider, 5,
            KernelFileEvents.Keywords, 0, 0, IntPtr.Zero);
        if (status != 0) throw new Win32Exception(status, $"Enabling the kernel file provider failed with Win32 error {status}.");
    }

    private void StartPump()
    {
        IntPtr name = Marshal.StringToHGlobalUni(SessionName);
        try
        {
            var logfile = new EventTraceLogfileW
            {
                LoggerName = name,
                ProcessTraceMode = ProcessTraceModeRealTime | ProcessTraceModeEventRecord,
                EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_callback)
            };
            _traceHandle = OpenTraceW(ref logfile);
            if (_traceHandle == InvalidProcessTraceHandle) throw Native.Error("Opening the ETW session for reading");
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }

        ulong handle = _traceHandle;
        _pump = new Thread(() => ProcessTrace(new[] { handle }, 1, IntPtr.Zero, IntPtr.Zero))
        {
            IsBackground = true,
            Name = "ETW pump"
        };
        _pump.Start();
    }

    private void OnEvent(IntPtr recordPtr)
    {
        byte* r = (byte*)recordPtr;
        uint pid = *(uint*)(r + 12);
        if (pid != _targetPid)
        {
            Interlocked.Increment(ref OtherProcessEvents);
            return;
        }

        Guid provider = *(Guid*)(r + 24);
        if (provider != KernelFileEvents.ProviderId) return;

        ushort flags = *(ushort*)(r + 4);
        int pointerSize = (flags & EventHeaderFlag32BitHeader) != 0 ? 4 : 8;
        ushort id = *(ushort*)(r + 40);
        byte version = *(r + 42);
        ushort length = *(ushort*)(r + 86);
        byte* data = *(byte**)(r + 96);
        var payload = new ReadOnlySpan<byte>(data, length);

        FileEvent? kept = id switch
        {
            KernelFileEvents.Create or KernelFileEvents.CreateNewFile =>
                KernelFileEvents.ParseCreate(payload, version, pointerSize) is { } c ? new FileEvent(id, pid, c.FileObject, c.FileName, 0) : null,
            KernelFileEvents.Write =>
                KernelFileEvents.ParseWrite(payload, version, pointerSize) is { } w ? new FileEvent(id, pid, w.FileObject, null, w.IoSize) : null,
            KernelFileEvents.DeletePath or KernelFileEvents.RenamePath =>
                KernelFileEvents.ParsePathEvent(payload, version, pointerSize) is { } p ? new FileEvent(id, pid, 0, p, 0) : null,
            _ => null
        };
        if (kept == null) return;

        Interlocked.Increment(ref TargetEvents);
        lock (_gate) _events.Add(kept);
    }

    /// <summary>Flushes, stops the session, and returns the target process's events.</summary>
    public IReadOnlyList<FileEvent> Stop()
    {
        if (!_stopped)
        {
            _stopped = true;
            // The session delivers buffers about once a second; give the last ones time to arrive.
            Thread.Sleep(TimeSpan.FromSeconds(2));
            StopLeftover();
            _pump?.Join(TimeSpan.FromSeconds(10));
            if (_traceHandle != InvalidProcessTraceHandle)
            {
                CloseTrace(_traceHandle);
                _traceHandle = InvalidProcessTraceHandle;
            }
        }
        lock (_gate) return _events.ToList();
    }

    public void Dispose()
    {
        try
        {
            Stop();
        }
        catch
        {
            // Last resort: whatever happened, don't leave the session running.
            StopLeftover();
        }
    }

    private static IntPtr AllocProperties()
    {
        int size = PropertiesSize + NameBytes;
        IntPtr p = Marshal.AllocHGlobal(size);
        new Span<byte>((void*)p, size).Clear();
        byte* b = (byte*)p;
        *(uint*)(b + 0) = (uint)size;                       // Wnode.BufferSize
        *(uint*)(b + 40) = 1;                               // Wnode.ClientContext: QPC timestamps
        *(uint*)(b + 44) = WnodeFlagTracedGuid;             // Wnode.Flags
        *(uint*)(b + 48) = 64;                              // BufferSize, KB
        *(uint*)(b + 52) = 4;                               // MinimumBuffers
        *(uint*)(b + 56) = 32;                              // MaximumBuffers
        *(uint*)(b + 64) = EventTraceRealTimeMode;          // LogFileMode
        *(uint*)(b + 68) = 1;                               // FlushTimer, seconds
        *(uint*)(b + 116) = PropertiesSize;                 // LoggerNameOffset
        return p;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventFilterDescriptor
    {
        public ulong Ptr;
        public uint Size;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EnableTraceParameters
    {
        public uint Version;
        public uint EnableProperty;
        public uint ControlFlags;
        public Guid SourceId;
        public IntPtr EnableFilterDesc;
        public uint FilterDescCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EventTraceHeader
    {
        public ushort Size;
        public ushort FieldTypeFlags;
        public uint Version;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid Guid;
        public ulong ProcessorTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EventTrace
    {
        public EventTraceHeader Header;
        public uint InstanceId;
        public uint ParentInstanceId;
        public Guid ParentGuid;
        public IntPtr MofData;
        public uint MofLength;
        public uint ClientContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TraceLogfileHeader
    {
        public uint BufferSize;
        public uint Version;
        public uint ProviderVersion;
        public uint NumberOfProcessors;
        public long EndTime;
        public uint TimerResolution;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint BuffersWritten;
        public Guid LogInstanceGuid;
        public IntPtr LoggerName;
        public IntPtr LogFileName;
        public fixed byte TimeZone[172];
        public long BootTime;
        public long PerfFreq;
        public long StartTime;
        public uint ReservedFlags;
        public uint BuffersLost;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EventTraceLogfileW
    {
        public IntPtr LogFileName;
        public IntPtr LoggerName;
        public long CurrentTime;
        public uint BuffersRead;
        public uint ProcessTraceMode;
        public EventTrace CurrentEvent;
        public TraceLogfileHeader LogfileHeader;
        public IntPtr BufferCallback;
        public uint BufferSize;
        public uint Filled;
        public uint EventsLost;
        public IntPtr EventRecordCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int StartTraceW(out ulong sessionHandle, string sessionName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int ControlTraceW(ulong sessionHandle, string sessionName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll")]
    private static extern int EnableTraceEx2(ulong traceHandle, ref Guid providerId, uint controlCode, byte level,
        ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ulong OpenTraceW(ref EventTraceLogfileW logfile);

    [DllImport("advapi32.dll")]
    private static extern int ProcessTrace(ulong[] handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll")]
    private static extern int CloseTrace(ulong traceHandle);
}
