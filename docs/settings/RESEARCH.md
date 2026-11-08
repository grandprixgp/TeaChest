# Finding app settings: research notes

**Written:** 8 Nov 2026, on `research/settings-discovery` (from `main` at `f9c5fab`)
**What this is:** the groundwork for `SPEC.md` in this folder. It covers each way TeaChest could work out which files and folders hold an app's settings, what each one needs, how far I'd trust it, what it costs, and what it risks. Where I checked something on a real PC, it says so. Where I haven't, it says "untested".

TeaChest moves apps today, not their settings. Owner decision 1 ("Settings over Nearby") needs a per-app recipe of paths. The question here is how to get those recipes without hand-writing hundreds of them.

---

## The short version

| Method | Admin? | Tells you which app? | Good for | Verdict |
|---|---|---|---|---|
| Hand-written recipes | No | Yes, by definition | The 50 to 100 apps most people have | **The backbone** |
| Static heuristics (known folders, vendor names, AppX package folders) | No | Mostly, by name matching | The long tail | **Second layer**, shown as "probably" |
| Outside catalogues (Ludusavi, Scoop, Mackup, USMT) | No | Yes | Seeding and checking recipes | Use as **references**; ship only what the licence allows |
| `FileSystemWatcher` / `ReadDirectoryChangesW` while an app runs | No | Only by timing | A per-user "learn mode" | **Best fit for learn mode** |
| USN change journal | Yes (an unprivileged variant returns records without names) | No (no process ID) | What changed on the volume in the last hours | **Useful, but not for attribution**, and too short for history. Optional |
| ETW `Microsoft-Windows-Kernel-File` | Yes | **Yes** (process ID per event) | Exact "this process wrote these files" | **Most precise**, but admin, heavy, and easy to leave running |
| Process Monitor and other minifilter tools | Yes, plus a driver | Yes | Comparison only | **Not for TeaChest** |

My recommendation, argued in `SPEC.md`: recipes first, heuristics second, and change watching only as an opt-in learn mode, unelevated by default.

---

## 1. The NTFS USN change journal

### How it works

NTFS (and ReFS) keeps a per-volume log of changes: one record each time a file or folder is created, written, renamed, deleted, has its attributes changed, or is closed. It's on by default on Windows client volumes (Vista and later; MS-FSA appendix A, note 9). Windows Search, backup tools and file indexers such as Everything use it to avoid rescanning the disk.

Three control codes matter, each called with `DeviceIoControl` on a volume handle opened as `\\.\C:`:

- **`FSCTL_QUERY_USN_JOURNAL`** returns `USN_JOURNAL_DATA_V1`: the journal ID, `FirstUsn` (oldest record still there), `NextUsn` (where the next record goes), `LowestValidUsn`, `MaxUsn`, `MaximumSize` and `AllocationDelta`.
- **`FSCTL_READ_USN_JOURNAL`** takes `READ_USN_JOURNAL_DATA_V1` (start USN, a reason mask, the journal ID, and the record versions you accept) and returns a buffer: the next USN to read from, then a run of records. Calling it in a loop from a USN you noted earlier gives you everything that happened since.
- **`FSCTL_ENUM_USN_DATA`** walks the master file table instead of the journal: one record per file or folder that exists now, with its file reference number, parent reference and name. It's how you build a reference-to-path map for the whole volume quickly.

Records come in three shapes. **V2** (XP and later) has 64-bit file references. **V3** (Windows 8 and later) has 128-bit ones, which ReFS needs. **V4** (Windows 8.1 and later) is for range tracking and isn't needed here. Each has `RecordLength`, so code skips unknown shapes safely.

What a record holds: `FileReferenceNumber`, `ParentFileReferenceNumber`, `Usn`, a UTC `TimeStamp`, a `Reason` bitmask (`DATA_EXTEND`, `DATA_OVERWRITE`, `FILE_CREATE`, `FILE_DELETE`, `RENAME_OLD_NAME`, `RENAME_NEW_NAME`, `CLOSE` and so on), `SourceInfo`, `SecurityId`, `FileAttributes`, and the **file name only**, not the path.

Reasons accumulate while a file is open: each change adds a record whose `Reason` is the running OR of everything since the open, and the final one has `USN_REASON_CLOSE` set. Reading with `ReturnOnlyOnClose = 1` gives one summary record per open-to-close, which is what you want for "what got written".

### Mapping file references to paths

