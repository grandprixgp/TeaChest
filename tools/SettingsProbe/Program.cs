using System.ComponentModel;
using System.Diagnostics;
using SettingsProbe;
using SettingsProbe.Etw;
using SettingsProbe.Usn;

// SettingsProbe: proves that TeaChest could learn where an app keeps its settings by watching what
// it writes. Everything printed is under the scratch folder or is a count. See README.md.

if (args.Length >= 2 && args[0] == "writer") return TestApp.Run(args[1]);

string command = args.Length > 0 ? args[0] : "help";
string scratch = Path.GetFullPath(Option("--scratch") ?? Path.Combine(Environment.CurrentDirectory, ".probe-scratch"));
bool useEtw = args.Contains("--etw");
bool tryUnprivileged = args.Contains("--unprivileged-read");
bool keep = args.Contains("--keep");

try
{
    switch (command)
    {
        case "journal":
            PrintJournal(scratch);
            return 0;
        case "run":
            return Run(scratch, useEtw, tryUnprivileged, keep);
        case "etw-cleanup":
            Console.WriteLine(KernelFileSession.StopLeftover() ? "Stopped a leftover probe session." : "No probe session was running.");
            return 0;
        default:
            Console.WriteLine("Usage: SettingsProbe journal | run [--etw] [--unprivileged-read] [--keep] [--scratch <dir>] | etw-cleanup");
            return 2;
    }
}
catch (Win32Exception ex)
{
    // Messages here are built by this tool and never include a path.
    Console.Error.WriteLine(ex.Message);
    return 1;
}

string? Option(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static char DriveOf(string path)
{
    string root = Path.GetPathRoot(path) ?? "";
    if (root.Length < 2 || root[1] != ':') throw new ArgumentException("The scratch folder must be on a lettered drive.");
    return char.ToUpperInvariant(root[0]);
}

static void PrintJournal(string scratch)
{
    using UsnJournalReader reader = UsnJournalReader.Open(DriveOf(scratch));
    UsnJournalInfo info = reader.Query();
    Console.WriteLine($"Journal on {DriveOf(scratch)}:");
    Console.WriteLine($"  maximum size     {info.MaximumSize / (1024.0 * 1024):0.#} MB (0x{info.MaximumSize:X})");
    Console.WriteLine($"  allocation delta {info.AllocationDelta / (1024.0 * 1024):0.#} MB (0x{info.AllocationDelta:X})");
    Console.WriteLine($"  held now         {(info.NextUsn - info.FirstUsn) / (1024.0 * 1024):0.#} MB of records (NextUsn - FirstUsn)");
    Console.WriteLine($"  record versions  {info.MinSupportedMajorVersion} to {info.MaxSupportedMajorVersion}");

    // How far back the journal reaches: the timestamp of its oldest record, and nothing else.
    DateTime? oldest = null;
    reader.Read(info.JournalId, info.FirstUsn, info.NextUsn, r => oldest ??= r.TimeStampUtc, stopAfterFirst: true);
    if (oldest is { } t)
    {
        TimeSpan age = DateTime.UtcNow - t;
        Console.WriteLine($"  oldest record    {age.TotalHours:0.#} hours ago");
    }
}

static int Run(string scratch, bool useEtw, bool tryUnprivileged, bool keep)
{
    if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
    Directory.CreateDirectory(scratch);
    Console.WriteLine($"Scratch folder: {scratch}");

    try
    {
        UInt128 rootId = UsnJournalReader.FileIdOf(scratch);
        using UsnJournalReader reader = UsnJournalReader.Open(DriveOf(scratch));
        UsnJournalInfo before = reader.Query();

        var stopwatch = Stopwatch.StartNew();
        using Process child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"writer \"{scratch}\"")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Couldn't start the test app.");
        uint childPid = (uint)child.Id;
        Console.WriteLine($"Test app started as PID {childPid}.");

        KernelFileSession? etw = null;
        IReadOnlyList<FileEvent> etwEvents = Array.Empty<FileEvent>();
        try
        {
            if (useEtw)
            {
                etw = KernelFileSession.Start(childPid);
                Console.WriteLine($"ETW session '{KernelFileSession.SessionName}' started; PID filter {(etw.PidFilterAccepted ? "accepted by ETW" : "refused, filtering in code only")}.");
                Thread.Sleep(500);
            }

            child.StandardInput.WriteLine("go");
            child.StandardInput.Flush();
            if (!child.WaitForExit(30_000))
            {
                child.Kill();
                throw new TimeoutException("The test app didn't finish in 30 seconds.");
            }
            Console.WriteLine($"Test app exited with {child.ExitCode} after {stopwatch.ElapsedMilliseconds} ms.");

            if (etw != null) etwEvents = etw.Stop();
        }
        finally
        {
            etw?.Dispose();
        }

        UsnJournalInfo after = reader.Query();
        var filter = new ScopeFilter(rootId);
        int read = reader.Read(before.JournalId, before.NextUsn, after.NextUsn, r => filter.Add(r));

        Console.WriteLine();
        Console.WriteLine("== USN journal ==");
        Console.WriteLine($"Window: {read} records ({(after.NextUsn - before.NextUsn) / 1024.0:0.#} KB of journal).");
        Console.WriteLine($"Kept {filter.Kept} under the scratch folder; skipped {filter.Skipped} elsewhere on the drive (not shown, never resolved).");
        IReadOnlyList<ScopedChange> changes = filter.Changes();
        PrintChanges(changes);

        Console.WriteLine();
        Console.WriteLine("== Learned recipe (attributed by time window + place) ==");
        IReadOnlyList<LearnedItem> learned = LearnedRecipe.Build(changes.Select(c => (c.RelativePath, c.IsDirectory, c.Deleted, c.MovedOut)));
        foreach (LearnedItem item in learned)
        {
            Console.WriteLine($"  {item.Verdict,-10} {item.RelativePath}  ({item.Reason})");
        }

        if (useEtw) PrintEtw(scratch, etwEvents, etw!);
        if (tryUnprivileged)
        {
            TryUnprivileged("the same (elevated) volume handle", reader, before, after, rootId, changes);
            using UsnJournalReader folderHandle = UsnJournalReader.OpenOnFolder(scratch);
            TryUnprivileged("a read-attributes handle to the scratch folder", folderHandle, before, after, rootId, changes);
        }
    }
    finally
    {
        if (!keep)
        {
            Directory.Delete(scratch, recursive: true);
            Console.WriteLine();
            Console.WriteLine("Scratch folder deleted.");
        }
    }
    return 0;
}

