# TeaChest on Linux

**Written:** 8 Nov 2026, on `task/linux-core-split` (main with TC-16 and TC-17, plus #28's TC-19 to TC-21)
**Status:** draft. The five decisions below are proposals. None of them is settled until I say so.

The goal is the same promise on Linux: new PC, same apps. This file covers what has to be decided, what the code says today, the plan from here to a first Linux release, and a concrete proposal for the manifest change. The first step, splitting the portable code into `TeaChest.Core`, is needed whichever way the decisions go, so it's being done now on this branch (card TL-01).

---

## The five decisions

### 1. Same app or sister app?

**Proposed, the user decides.**

The question is whether the Linux UI and the Windows UI are the same code.

| Option | What it means | Effort | Risk | What it rules out |
|---|---|---|---|---|
| **A. Avalonia everywhere** | Port `MainWindow.xaml` and the themes to Avalonia and drop WPF on Windows too | Highest up front: about 1,800 lines of XAML (`MainWindow.xaml` 984, `Controls.xaml` 742, two theme files), the converters, and the WPF bits in the view models. Then one UI forever | The Windows app changes look and feel in one go. Avalonia's DataGrid is a separate package and behaves differently from WPF's (sorting, virtualisation, keyboard). The single-file exe grows, because Skia ships native libraries; my guess is 20 to 30 MB, not measured | WPF-only polish, such as the DWM title bar colours, unless Avalonia gets an equivalent. A Windows-only release that's as small as today's |
| **B. Sister UI: WPF on Windows, Avalonia on Linux** | Both front ends sit on the same Core and the same UI-neutral view models | Medium up front: the view models move out of WPF (TL-05), then an Avalonia shell for Linux (TL-12). Every later UI card is done twice | Low for Windows, which doesn't change. Drift between the two UIs is the long-term cost | Nothing. A later move to A stays open, and the Avalonia UI can be tried on Windows side by side before deciding |
| **C. Different toolkit on Linux** (GTK 4 through GirCore, say) | A native GNOME-looking app | Like B, but no XAML or bindings carry over, so the views are a full rewrite | Two very different UI stacks to keep up | A later single UI |
| **D. Headless first on Linux** | Ship `teachest scan` and `teachest install` on Linux with no GUI, then pick a UI | Lowest to a first release. Reuses TC-24 and TC-25's command handling once it's in Core | People expect a GUI from TeaChest. Nearby pairing needs a prompt somewhere | Nothing, but it only delays this decision |

**Recommendation: B, with a checkpoint.** Move the view models out of WPF first (that's needed for A and B alike), build the Linux front end in Avalonia, and leave the Windows app alone. Once the Avalonia UI exists and has been used, decide whether it replaces WPF on Windows too. That keeps every Windows release safe while the Linux work is young, and it turns A from a leap into a comparison. The cost is doing UI cards twice during the overlap, so the checkpoint shouldn't be put off for long.

### 2. Cross-OS migration?

**Proposed, the user decides.**

Linux to Linux is the simple version. Windows to Linux needs a mapping from WinGet IDs to Linux packages, and many apps won't have one.

| Option | Effort | Risk | What it rules out |
|---|---|---|---|
| **A. Linux to Linux only in v1** | Lowest | None beyond the backends themselves | The "moving from Windows to Linux" story, until later |
| **B. A curated WinGet-to-Flatpak table** | Small to start, then upkeep. A reviewed table of the few hundred apps that are on both WinGet and Flathub (Firefox, VS Code, Discord, Spotify, Steam, OBS, GIMP and so on) | A wrong row installs the wrong app, so every row needs review. It goes stale | Nothing |
| **C. Repology lookups** | Medium | Every scan sends the app list to a third party, which breaks the "nothing leaves the LAN" feel. Repology's name matching is loose, which is exactly the TC-06 problem again. Needs the network | Offline use |
| **D. Both B and C** | Highest | Both of the above | |

**Recommendation: A for v1, then B as the first thing after it.** v1 should still open a Windows manifest on Linux and show everything as manual, with the reason, because that's honest and costs almost nothing. The curated table (TL-13) is the right second step: it's small, offline, reviewable, and targets Flatpak, which is the same on every distro. I'd leave Repology out, mainly because it sends the app list off the machine.

### 3. Which distros first?

**Proposed, the user decides.**

| Option | Effort | Notes |
|---|---|---|
| **A. Flatpak + Debian/Ubuntu (apt)** | Two inventory backends, two install backends | Flatpak covers most desktop apps people would name, and works the same on every distro. `apt-mark showmanual` gives a decent first cut at "what did a person install" |
| **B. Flatpak only** | One backend each way | Smallest. Misses everything people installed with apt, which on Ubuntu is a lot of developer tooling |
| **C. A plus Fedora (dnf)** | Three backends | `dnf repoquery --userinstalled` is the equivalent of `apt-mark showmanual`. Adds a CI image and an rpm package |
| **D. A plus Arch (pacman)** | Three backends | `pacman -Qqe` lists explicit installs. Arch users are least likely to need a migration tool |

**Recommendation: A, then Fedora.** It's what the spec sheet suggested and the code doesn't change that: Flatpak needs no root to install per-user, so it's the easiest first install backend, and apt is the biggest base. Snap and AppImage are inventory-only at first (listed as manual), since installing them well is its own job.

### 4. Manifest schema

**Proposed, the user decides.** The full proposal is in "The manifest question" below.

**Recommendation:** add an optional `packages` list to each app, one entry per package manager, and keep today's top-level `id` meaning "the Windows install" forever. Old TeaChest builds ignore the new field, so manifests saved today and tomorrow stay readable both ways. It's the one Linux-related change worth landing early, before any Linux code, because every manifest saved from then on carries the extra room.

### 5. Branch plan

**Proposed, the user decides.**

| Option | Effort | Risk | What it rules out |
|---|---|---|---|
| **A. Long-lived `linux` branch** | Merges from `main`, often | Drifts fast against a moving `main`. Every core change on `main` has to be merged across by hand | Small, reviewable PRs |
| **B. Split on `main`, Linux code on a branch** (the spec sheet's suggestion) | Less merging, since the shared code lives on `main` | Linux-only projects still drift, though less | |
| **C. Everything on `main`, in separate projects** | Lowest | `main` carries unreleased Linux code. The Windows release must not publish it (it won't: `build.ps1` and both workflows name `src/TeaChest` only) | Hiding the work in progress, if that matters |

**Recommendation: C.** Land this split on `main` first. Then Linux code goes into its own projects (`src/TeaChest.Linux` for backends, and a Linux UI project once decision 1 is made), built and tested by the Linux CI job, and not shipped until it's ready. The Windows project never references them, so a Windows release can't pick them up by accident. Nothing drifts, and each card is a normal small PR. B is fine too if I'd rather keep `main` Windows-only for now.

---

## What the code says

Surveyed at `9df67ce`. Every Windows-only dependency, by file. Grep terms used: `System.Windows`, `Microsoft.Win32`, `Registry`, `ProtectedData`, `DllImport`, `LibraryImport`, `ComImport`, `Windows.` (WinRT), `winget`, `netsh`, `cmd.exe`, `Dwm`, `Dispatcher`, `SupportedOSPlatform`, `net8.0-windows`.

- **No `[SupportedOSPlatform]` attributes** anywhere.
- **`net8.0-windows10.0.19041.0`** in both `src/TeaChest/TeaChest.csproj` and `tests/TeaChest.Tests/TeaChest.Tests.csproj`, with `UseWPF`. The 10.0.19041 part is there for the WinRT projection that `AppxInventoryService` uses.
- **One `DllImport`**: `dwmapi.dll` in `ThemeService`. **One COM interop block**: Network List Manager in `NetworkProfiles`. **One WinRT use**: `Windows.Management.Deployment` in `AppxInventoryService`. No `LibraryImport`.

Sorting key:
- **Portable as is:** builds and behaves the same on Linux today.
- **Portable once moved:** fine on its own, but sits in the WPF project or leans on something Windows-only that a small change removes.
- **Needs an interface:** the logic is shared, but one piece has to be done differently per OS.
- **Windows only:** the whole thing is a Windows feature; Linux gets its own version, not a port.

| File | Windows-only dependency | Sort | Where it is after TL-01 |
|---|---|---|---|
| `Models/AppClassifier.cs` | None in code. The rules are Windows-flavoured (inbox apps, Store, VC++ runtimes) | Portable as is. Linux needs its own base-system rules later | Core |
| `Models/AppItem.cs` | None. Some fields only mean something on Windows (`PackageFamilyName`, `WingetArgs`, `UninstallString`) | Portable as is | Core |
| `Models/CommandLine.cs` | None. It implements Windows quoting rules (`CommandLineToArgvW`), for `WingetArgs` | Portable as is | Core |
| `Models/Manifest.cs` | None. Has a `WindowsVersion` field | Portable as is. See decision 4 | Core |
| `Models/PackageIds.cs` | None. Validates WinGet and Store IDs | Portable as is | Core |
| `Models/TextUtil.cs` | None | Portable as is | Core |
| `Services/AppPaths.cs` | `SpecialFolder.LocalApplicationData`. On Linux .NET maps that to `$XDG_DATA_HOME`, or `~/.local/share`, which is already the right XDG place | Portable as is | Core |
| `Services/ManifestService.cs` | None. Also writes the `.winget` file, which is a Windows artefact but harmless | Portable as is | Core |
| `Services/ManifestSanitizer.cs` | None. The rules are WinGet-specific | Portable as is | Core |
| `Services/WingetConfigurationWriter.cs` | None (plain text) | Portable as is | Core |
| `Services/ProcessRunner.cs` | None | Portable as is | Core |
| `Services/InstallService.cs` | Plan and report logic is portable. It built the installed index from the registry, AppX and WinGet, and installed through `WingetService` | Needs an interface | Core, with `IInstallBackend`. `WingetInstallBackend` stays on Windows |
| `Services/InstalledIndex.cs` | The index itself is portable. `BuildAsync` and `Build` read the registry, AppX and WinGet | Needs an interface | Core keeps the index. `WindowsInstalledIndex` builds it on Windows |
| `Services/Lan/LanProtocol.cs`, `Pairing.cs`, `PairingThrottle.cs` | None | Portable as is | Core |
| `Services/Lan/Discovery.cs` | None in code. Uses `UdpClient` broadcast and `NetworkInterface`, which work on Linux. Behaviour still to check there (TL-11) | Portable as is | Core |
| `Services/Lan/PeerClient.cs`, `PeerServer.cs` | None in code. `SslStream` uses OpenSSL on Linux instead of SChannel; mutual TLS with self-signed certificates needs checking there (the Core tests do it) | Portable as is | Core |
| `Services/Lan/InventoryDiff.cs` | None, once `InstalledIndex` is split | Portable once moved | Core |
| `Services/Lan/DeviceIdentity.cs` | `ProtectedData` (DPAPI) for `device.pfx.dpapi`. Also `KnownPeers`, which is plain JSON | Needs an interface | Core, with `IDeviceKeyProtector`. `DpapiKeyProtector` stays on Windows |
| `Services/ScanService.cs` | Wired to `RegistryInventoryService`, `AppxInventoryService`, `WingetService`. The merge and dedupe logic (`CollectAsync`, `MergeRange`, `DeduplicateById`) is portable | Needs an interface | WPF project for now. TL-03 |
| `Services/NameCache.cs` | None in code, but it only caches WinGet and Store lookups for `ScanService` | Portable once moved | WPF project, with `ScanService` |
| `Services/ExtraSources/IExtraSource.cs` | `RunShimAsync` runs `cmd.exe /d /c`; `FindOnPath` splits `PATH` on `;` and tries `PATHEXT` | Needs an interface (how to run a tool, and how to find it on `PATH`) | WPF project. TL-04 |
| `Services/ExtraSources/VsCodeSource.cs` | Only through the shim. `code --list-extensions` works on Linux too | Portable once moved | WPF project. TL-04 |
| `Services/ExtraSources/ScoopSource.cs`, `ChocolateySource.cs` | Windows-only tools | Windows only | WPF project |
| `Services/WingetService.cs`, `WingetTable.cs` | `winget.exe`. The parsing is plain code | Windows only | WPF project |
| `Services/RegistryInventoryService.cs` | `Microsoft.Win32.Registry` | Windows only | WPF project |
| `Services/RegistryNoiseFilter.cs` | None in code; registry-specific heuristics. The idea carries over to Linux's base-system filter | Windows only, for now | WPF project |
| `Services/AppxInventoryService.cs` | WinRT `PackageManager` | Windows only | WPF project |
| `Services/AppLinks.cs` | `ms-windows-store://` links, drive-letter-only folders, `winget.run` | Windows only. Linux gets Flathub links and `xdg-open` | WPF project |
| `Services/Lan/FirewallService.cs` | `netsh advfirewall`, relaunch with `runas` and `--configure-firewall` | Windows only. Linux gets its own (TL-11) | WPF project |
| `Services/Lan/NetworkProfiles.cs` | Network List Manager COM interop. `SelectLanNetworks` is plain code | Windows only. Linux has no Public/Private profile as such; firewalld zones are the nearest thing | WPF project |
| `Services/ThemeService.cs` | `DwmSetWindowAttribute` P/Invoke, WPF resources | Windows only | WPF project |
| `Services/WpfDialogService.cs` | WPF `MessageBox`, `Microsoft.Win32` file dialogs | Windows only | WPF project |
| `ViewModels/IDialogService.cs` | None | Portable as is | WPF project, moves with the view models (TL-05) |
| `ViewModels/CollectionDiff.cs` | None (`ObservableCollection` is in the base library) | Portable as is | WPF project, moves with the view models |
| `ViewModels/Commands.cs` | WPF `CommandManager.RequerySuggested` | Portable once moved | WPF project. TL-05 |
| `ViewModels/MainViewModel.cs` | `CollectionViewSource` / `ICollectionView` from `System.Windows.Data` | Portable once moved | WPF project. TL-05 |
| `ViewModels/NearbyViewModel.cs` | `Dispatcher` and three `DispatcherTimer`s; `FirewallService`; `NetworkProfiles`; an `ms-settings:` link | Needs an interface | WPF project. TL-05 |
| `ViewModels/InverseBooleanToVisibilityConverter.cs` | WPF `Visibility` | Windows only | WPF project |
| `App.xaml(.cs)`, `MainWindow.xaml(.cs)`, `Themes/*.xaml`, `AssemblyInfo.cs`, `app.manifest` | WPF, `ThemeInfo`, the Windows app manifest | Windows only | WPF project |

Also Windows-only, outside `src`: the `cli/` PowerShell scripts, `build.ps1`, `packaging/winget`, and both CI workflows (`windows-latest`).

---

## The manifest question (decision 4)

**Proposal only. Nothing here is implemented.**

### What a manifest looks like today

`schemaVersion` is 1. Each app has a single `id`, plus `source` and `installType` (`winget`, `msstore`, `download` or `manual`). An app has exactly one way to be installed, and it's a Windows one. Two facts about today's readers matter:

1. **Nothing checks `schemaVersion`.** It's written as 1 and never read back.
2. **Unknown properties are ignored.** `ManifestService.JsonOptions` and `LanProtocol.JsonOptions` don't set `UnmappedMemberHandling`, so System.Text.Json skips fields it doesn't know. That's how `matchConfidence` was added without breaking older builds.

### The change

Add an optional `packages` list to each app: every known way to install it, one entry per package manager. Add a top-level `platform`, and a neutral `osVersion` beside `windowsVersion`.

```json
{
  "schemaVersion": 2,
  "generatedAt": "2026-11-20T10:12:00+00:00",
  "generatedBy": "TeaChest",
  "sourceComputer": "OLD-PC",
  "platform": "windows",
  "windowsVersion": "Microsoft Windows NT 10.0.26100.0",
  "osVersion": "Microsoft Windows NT 10.0.26100.0",
  "stats": { "detected": 2, "selected": 2 },
  "apps": [
    {
      "name": "Mozilla Firefox",
      "id": "Mozilla.Firefox",
      "source": "winget",
      "installType": "winget",
      "version": "132.0",
      "include": true,
      "packages": [
        { "manager": "winget", "id": "Mozilla.Firefox" },
        { "manager": "flatpak", "id": "org.mozilla.firefox", "origin": "flathub", "mapping": "curated" }
      ]
    },
    {
      "name": "Paint.NET",
      "id": "dotPDN.PaintDotNet",
      "source": "winget",
      "installType": "winget",
      "include": true,
      "packages": [
        { "manager": "winget", "id": "dotPDN.PaintDotNet" }
      ]
    }
  ]
}
```

The same Firefox row saved on Ubuntu:

```json
{
  "name": "Firefox",
  "id": "",
  "source": "flatpak",
  "installType": "manual",
  "notes": "Installed with Flatpak.",
  "include": true,
  "packages": [
    { "manager": "flatpak", "id": "org.mozilla.firefox", "origin": "flathub", "scope": "user" }
  ]
}
```

### The rules

1. **Top-level `id`, `source` and `installType` keep their meaning: the Windows install.** A Linux-made manifest leaves `id` empty and `installType` as `manual`, with `source` naming the manager. TeaChest 1.3 on Windows then shows Linux apps as manual rows with a note, which is the truth, and never passes a Flatpak ID to WinGet.
2. **`packages` is optional.** A reader that finds none builds the list from the top-level fields (`winget` or `msstore` ID), so v1 manifests need no conversion.
3. **Each `packages` entry is validated by its manager**, in `ManifestSanitizer`, the same way `PackageIds` does for WinGet today. For example Flatpak IDs as reverse-DNS (`^[A-Za-z_][\w-]*(\.[A-Za-z_][\w-]*){2,}$`), Debian names as `^[a-z0-9][a-z0-9+.\-]+$`, and no leading `-` anywhere. An unknown `manager` is dropped with a warning when the manifest came from the network, and kept but never installed from a local file. At most 8 entries per app, at most one per manager.
4. **`mapping` says where a cross-OS entry came from:** absent means this PC's own scan saw it, `curated` means the TL-13 table. The UI shows curated rows as a guess worth checking, like a loose WinGet match today.
5. **No `--override`-style extras in `packages`.** Arguments stay in `wingetArgs` for WinGet only. Linux backends take no arguments from a manifest at all.

### When the version goes up

- **No bump for a field old readers can safely ignore.** That's how `matchConfidence` went in, and it stays the rule.
- **Bump when a field changes meaning, or when ignoring it would make an old reader do something wrong.**
- **`packages` is ignorable** as long as rule 1 holds, so strictly it needs no bump. I'd still write `schemaVersion: 2` from the first build that writes `packages`, so a newer reader knows the list is complete and authoritative rather than missing.
- **From the same change on, readers check the version.** A file with a higher `schemaVersion` than the reader knows still loads what it understands, gets a load warning ("This file was saved by a newer TeaChest. Some details may be missing."), and isn't re-saved over without asking.
- **Nearby needs no bump.** `PeerMessage.Inventory` is a `ManifestDocument`, unknown fields are ignored by 1.2 and 1.3, and `LanProtocol.Version` and the AppMigrate wire constants stay exactly as they are.

---

## The plan to a first Linux release

Cards in the same format as `TEACHEST-SPECS.md`. The "Rules for every task" from there apply to every card, except where a card says so. "Waits on" names the decision a card needs first; a card with none can start once TL-01 is merged.

### Stage 0: the split

#### TL-01: Split the portable code into TeaChest.Core

**Status:** done on `task/linux-core-split`, waiting for review.

**Why:** WPF can't build on Linux, so nothing Linux can start while the portable code lives in the WPF project. Every option for decisions 1 and 2 needs this.

**Files:** new `src/TeaChest.Core/`, new `tests/TeaChest.Core.Tests/`, `src/TeaChest/TeaChest.csproj`, `tests/TeaChest.Tests/TeaChest.Tests.csproj`, `TeaChest.sln`, `.github/workflows/build.yml`, the moved files, and the few callers that construct `InstallService`, `InstalledIndex` and `DeviceIdentity`.

**Do:** see "What TL-01 did" at the end of this file.

**Must pass:** every existing test, 511 or more in total, none deleted; Core and Core.Tests build on plain `net8.0` with `-p:EnableWindowsTargeting=false`; nothing in Core references `System.Windows`, `Microsoft.Win32`, `ProtectedData` or P/Invoke; the Linux CI job.

**Out of scope:** any behaviour change on Windows, namespaces, Avalonia.

### Stage 1: portable groundwork, no Linux code yet

#### TL-02: Per-platform package IDs in the manifest

**Waits on:** decision 4.

**Why:** manifests saved from now on should carry room for other platforms. It's the cheapest change to make early and the most annoying to make late.

**Files:** `src/TeaChest.Core/Models/Manifest.cs`, `src/TeaChest.Core/Services/ManifestService.cs`, `src/TeaChest.Core/Services/ManifestSanitizer.cs`, `src/TeaChest.Core/Models/PackageIds.cs`, new `tests/TeaChest.Core.Tests/ManifestPackagesTests.cs`

**Do:**
1. Add `PackageRef { Manager, Id, Origin, Scope, Mapping }` and `AppRecord.Packages` (nullable list), plus `ManifestDocument.Platform` and `OsVersion`.
2. Write `schemaVersion: 2` with `packages` filled from the top-level ID on Windows.
3. Validate entries per rule 3 above in `ManifestSanitizer`. Keep every existing check.
4. Warn on a `schemaVersion` above 2, per "When the version goes up".

**Must pass:** a v1 manifest loads unchanged; a v2 manifest round-trips; a v2 file deserialises with today's v1 `AppRecord` shape and gives the same top-level fields (proves old readers are fine); bad or unknown entries are dropped from a Network manifest and kept-but-inert from a local one; a flag-shaped ID is refused.

**Out of scope:** using `packages` for installs, any Linux manager, the curated table.

#### TL-03: Scan pipeline into Core

**Why:** the merge, dedupe and progress logic in `ScanService` is the same on every OS. Only the sources differ.

**Files:** `src/TeaChest/Services/ScanService.cs`, new `src/TeaChest.Core/Services/Scanning/IInventorySource.cs`, new `src/TeaChest.Core/Services/Scanning/ScanPipeline.cs`, new `tests/TeaChest.Core.Tests/ScanPipelineTests.cs`

**Do:** an `IInventorySource { Name; ScanAsync }` and an optional `IPackageResolver` for the name lookup step. Move `CollectAsync`, `MergeRange`, `DeduplicateById`, `Publish` and the resolve loop into `ScanPipeline`. `ScanService` becomes the Windows wiring: registry, AppX and WinGet as sources, WinGet as the resolver, `NameCache` as is. Keep every warning and status string word for word.

**Must pass:** every existing scan test, unchanged except for type names; a fake source and a fake resolver run through the pipeline on Linux.

**Out of scope:** any Linux source.

#### TL-04: Running tools on each OS

**Why:** `ExtraSourceTools.RunShimAsync` goes through `cmd.exe` and `FindOnPath` uses `;` and `PATHEXT`. Linux needs `:` and the executable bit, and no shell at all.

**Files:** `src/TeaChest/Services/ExtraSources/IExtraSource.cs`, `VsCodeSource.cs`, new `src/TeaChest.Core/Services/ToolRunner.cs`, new `tests/TeaChest.Core.Tests/ToolRunnerTests.cs`

**Do:** a Core `ToolRunner` with the same safe-token rule and timeout, which on Windows does exactly what `RunShimAsync` does today and on Unix runs the tool directly through `ProcessRunner` after a `PATH` lookup. Move `IExtraSource`, `Prepare` and `VsCodeSource` to Core. Scoop and Chocolatey stay on Windows.

**Must pass:** every existing source test; Unix `PATH` lookup tests that only run on Unix (skipped with a reason elsewhere); arguments are still constants only.

**Out of scope:** new sources.

#### TL-05: View models without WPF

**Waits on:** decision 1 (needed for options A and B, not for C or D).

**Why:** the view models are most of the app's behaviour, and only a handful of lines tie them to WPF.

**Files:** `src/TeaChest/ViewModels/*.cs`, `src/TeaChest/MainWindow.xaml.cs`, new `src/TeaChest.Core/Presentation/` (or a separate `TeaChest.Presentation` project, which the card should pick and say why), tests that construct view models.

**Do:** replace `CollectionViewSource` with a filtered list the view model owns; replace `Dispatcher` and `DispatcherTimer` with a small `IUiThread` (post, and a timer factory); give `RelayCommand` its own `CanExecuteChanged` instead of `CommandManager`; put `FirewallService`, `NetworkProfiles` and the `ms-settings:` link behind one `INearbyPlatform` interface. Then move the view models, `CollectionDiff` and `IDialogService` out of the WPF project. The WPF app must look and behave exactly as before.

**Must pass:** every existing view model test; a visual check of every screen on Windows by the coordinator.

**Out of scope:** Avalonia itself.

#### TL-06: Device key on Linux

**Why:** `DeviceIdentity` needs somewhere safe to keep its private key. DPAPI doesn't exist on Linux.

**Files:** new `src/TeaChest.Linux/LinuxKeyProtector.cs` (or wherever decision 5 puts Linux code), new tests.

**Do:** implement `IDeviceKeyProtector` as a file written with mode 0600 in `AppPaths.DataDirectory` (the same trust model as `~/.ssh`), named `device.pfx`, refusing to load if the file or folder is readable by others. Leave room for a Secret Service (keyring) version later; it isn't needed for v1.

**Must pass:** round trip; refuses a world-readable file; never touches `device.pfx.dpapi`.

**Out of scope:** keyring support.

### Stage 2: Linux backends

#### TL-07: Flatpak inventory

**Waits on:** decisions 3 and 5.

**Files:** new `src/TeaChest.Linux/Inventory/FlatpakSource.cs`, new tests.

**Do:** `flatpak list --app --columns=application,name,version,origin,installation`. Parse in a pure `internal static` method from canned output. Source `flatpak`, `installType` `manual` until TL-09 exists, `packages` filled per TL-02. Runtimes are already left out by `--app`. A missing `flatpak` is normal, not an error.

**Must pass:** canned output with user and system installs, odd columns, a missing tool.

#### TL-08: apt inventory and the base-system filter

**Waits on:** decisions 3 and 5.

**Files:** new `src/TeaChest.Linux/Inventory/AptSource.cs`, new `src/TeaChest.Linux/Inventory/BaseSystemFilter.cs`, new tests.

**Do:** start from `apt-mark showmanual`, subtract what the installer put there (the `/var/log/installer` or `/var/lib/apt/extended_states` baseline where it exists, otherwise the `Priority: required/important/standard` set from `dpkg-query`), then keep packages that own a `.desktop` file or a binary in `/usr/bin` that isn't a library tool. This is the TC-02 problem again, harder, so it needs a real-PC check and a reviewed list of expected results from at least two installs.

**Must pass:** canned outputs from a fresh Ubuntu, a fresh Debian, and a used developer machine, with expected keep and drop lists.

#### TL-09: Flatpak install backend

**Waits on:** decisions 3 and 5, and TL-02.

**Files:** new `src/TeaChest.Linux/Install/FlatpakBackend.cs`, new tests.

**Do:** implement `IInstallBackend`: the installed index from TL-07, and `flatpak install --user --noninteractive <origin> <id>`. Per-user, so no root. Exit-code and output classification in its own `Classify`, the way `InstallService.Classify` does WinGet today (that method moves behind the backend in this card).

**Must pass:** plan and report tests through a fake runner, the same way `InstallStatusTests` drives WinGet today.

#### TL-10: apt install backend

**Waits on:** decisions 3 and 5, and TL-02.

**Do:** `pkexec apt-get install -y <names>` in one batch, so there's one password prompt rather than forty. Names come only from validated `packages` entries. Report per package from apt's output. **Coordinator tests the real run** in a VM; tests never run apt.

#### TL-11: Nearby on Linux

**Waits on:** decision 5.

**Do:** check that discovery (UDP broadcast on 47820), the TLS session and pairing work between two Linux machines and between Linux and Windows. Detect ufw and firewalld and, when one is active and blocking, show the exact command to allow TeaChest rather than changing the firewall ourselves. No `netsh` equivalent runs silently. **Can't be fully tested on one PC; coordinator runs it on two.**

### Stage 3: the Linux UI

#### TL-12: Linux front end

**Waits on:** decision 1 and TL-05.

**Do:** under option B, an Avalonia app on the TL-05 view models with the same screens, the same theme tokens as `Dark.xaml` and `Light.xaml`, and the same wording. Adds the Avalonia packages, which this card is allowed to do. **Coordinator does the visual check.**

### Stage 4: cross-OS

#### TL-13: Curated WinGet-to-Flatpak table

**Waits on:** decision 2 and TL-02.

**Do:** a reviewed data file of WinGet ID to Flathub ID pairs, loaded into `packages` with `mapping: curated` when a Windows manifest is opened on Linux. Start small (the 50 most common apps), and keep every row human-checked. Show curated rows as "worth a quick check", like loose matches.

### Stage 5: shipping

#### TL-14: Packaging and CI per distro

**Waits on:** decisions 3 and 5.

**Do:** a Flatpak manifest first (`flatpak-spawn --host` for running apt and flatpak on the host, which needs a sandbox review), then an AppImage. CI jobs for Ubuntu and, later, Fedora containers.

#### TL-15: Linux release workflow

**Waits on:** TL-14 and the owner's go-ahead for a public Linux build.

**Do:** a tag-triggered draft release, the same shape as `release.yml`, attaching the Linux packages with checksums. Draft only; a person publishes it.

---

## What TL-01 did

**Projects.** `src/TeaChest.Core` targets plain `net8.0`, with `Nullable` and `ImplicitUsings` as the WPF project has them. `src/TeaChest` stays on `net8.0-windows10.0.19041.0` and references Core. Core gives `InternalsVisibleTo` to `TeaChest`, `TeaChest.Tests` and `TeaChest.Core.Tests`, since the WPF project and its tests use a few `internal` members (`ManifestService.JsonOptions`, `MatchConfidenceJsonConverter`, `PairingThrottle` and so on).

**Namespaces stay as they were** (`TeaChest.Models`, `TeaChest.Services`, `TeaChest.Services.Lan`), to keep the diff to moves. Only the assembly changes. `MainWindow.xaml` only maps `TeaChest.ViewModels`, which didn't move, so no XAML changed.

**Moved to Core as is:** `Models/*` (all six files), `AppPaths`, `ManifestService`, `ManifestSanitizer`, `WingetConfigurationWriter`, `ProcessRunner`, and in `Lan`: `LanProtocol`, `Discovery`, `Pairing`, `PairingThrottle`, `PeerClient`, `PeerServer`, `InventoryDiff`.

**Moved with a seam:**
- `InstallService` takes an `IInstallBackend` (the installed index, and installing one app). `WingetInstallBackend` in the WPF project does what the default constructor did before: `WindowsInstalledIndex.BuildAsync` and `WingetService.InstallAsync`. The test constructor that takes just an install function is kept, now `internal`; a plan built through it sees an empty installed index, and no test builds one that way. `Classify` (the WinGet exit codes) stays in `InstallService` for now; TL-09 moves it behind the backend.
- `InstalledIndex` keeps its sets and `Contains`. Building one from the registry, AppX and WinGet moved to `WindowsInstalledIndex` in the WPF project, unchanged.
- `DeviceIdentity.LoadOrCreate` takes an `IDeviceKeyProtector`. `DpapiKeyProtector` in the WPF project does exactly what the inline DPAPI calls did, with the same `device.pfx.dpapi` file name, so existing pairings keep working.

**Stayed in the WPF project:** everything in the "Windows only" rows above, plus `ScanService`, `NameCache`, `ExtraSources`, and the view models, which have their own cards.

**Tests.** The Core tests live in `tests/TeaChest.Core.Tests` on plain `net8.0`, with the same xUnit packages and versions as `TeaChest.Tests`, and the same `TeaChest.Tests` namespace.
- Whole files moved: `AppPathsTests`, `ClassifierTests`, `ManifestDiffTests`, `ManifestTests`, `PairingThrottleTests`, `TextUtilTests`, `WingetConfigurationTests`, and `LanTests`.
- `LanTests.cs` held two classes that need Windows. `NetworkProfileTests` and `InventoryDiffTests` (which builds its index from WinGet rows) went to a new `tests/TeaChest.Tests/LanWindowsTests.cs`, unchanged.
- `InstallTests.cs` held `InstallClassificationTests`, which only needs Core. It moved to `tests/TeaChest.Core.Tests/InstallClassificationTests.cs`, unchanged. `InstalledIndexTests` stayed.
- The only edits to existing tests are `InstalledIndex.Build(` becoming `WindowsInstalledIndex.Build(` in five places.
- New: `DeviceIdentityTests` and `InstallBackendTests` in Core, `DpapiKeyProtectorTests` on Windows, for the two new seams.
- Count: 511 before; 164 in Core and 355 on Windows after, 519 in all. Nothing deleted or skipped.

**CI.** `build.yml` has a new `core-linux` job on `ubuntu-latest` that restores, builds (with `-p:EnableWindowsTargeting=false`) and tests `TeaChest.Core.Tests`, which builds Core with it. The Windows job is unchanged and now runs both test projects through the solution. The Linux job hasn't run yet.

**Side effect.** `build.ps1` zips the whole publish folder, so release zips gain a 40 KB `TeaChest.Core.pdb` next to the existing `TeaChest.pdb`. The exe is still a single file with Core inside it.