A record names the file and its parent folder's reference number, but not the full path. Three ways to get one:

1. **`OpenFileById` + `GetFinalPathNameByHandle`.** Open the parent folder by its reference number and ask for its path. Simple, and correct for anything that still exists. It fails for folders deleted since, and it opens the folder (a cheap metadata open, but still an open).
2. **Walk the parent chain from an `FSCTL_ENUM_USN_DATA` map.** Enumerate the MFT once (a few seconds on a large SSD, tens of MB of memory for a million files), keep reference → (parent, name), then walk up to the root. This is what Everything does. It handles bulk lookups well, but the map is only a snapshot, so rename and delete records have to update it as you read.
3. **Filter by ancestry instead of resolving everything.** If you only care about records under one folder, resolve that folder's reference number once, then keep a set of "known descendants" and add to it as `FILE_CREATE` records for subfolders arrive. Records whose parent isn't in the set are dropped without ever being turned into a path. **This is what the prototype does**, and it's the privacy-friendly way: paths outside the folder of interest are never built.

Edge cases: a rename produces two records (old name under the old parent, new name under the new parent). Hard links mean one file can have several parents, and a record shows only the name used. Reference numbers carry a 16-bit sequence number, so a reused MFT slot gets a different full reference, but code comparing only the low 48 bits would confuse them.

### What a record doesn't tell you