static void PrintChanges(IReadOnlyList<ScopedChange> changes)
{
    foreach (ScopedChange c in changes)
    {
        var what = new List<string>();
        if (c.Created) what.Add("created");
        if (c.Written) what.Add("written");
        if (c.Renamed) what.Add("renamed");
        if (c.Deleted) what.Add("deleted");
        if (c.MovedOut) what.Add("moved out");
        Console.WriteLine($"  {(c.IsDirectory ? "dir " : "file")} {c.RelativePath,-40} {string.Join(", ", what),-28} {c.RecordCount} records");
    }
}

static void PrintEtw(string scratch, IReadOnlyList<FileEvent> events, KernelFileSession session)
{
    Console.WriteLine();
    Console.WriteLine("== ETW Microsoft-Windows-Kernel-File, test app's PID only ==");
    var device = new char[1024];
    string drive = scratch[..2];
    int n = Native.QueryDosDeviceW(drive, device, device.Length);
    if (n == 0) throw Native.Error("Looking up the drive's device name");
    var scope = new NtPathScope(new string(device).Split('\0')[0], scratch);

    var names = new Dictionary<ulong, string?>();
    var written = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    var opened = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
    var deletedOrRenamed = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
    int outside = 0;

    foreach (FileEvent e in events)
    {
        switch (e.EventId)
        {
            case KernelFileEvents.Create or KernelFileEvents.CreateNewFile:
                string? rel = e.NtPath == null ? null : scope.Relative(e.NtPath);
                names[e.FileObject] = rel;
                if (rel == null) outside++; else opened.Add(rel);
                break;
            case KernelFileEvents.Write:
                if (names.TryGetValue(e.FileObject, out string? target) && target != null)
                {
                    written[target] = written.GetValueOrDefault(target) + e.IoSize;
                }
                break;
            case KernelFileEvents.DeletePath or KernelFileEvents.RenamePath:
                string? path = e.NtPath == null ? null : scope.Relative(e.NtPath);
                if (path == null) outside++; else deletedOrRenamed.Add($"{(e.EventId == KernelFileEvents.DeletePath ? "delete" : "rename")} {path}");
                break;
        }
    }

    Console.WriteLine($"Events from the test app: {session.TargetEvents}. From other processes: {session.OtherProcessEvents} (dropped unread).");
    Console.WriteLine($"Test app's file events outside the scratch folder (runtime, DLLs and so on): {outside} (not shown).");
    Console.WriteLine("Opened under scratch:");
    foreach (string o in opened) Console.WriteLine($"  {o}");
    Console.WriteLine("Bytes written under scratch, by file:");
    foreach (var (file, bytes) in written.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)) Console.WriteLine($"  {file,-40} {bytes} bytes");
    Console.WriteLine("Deletes and renames under scratch:");
    foreach (string d in deletedOrRenamed) Console.WriteLine($"  {d}");
}

static void TryUnprivileged(string handleKind, UsnJournalReader reader, UsnJournalInfo before, UsnJournalInfo after, UInt128 rootId, IReadOnlyList<ScopedChange> privileged)
{
    Console.WriteLine();
    Console.WriteLine($"== FSCTL_READ_UNPRIVILEGED_USN_JOURNAL on {handleKind} ==");
    var filter = new ScopeFilter(rootId);
    int blankNames = 0;
    try
    {
        int read = reader.Read(before.JournalId, before.NextUsn, after.NextUsn, r =>
        {
            if (r.FileName.Length == 0) blankNames++;
            filter.Add(r);
        }, unprivileged: true);
        Console.WriteLine($"Returned {read} records; {filter.Kept} under scratch, {filter.Skipped} elsewhere (not shown). Records with a blank name: {blankNames}.");
        var seen = filter.Changes().Select(c => c.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (ScopedChange c in privileged.Where(c => !seen.Contains(c.RelativePath)))
        {
            Console.WriteLine($"  Missing compared with the normal read: {c.RelativePath}");
        }
    }
    catch (Win32Exception ex)
    {
        Console.WriteLine(ex.Message);
    }
}
