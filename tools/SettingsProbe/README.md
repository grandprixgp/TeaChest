# SettingsProbe

A small research tool that proves the core idea in `docs/settings/SPEC.md`: TeaChest can learn where an app keeps its settings by watching what it writes, then sort those files into "worth carrying" and "never carry".

It isn't part of TeaChest, `TeaChest.sln` or CI, and nothing in the app references it.

## What it does

`SettingsProbe run` creates a scratch folder, notes the NTFS change journal's position, starts a stand-in "app" (this same exe in `writer` mode) that writes settings, a profile, a cache, a log, a token file and a stray temp file into the scratch folder, then reads the journal from the noted position to the current one and shows what happened **under the scratch folder only**. It then turns that into a learned recipe.

With `--etw` it also runs a real-time ETW session on `Microsoft-Windows-Kernel-File` and keeps only the stand-in app's own events, by process ID. With `--unprivileged-read` it repeats the journal read with `FSCTL_READ_UNPRIVILEGED_USN_JOURNAL`, on the volume handle and on a plain folder handle.

```text
SettingsProbe journal                       journal size, and how far back it reaches (sizes and an age only)
SettingsProbe run [--etw] [--unprivileged-read] [--keep] [--scratch <dir>]
SettingsProbe etw-cleanup                   stops a leftover probe ETW session, if one exists
```

It needs an elevated prompt: opening the volume and starting ETW both need admin. The scratch folder defaults to `.probe-scratch` in the current folder and is deleted at the end unless `--keep` is given.

## Privacy and safety

The journal covers the whole drive, so the filtering is the point of the design:

- **`ScopeFilter`** keeps a record only if its parent is the scratch folder or a folder already known to be inside it. Anything else is counted and dropped without its name being looked at, so no path outside the scratch folder is ever built.
- **ETW events** from other processes are counted and dropped in the callback. The stand-in app's own events outside the scratch folder (the .NET runtime loading, for example) are counted, never shown.
- **Read only.** The tool can only send the four control codes in `FsctlCodes`, and a test checks that create, delete and resize aren't among them.
- **ETW cleanup.** The session has a fixed name, any leftover with that name is stopped first, and the session is stopped in `Dispose`. `logman query -ets` shows nothing left after a run.
- Error messages are built by the tool and never include a path.

## Building and testing

```powershell
dotnet build tools\SettingsProbe -c Release
dotnet test tools\SettingsProbe.Tests -c Release
```

The tests only parse canned bytes. They never open a volume or start ETW.