**There's no process ID, thread ID or user in a USN record.** `SecurityId` is an index into the volume's security descriptor table (the file's permissions), not who wrote it. `SourceInfo` only marks changes made by data-management software, antivirus side streams or replication (`USN_SOURCE_DATA_MANAGEMENT`, `USN_SOURCE_AUXILIARY_DATA`, `USN_SOURCE_REPLICATION_MANAGEMENT`), and only when that software sets it.

So attributing writes to an app has to be done indirectly:

- **By time window:** note `NextUsn` before launching the app and again after it closes, and look at what changed in between. Everything else running at the time (browser caches, OneDrive, Defender, Windows Update, the indexer) lands in the same window. It's noisy on a real PC.
- **By place:** keep only records under folders whose names match the app's vendor or product (from the uninstall entry), or under the folder you're testing. That works well when the app uses a sensibly named folder, which most do.
- **By difference:** run the same window twice, once with the app and once without, and keep what only appears with it. Better, but slow and still noisy.
- **By joining with ETW** (section 2), which does carry the process ID. At that point ETW is doing the attributing and the journal adds little.

The honest summary: **the journal is good at "what changed", poor at "who changed it".**

### Size and wrap-around

The journal is a circular log with a target size. When it grows past `MaximumSize + AllocationDelta`, NTFS trims the oldest `AllocationDelta` bytes at its next checkpoint, and `FirstUsn` moves forward. A V2 record is about 60 bytes plus the name, so typically 80 to 120 bytes.

- Reading from a USN older than `FirstUsn` fails with `ERROR_JOURNAL_ENTRY_DELETED`. Code must treat that as "history lost, start again", not as an error to retry.
- If the journal is deleted and re-created (by an admin, or by some disk tools), its ID changes, and reading with the old ID fails. Records written in between are simply gone.
- Microsoft's own advice for backup software is to raise the default size, which shows how small it is for long histories.
- **Measured on the development PC (Windows 11, build 26300):** `MaximumSize` 32 MB (`0x2000000`), `AllocationDelta` 8 MB (`0x800000`), about 33 to 40 MB of records held, and **the oldest record was 0.8 hours old**. That PC is busier than most (builds, agents, sync), but even a quiet PC would only get to days.

**What that means for TeaChest:** the journal holds hours to days of a PC's history, not months. "Which folders do you use most" over a long period would need TeaChest to read the journal regularly and keep its own summary, which means a background task or service. That's a lasting system change, so it's out unless the owner decides otherwise. Reading the journal **while a learn session runs**, though, is easily inside its window.

### Needing admin

Microsoft's docs say every change journal operation through these control codes needs membership of Administrators. In practice, opening `\\.\C:` for reading needs an elevated token.

There is also **`FSCTL_READ_UNPRIVILEGED_USN_JOURNAL`** (function 234, `FILE_ANY_ACCESS`), which appears in the SDK headers and in wrappers such as Vanara, but has no page on Microsoft Learn. The prototype tried it, from an elevated process:

- It returned **the same records** as the normal read (same count, same file IDs, parents, reasons and times), **but every file name was blank**, in all 181 records of the window.
- It worked on the volume handle and also on **a handle to an ordinary folder opened only to read attributes**, which is the kind of handle an unelevated process can open.

So the unprivileged read looks designed for exactly this: a non-admin caller learns *that* things changed, and by file ID *which* things, but has to resolve names itself, which it can only do for files it's allowed to open (with `OpenFileById` on its own handle). **Untested from a standard-user token**: my process was elevated, so I can't yet say an unelevated TeaChest gets the same result. If it does, an unelevated learn mode could use the journal after all, watching IDs and resolving only the ones under the folders it cares about. That's worth one more test in a non-admin session before anyone builds on it.

**How this fits TeaChest:** the app runs unelevated and has one elevated helper path today (`TeaChest.exe --configure-firewall`, which adds the firewall rule and exits). A journal reader would be a second helper of the same shape, and the shape matters for privacy: the elevated process sees the **whole volume's** activity, every user's. So it must take the folders of interest as input, filter inside itself, and hand back only the records under those folders, never a raw dump. It should also refuse to run unless the user asked for a learn session, and print nothing else.

### Cost and reliability

- **CPU and I/O:** reading a few MB of journal takes milliseconds. An MFT enumeration on a large disk takes seconds and some memory.
- **Reliability:** high for "something with this name under this folder changed". Low for attribution (see above), and zero for anything older than the journal's window.
- **Not covered:** FAT32 and exFAT drives (USB sticks), network shares, and cloud placeholder files that change without local writes.

### Privacy risk: high

The journal is a timeline of everything every user did on the volume: document names, browser profile churn, downloads, tool names, project names. Even file names alone are revealing. Anything that reads it must filter before keeping or showing anything, and must never send journal data to a peer. The prototype follows that rule: it drops every record outside its scratch folder before building a path.

---

## 2. Other ways to watch

### ETW: `Microsoft-Windows-Kernel-File`

The manifest-based kernel file provider, GUID `{EDD08927-9CC4-4E65-B970-C2560FB5C289}`. Its keywords include `FILENAME` (0x10), `FILEIO` (0x20), `CREATE` (0x80), `WRITE` (0x200), `DELETE_PATH` (0x400), `RENAME_SETLINK_PATH` (0x800) and `CREATE_NEW_FILE` (0x1000). Every event's header carries the **process ID** and thread ID. The `Create` event (ID 12) includes the file name as an NT path (`\Device\HarddiskVolumeN\...`) and a `FileObject` pointer. `Write` (16), `Cleanup` (13) and `Close` (14) carry the `FileObject` but no name, so names come from matching the `FileObject` against an earlier `Create`. The older alternative is the NT Kernel Logger's `FileIo` class, which needs the special kernel session.

- **Needs:** an elevated token, membership of Performance Log Users, or a service account, to start a session. Since Windows 10 1703 a low-privilege process can start a *private* in-process session, but that only sees providers in its own process, which doesn't help here.
- **Attribution:** exact, by process ID. But it misses work done on an app's behalf by another process: services, brokers, updaters, child processes with new IDs, and Store app settings written by system components. Following child processes needs process-start events too.
- **Cost:** heavy. A real-time session with the I/O keywords on a busy PC delivers thousands of events a second. `EnableTraceEx2` can filter by process ID (`EVENT_FILTER_TYPE_PID`, Windows 8.1 and later, up to 8 IDs). **Tested: this provider ignores it.** `EnableTraceEx2` accepted the filter without complaint, and the session still delivered 10,000 to 33,000 events from other processes during a half-second test, so all the filtering has to happen in the consumer, and the consumer sees every process's file names in memory while it does.
- **Reliability:** events can be dropped if the consumer falls behind (the session reports lost events). Paths come back in NT device form and need mapping to drive letters.
- **The trap:** **an ETW session outlives the process that started it.** If the tool crashes, the session keeps running, and collecting, until someone stops it or the PC restarts, and only 64 sessions can exist at once. Any use in TeaChest needs a fixed session name, a stop in a `finally`, a stop of any leftover session with that name before starting, and a check afterwards.
- **Privacy risk:** high while it runs, the same as the journal but live and with process IDs. Filter in-process, keep nothing outside the target.

### `ReadDirectoryChangesW` / `FileSystemWatcher`

Watches one folder tree and reports changes as they happen.

- **Needs:** nothing special. Any process can watch folders it can read, so a per-user learn mode can watch `%APPDATA%`, `%LOCALAPPDATA%` and `Documents` unelevated.
- **Attribution:** none, as with the journal. Timing plus place is all you get.
- **Cost:** low. It works only while the watcher runs, and covers only the trees you name.
- **Reliability:** the buffer can overflow under bursts, in which case you lose events and get an error (.NET raises `Error` with an `InternalBufferOverflowException`, and the buffer tops out at 64 KB). Fine for "which folders saw writes", not for an exact log.
- **Privacy risk:** moderate, and much smaller than the journal: it only sees the trees you choose, only while running, and only the current user's files.

For a learn mode that asks "what does this app write while you use it?", this is the best fit: no admin, nothing lasting, and the same "by time and place" attribution the journal would give.

### Process Monitor and other minifilter tools (comparison only)

Process Monitor loads a file system minifilter driver and records every file and registry operation with process, thread, stack and result. It's the gold standard for "where does this app keep its settings", and it's how people write recipes by hand. TeaChest can't use it: it needs a driver (a lasting system change, and a signed driver of our own would be a major undertaking), it's elevated, and Sysinternals tools come under Microsoft's licence terms. UE-V's generator (section 4) does the same job with its own monitoring. I didn't run it, per the brief.

---

## 3. Static heuristics

These need no admin and no watching, and they run in milliseconds, which is why they're the second layer after recipes.

### Known folders

Resolve these with `SHGetKnownFolderPath` (or `Environment.GetFolderPath`) rather than building paths, since users and admins can redirect them:

| Folder | Known folder | What lives there |
|---|---|---|
| `%APPDATA%` | `FOLDERID_RoamingAppData` | Settings meant to follow the user: the best hunting ground |
| `%LOCALAPPDATA%` | `FOLDERID_LocalAppData` | Machine-specific data, caches, often whole per-user installs (Squirrel, Chromium-based apps) |
| `%USERPROFILE%\AppData\LocalLow` | `FOLDERID_LocalAppDataLow` | Low-integrity apps; many Unity games keep saves and settings here |
| `Documents\<app>` | `FOLDERID_Documents` | Older apps, game saves, project templates |
| `Saved Games` | `FOLDERID_SavedGames` | Some games |
| `%ProgramData%` | `FOLDERID_ProgramData` | Machine-wide data. Rarely per-user settings, often licences and caches |
| `%USERPROFILE%\.<tool>` | `FOLDERID_Profile` | Cross-platform tools: `.gitconfig`, `.ssh`, `.vscode`, `.cargo` |

### Matching names from the uninstall entries

TeaChest already reads `DisplayName`, `Publisher` and `InstallLocation` from the uninstall keys. A good first guess is a folder under one of the roots above whose name matches the normalised publisher or product name, or `<Publisher>\<Product>` two levels deep. Using `TextUtil.NormalizeStrict` on both sides already handles case and punctuation. Things to watch:

- **Publisher names are long and messy** ("Microsoft Corporation", "Mozilla", "JetBrains s.r.o."), so strip legal suffixes (`Inc`, `Ltd`, `LLC`, `GmbH`, `s.r.o.`, `Corporation`) before matching.
- **Folder names follow the product, not the publisher, for many apps** (`%APPDATA%\Code` for VS Code, `%APPDATA%\obs-studio`), so a heuristic match is a suggestion, never a certainty.
- **Shared vendor folders** (`%APPDATA%\Microsoft`, `%LOCALAPPDATA%\Google`) hold many products, so a publisher-only match there must go one level deeper or not match at all.
- **`InstallLocation`** points at binaries. It only matters for portable apps that keep a `settings.ini` or `data\` folder beside the exe.

### The registry: `HKCU\Software\<Vendor>\<Product>`

Same matching, on the registry. Many Win32 apps keep settings here (Notepad++ doesn't, 7-Zip, PuTTY and Sysinternals tools do). Carrying registry settings means exporting a key and importing it on the new PC, which is a registry write. The spec sheet's rules forbid registry edits by implementers, so this needs its own decision (see `SPEC.md`).

### Store and MSIX apps

This is the most deterministic heuristic, because TeaChest already records each Store app's `PackageFamilyName`. Every packaged app's per-user data lives under `%LOCALAPPDATA%\Packages\<PackageFamilyName>\`:

- `LocalState`: the app's own files. Usually the settings worth carrying.
- `RoamingState`: meant to roam. Roaming app data is deprecated, but the folder still exists.
- `Settings\settings.dat`: an app-private registry hive (`ApplicationData.LocalSettings`). Copying it between PCs is unsupported and fragile.
- `LocalCache`, `TempState`, `AC` (`INetCache`, `INetCookies` and other per-app browser state): never carry.

USMT explicitly doesn't migrate Store app settings. Restoring `LocalState` into a freshly installed package is plausible for simple apps but untested, and the package version and ACLs on the new PC may differ. I'd offer it only per app, with a recipe saying it's known to work.

### Electron and Chromium-based apps

Electron's default `userData` folder is `%APPDATA%\<app name>`. Inside it, the layout is Chromium's, which gives a very reliable exclude list that applies to dozens of apps (VS Code, Slack, Discord, Teams classic, Obsidian, Postman, and so on):

- **Never carry:** `Cache`, `Code Cache`, `GPUCache`, `DawnCache`, `Crashpad`, `blob_storage`, `Service Worker\CacheStorage`, `logs`, `*.log`.
- **Never carry, because it holds credentials:** `Cookies` and `Network\Cookies` (encrypted with a key in `Local State` that's itself protected by DPAPI, so it's useless on another PC anyway), `Local Storage\leveldb`, `Session Storage`, `IndexedDB`, `Login Data`, and `Local State` itself. Many apps keep sign-in tokens in Local Storage.
- **Usually what you want:** the app's own JSON or folder at the top level (`settings.json`, `config.json`, `User\` for VS Code).

Squirrel-installed apps put their binaries in `%LOCALAPPDATA%\<app>\app-x.y.z\` and their data in `%APPDATA%\<app>\`, so the `%LOCALAPPDATA%` folder is not settings.

---

## 4. What others have catalogued

Facts such as "VS Code keeps its settings in `%APPDATA%\Code\User\settings.json`" aren't copyrightable, but a curated database of them can be. So the licence decides whether TeaChest may **ship** a catalogue, **fetch** it at run time, or only **read it as a reference** while writing its own recipes.

| Source | What it covers | Licence | Windows value | Ship? | Fetch? |
|---|---|---|---|---|---|
| **Mackup** (`lra/mackup`) | About 600 apps' config files, as `.cfg` files listing paths relative to home | **GPL-3.0-or-later**, not MIT (checked on PyPI and GitHub) | Low: almost all macOS or XDG paths | No: shipping GPL data inside an MIT app brings GPL terms into it | Possible, but low value on Windows |
| **Ludusavi manifest** (`mtkennerly/ludusavi-manifest`) | Save and config paths for tens of thousands of PC games, with tags (`save`, `config`) and conditions (`os`, `store`) | Repo is **MIT**, but the data is compiled from **PCGamingWiki**, which is **CC BY-NC-SA 3.0**. I'd treat the data as carrying NC-SA terms | **High** for games: uses placeholders like `<winAppData>`, `<winDocuments>`, `<home>`, `<base>` (install folder), `<storeUserId>`, plus registry keys | Not without the owner deciding on the NC-SA question | **Yes, plausibly**, unmodified, with attribution, as an optional download. Or simply point game players at Ludusavi itself |
| **PCGamingWiki** | The upstream of the above | CC BY-NC-SA 3.0 | High, games | No | Through Ludusavi rather than scraping the wiki |
| **chezmoi** (`twpayne/chezmoi`) | A dotfile manager, not a catalogue | MIT | None as data. Useful as design: templates, per-machine differences, encrypted secrets, "never store secrets in plain text" | n/a | n/a |
| **dotbot** (`anishathalye/dotbot`) | A dotfile installer, not a catalogue | MIT | None as data | n/a | n/a |
| **Community dotfile repos** | Personal configs | Various | Low, and full of personal data | No | No |
| **USMT `MigApp.xml`** | Office, plus a list of mostly older apps | Ships with the Windows ADK under Microsoft's licence terms. I'd assume it isn't redistributable | Low for current apps, but its **rule language** is the best prior art: include, exclude, merge, version detection, "migrate after install, before first run" | No | No |
| **Windows Easy Transfer** | Gone: removed in Windows 10, with Microsoft pointing people to PCmover Express | n/a | None | n/a | n/a |
| **UE-V settings location templates** | Microsoft's own app-settings catalogue for roaming settings in Windows 10/11 Enterprise. The UE-V generator **watches an app run** to discover where it writes, limited to `HKCU` and `AppData\Roaming` | Microsoft | The **design** is directly relevant: Microsoft's answer to this problem was a recipe database plus a learn mode, the same shape I'm recommending | No | No |
| **Scoop buckets** (`ScoopInstaller/Main`, `/Extras`) | Each manifest's `persist` field lists the data files and folders of a portable app, relative to its install folder | **Unlicense** (public domain), both checked | Medium: only for portable layouts, but those are the apps TeaChest's scan can't see otherwise, and it's the one catalogue TeaChest could freely ship parts of | **Yes** | Yes |
| **PortableApps.com launcher files** | Each package's launcher INI lists the settings folders, files and registry keys it moves in and out | Varies by package. **Untested** lead, not checked | Potentially high for Win32 apps | Unknown | Unknown |
| **winget-pkgs** | Installers only, no settings | MIT | None for settings | n/a | n/a |

**My take:** no outside catalogue gives TeaChest its recipes for free on Windows. Ludusavi is excellent for games but comes with a licence question. Scoop's `persist` data is freely usable but narrow. The rest are prior art. The practical route is a small, hand-checked recipe set of TeaChest's own (written from each app's documentation and checked on a real PC), with Scoop's data as a supplement and Ludusavi as an optional fetch if the owner is happy with its licence.

---

## 5. What not to carry

These rules apply to every recipe and every heuristic, and the scanner should enforce them even when a recipe says otherwise.

| Kind | Examples | Why not |
|---|---|---|
| **Caches** | `Cache`, `Code Cache`, `GPUCache`, `ShaderCache`, `*.tmp`, `Temp`, `LocalCache` | Rebuilt on demand. Large. Often version-bound |
| **Logs and crash dumps** | `logs`, `*.log`, `Crashpad`, `CrashDumps`, `*.dmp` | No value on a new PC, and they leak activity |
| **Browser profiles** | Chrome's `User Data`, Edge, Firefox's `Profiles` | Huge, full of cookies, saved passwords and history, and the browsers' own sync does this properly |
| **Credentials and tokens** | `Login Data`, `Cookies`, `Local Storage`, `.git-credentials`, `.npmrc` with `_authToken`, `.pypirc`, `.docker\config.json` `auths`, `credentials.json`, `*.pem`, `*.key`, `id_*` SSH private keys, `.aws\credentials`, `.azure`, `gh\hosts.yml` | A peer must never be able to read them. Moving them is a security decision the user makes knowingly, outside TeaChest |
| **Machine-bound data** | Anything encrypted with DPAPI (Chromium `Local State` keys, many apps' saved passwords), `settings.dat` hives, hardware IDs, GPU-specific shader caches, absolute paths to the old PC's drives | Won't decrypt or won't make sense on another PC, and can break the app |
| **Licence activations** | Activation files, `*.lic`, licence registry keys | Often tied to hardware, may count as a second activation, may break the licence terms |
| **Sync clients' own state** | OneDrive, Dropbox, Google Drive databases | Copying them confuses the sync and can cause data loss |
| **Huge or open databases** | Mail stores (`.ost`), app-held SQLite files open while running | Size, and a copy taken while open can be corrupt |

Secret detection (in `SPEC.md`) adds a content check on top of these name rules, because tokens turn up in ordinary-looking JSON.

---

## Sources

Checked directly: Microsoft Learn pages for `FSCTL_READ_USN_JOURNAL`, `FSCTL_ENUM_USN_DATA`, `USN_RECORD_V2`, `USN_RECORD_V4`, `USN_JOURNAL_DATA_V0`/`V1`, `READ_USN_JOURNAL_DATA_V1`, "Using the change journal identifier", "Creating, modifying, and deleting a change journal", "Walking a buffer of change journal records", `fsutil usn`, MS-FSA appendix A, "Controlling event tracing sessions", "Configuring and starting a private logger session", the `FileIo` class, USMT's "What does USMT migrate" and "Migrate application settings", UE-V 2.1's "Working with custom templates and the generator", and Electron's `app.getPath` docs. The `Microsoft-Windows-Kernel-File` manifest as extracted in `repnz/etw-providers-docs`. Licences from each project's GitHub page or PyPI entry, and PCGamingWiki's copyright page.

Checked with the prototype (`tools/SettingsProbe`) on the development PC: the journal's size and reach, attribution by time and place, the unprivileged read blanking names, and the ETW PID filter being ignored.

Leads not verified: `FSCTL_READ_UNPRIVILEGED_USN_JOURNAL` from a standard-user token, and PortableApps.com launcher licences.
