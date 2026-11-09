# TeaChest: carrying app settings

**Written:** 8 Nov 2026, on `research/settings-discovery` (from `main` at `f9c5fab`)
**Status:** draft. Every choice marked **"Proposed, the user decides"** is a proposal and isn't settled until I say so. Decisions S1 to S11 in section 6 were settled on 9 Nov 2026, each as recommended. The approach (section 1), the recipe format and the Nearby design are still proposals. The research behind this is in `RESEARCH.md` in this folder, and the prototype is `tools/SettingsProbe`.

TeaChest moves apps, not their settings. Owner decision 1 in the spec sheet ("Settings over Nearby") needs a per-app recipe of paths, opt-in per item, and a protocol change. This file covers how TeaChest finds those paths, what a recipe looks like, how it looks in the app, the security design, and a staged plan as cards.

---

## 1. The approach

**Proposed, the user decides.**

| Layer | What it is | Trust | When it's used |
|---|---|---|---|
| **1. Recipes** | A small database of hand-checked recipes, shipped with TeaChest, one per app, matched by WinGet ID, Store family or name | High: someone checked it on a real PC | Always, for the apps it covers |
| **2. Heuristics** | Known folders plus the vendor and product names TeaChest already reads, and the AppX package folder for Store apps | Medium: shown as "probably this app's" | For installed apps with no recipe |
| **3. Learn mode** | Watch what an app writes while the user opens it and changes a setting, then suggest a draft recipe | Medium to high for that PC | Only when the user asks, per app |

The "what not to carry" rules sit underneath all three, and a recipe can't override them.

**Why this order:**

- **Recipes are the only thing that knows *intent*.** Watching tells you that OBS wrote `service.json`; only a recipe knows that file holds your stream key and must never leave the PC. The same goes for VS Code's `globalStorage` (sign-in state) next to its `settings.json` (what you want).
- **Heuristics are free and cover the long tail,** but they guess. They're good for showing "these folders look like Contoso's, 12 MB", and bad at deciding what inside them is safe.
- **Watching can't be the backbone.** The change journal has no process IDs, needs admin, and on the development PC held **under an hour** of history (section 2). ETW has process IDs but needs admin, delivers every process's file activity to the consumer even with a PID filter, and outlives a crash. So watching is an opt-in tool for writing recipes, not something TeaChest runs in the background.
- **This is the shape Microsoft settled on too.** UE-V ships settings location templates (recipes) and a generator that watches an app run to draft new ones (learn mode).

**What it changes about the README's "smart file explorer" idea (owner decision 2):** the `PeerClient.cs` TODO hopes the change journal could tell us which folders someone uses most. It can't, at least not without TeaChest running a background task that keeps its own summary, because the journal only reaches back hours. I'd drop the journal from that idea. Whatever decision 2 becomes, it should rest on known folders and recent file times, not the journal.

---

## 2. Results on a real PC

The prototype, `tools/SettingsProbe`, ran on the development PC (Windows 11, build 26300, elevated prompt). It starts a stand-in app that writes the kinds of files real apps write into a scratch folder, reads the change journal (and ETW) around the run, and prints only what happened under that folder. The scratch folder was deleted after each run, and `logman query -ets` showed no session left behind.

**The journal on that PC:** maximum size 32 MB, allocation delta 8 MB, and the oldest record was **0.8 hours** old.

**What the journal attributed, by time window and place, from one run:**

```text
Window: 181 records (21.2 KB of journal).
Kept 37 under the scratch folder; skipped 144 elsewhere on the drive (not shown, never resolved).
  file Contoso\Notepadish\settings.json         created, written, renamed    9 records
  file Contoso\Notepadish\Profiles\default.ini  created, written             3 records
  file Contoso\Notepadish\Cache\thumb-001.bin   created, written             3 records
  file Contoso\Notepadish\logs\app.log          created, written             5 records
  file Contoso\Notepadish\token.json            created, written             3 records
  file Contoso\Notepadish\recover.tmp           created, written, deleted    4 records

== Learned recipe (attributed by time window + place) ==
  Offer      Contoso\Notepadish\Profiles\default.ini  (written by the app)
  Offer      Contoso\Notepadish\settings.json  (written by the app)
  Exclude    Contoso\Notepadish\Cache\thumb-001.bin  (cache, log or temp folder)
  Exclude    Contoso\Notepadish\logs\app.log  (cache, log or temp folder)
  NeverCopy  Contoso\Notepadish\token.json  (credentials file)
  Transient  Contoso\Notepadish\recover.tmp  (deleted again)
```

**What worked:**

- **Reading the journal from a noted USN and resolving paths, without ever resolving anything outside the folder.** The filter follows the folder's own file ID and adds subfolders as they're created, so a path is only ever built from names inside the scope.
- **Following a file across renames.** The stand-in saves settings the safe way (write `settings.json.new`, rename it over `settings.json`). The journal tracks the file ID, so it shows up once, under its final name.
- **Turning what was written into a recipe** with the "never carry" rules: the cache and log were excluded, the token file was refused, and the temp file was recognised as transient.
- **ETW attribution by process ID** was exact: 175 events from the stand-in, with bytes written per file (the 256 KB cache file stands out at once), the delete, and the rename.

**What didn't, or only partly:**

- **The journal can't say who.** In that window, 144 of 181 records came from other processes. Place (the folder) did all the attributing. On a real PC that means learn mode has to know which folders to look at, which is exactly what recipes and heuristics give it.
- **ETW's PID filter is ignored** by the kernel file provider. `EnableTraceEx2` accepted it, and the session still delivered 10,000 to 33,000 events from other processes in half a second. The probe dropped them unread, but TeaChest would be holding every process's file names in memory while learning.
- **ETW sees the name at open time,** so the atomic save shows up as writes to `settings.json.new` plus a rename. Joining that back to `settings.json` is extra work the journal gets for free.
- **ETW also sees the app's own runtime:** 124 events from the stand-in were outside the scratch folder (DLLs, the .NET runtime).
- **The unprivileged journal read returns the same records with every name blank,** on both the volume handle and a plain folder handle. From a standard-user token it's untested, because the probe ran elevated.

**Unit tests:** 58, all passing, on canned bytes only: a hand-written V2 record, V3 records with 128-bit IDs, unknown versions, broken lengths, the scope filter (renames, moves in and out, folders created inside, blank names), the "never carry" rules, the ETW payload layouts for versions 0 and 1 and both pointer sizes, NT path scoping, and a check that the tool can't send the control codes that create or delete the journal.

---

## 3. Recipes

### The format

**Proposed, the user decides.** JSON, because TeaChest already reads and writes JSON with `System.Text.Json`, so no new package. One file per app, shipped as embedded resources in `TeaChest.Core`.

```json
{
  "recipeVersion": 1,
  "id": "vscode",
  "name": "Visual Studio Code",
  "match": {
    "wingetIds": ["Microsoft.VisualStudioCode"],
    "storeFamilies": [],
    "displayName": "^Microsoft Visual Studio Code( \\(User\\))?$"
  },
  "checked": { "appVersion": "1.10x", "on": "2026-11-08", "notes": "Settings Sync covers most of this if it's on." },
  "items": [
    {
      "id": "settings",
      "label": "Settings and keyboard shortcuts",
      "root": "{RoamingAppData}/Code/User",
      "include": ["settings.json", "keybindings.json", "tasks.json", "snippets/**", "profiles/*/settings.json", "profiles/*/keybindings.json"],
      "action": "copy",
      "default": true
    },
    {
      "id": "state",
      "label": "Editor state and sign-ins",
      "root": "{RoamingAppData}/Code/User",
      "include": ["globalStorage/**", "workspaceStorage/**", "History/**"],
      "action": "never",
      "why": "Holds sign-in tokens (encrypted for this PC) and per-project state that points at this PC's folders."
    }
  ],
  "exclude": ["**/Cache/**", "**/CachedData/**", "**/logs/**", "**/Backups/**"]
}
```

**Fields:**

- **`match`:** how a recipe finds its app in a scan: WinGet IDs first, then Store package families, then a name pattern as a last resort. One recipe can match several IDs (stable and Insiders builds, say).
- **`root`:** always starts with a **token**, never a literal path. The tokens are `{RoamingAppData}`, `{LocalAppData}`, `{LocalAppDataLow}`, `{Documents}`, `{SavedGames}`, `{Profile}` (the user's home folder), and `{PackageData:<family>}` (a Store app's `%LOCALAPPDATA%\Packages\<family>`). Each one resolves through the known-folder API on each PC, so a redirected Documents folder still works. `{ProgramData}` is left out on purpose for now: it's machine-wide and usually licences or caches.
- **`include` / `exclude`:** globs relative to the root, `/` as the separator. `**` matches any depth.
- **`action`:**
  - `copy`: offered, and replaces the new PC's file (after a backup, see "Restoring").
  - `copy-if-missing`: offered, but only written when the new PC has no such file yet.
  - `offer-unticked`: offered but not ticked by default (open-file history, window positions, anything that mentions the old PC's paths).
  - `never`: shown greyed out with its `why`, so people see that TeaChest knows about it and chose not to move it.
- **`kind`** (optional, default `files`): `registry` for `HKCU` keys. Registry items need their own decision (decision S8 below), so recipes can describe them but nothing acts on them yet.
- **`prefer`** (optional): the app's own export or sync, when it has one and it's better ("PowerToys has Backup & restore in its General settings").

**Rules the loader enforces:** a root must start with a known token; no `..`, absolute paths, drive letters, UNC paths or environment variables anywhere; globs can't escape the root; `id`s are `[a-z0-9-]+`. A recipe that breaks a rule is dropped with a warning, the same way `ManifestSanitizer` treats bad manifest entries. The global "never carry" rules from `RESEARCH.md` section 5 apply on top, so a recipe saying `copy` on `**/Cookies` is overruled.

**Merging** (combining old and new files key by key) is tempting for `.gitconfig` and JSON settings, but it's where subtle breakage lives. v1 has no merge action: `copy` keeps a backup of what it replaced, and `copy-if-missing` covers the "don't clobber" case. **Decided by the user on 9 Nov 2026** (decision S2).

### Examples

Paths are as the apps document them, and each was checked against public docs, but **untested on a second PC**. Every recipe needs a real round trip before it ships (card TS-03).

| App | Match | Carry | Never carry | Notes |
|---|---|---|---|---|
| **VS Code** | `Microsoft.VisualStudioCode` | `{RoamingAppData}/Code/User`: `settings.json`, `keybindings.json`, `tasks.json`, `snippets/**`, `profiles/*/…` | `globalStorage`, `workspaceStorage`, `History`, `Backups`, `CachedData` | Extensions already come across through TC-19's VS Code source. Settings Sync does this better if it's on |
| **Windows Terminal** | `Microsoft.WindowsTerminal`, family `Microsoft.WindowsTerminal_8wekyb3d8bbwe` | `{PackageData:Microsoft.WindowsTerminal_8wekyb3d8bbwe}/LocalState/settings.json`; the unpackaged build uses `{LocalAppData}/Microsoft/Windows Terminal/settings.json` | `state.json` (window state), `Settings/settings.dat` | Profiles can point at the old PC's paths (starting folders, icons). `copy`, with a note |
| **Git** | `Git.Git` | `{Profile}/.gitconfig`, `{Profile}/.config/git/config`, `{Profile}/.config/git/ignore` | `{Profile}/.git-credentials` | `.gitconfig` can hide a token in a `url.…insteadOf` line, so it goes through the secret check (section 5). `copy-if-missing` |
| **OpenSSH client** | Always (it ships with Windows) | `{Profile}/.ssh/config` `copy`, `known_hosts` `offer-unticked` | `id_*`, `*.pem`, `*.key`, `authorized_keys` | Private keys never move through TeaChest. The UI says so in plain words |
| **Notepad++** | `Notepad++.Notepad++` | `{RoamingAppData}/Notepad++`: `config.xml`, `shortcuts.xml`, `contextMenu.xml`, `stylers.xml`, `langs.xml`, `userDefineLangs/**`, `themes/**`, `plugins/config/**` | `backup/**` (unsaved text) | `session.xml` lists the old PC's open files: `offer-unticked`. A portable install (`doLocalConf.xml` beside the exe) keeps settings in its own folder, which this recipe doesn't cover |
| **OBS Studio** | `OBSProject.OBSStudio` | `{RoamingAppData}/obs-studio`: `basic/scenes/**`, `basic/profiles/**` (except below), `global.ini` / `user.ini` | `basic/profiles/*/service.json` (**stream key**), `logs`, `crashes`, `profiler_data`, `plugin_config/**/cache` | The best example of why recipes matter: the folder is clearly "settings", and one file inside it is a secret |
| **PowerToys** | `Microsoft.PowerToys` | `{LocalAppData}/Microsoft/PowerToys`: `settings.json` and each module's `settings.json` | `Logs`, `Updates`, `*.log` | `prefer`: PowerToys' own Backup & restore |
| **7-Zip** | `7zip.7zip` | `HKCU\Software\7-Zip` (`kind: registry`) | none | Waits on decision S8. Until then the recipe exists but is shown as "not carried yet" |
| **PuTTY** | `PuTTY.PuTTY` | `HKCU\Software\SimonTatham\PuTTY\Sessions` (`kind: registry`), `SshHostKeys` `offer-unticked` | Private key files (`*.ppk`) | Waits on decision S8 |
| **Firefox** | `Mozilla.Firefox` | nothing | `{RoamingAppData}/Mozilla/Firefox/Profiles/**` | `prefer`: Firefox Sync. A browser profile holds cookies, saved passwords and history, so it's `never` for every browser |

---

## 4. In the app

**Decided by the user on 9 Nov 2026** (decision S1 covers whether settings are in scope at all, and in which release).

### On the old PC (Collect)

1. After a scan, apps with a recipe or a heuristic match get a small **Settings** count in the grid ("3 items, 1.2 MB").
2. The details panel (TC-16) gets a **Settings** section for the selected app: one row per recipe item, with its label, size, and a tick box. Recommended items start ticked. `offer-unticked` items start unticked with a one-line reason. `never` items are greyed out with their `why` ("Holds your stream key, so it stays on this PC").
3. Heuristic matches show as **"Probably Contoso's"** with the folder and size, unticked, and a link to "Learn this app's settings" (learn mode, once it exists).
4. Anything the secret check flags (section 5) shows a warning and can't be ticked in v1.
5. Nothing is offered over Nearby until the user turns on **Share settings too** while sharing. It's off by default and turns itself off when sharing stops.

### On the new PC (Install)

1. After the apps install, an extra step: **"Bring their settings across?"**, listing what the old PC offered, grouped by app, with sizes and the same ticks.
2. Settings are restored **after the app is installed and before it's first opened**, which is what USMT recommends too, because some apps overwrite settings on first run. If the app is already running, that item waits and says so.
3. Anything replaced is backed up first (to `%LOCALAPPDATA%\TeaChest\settings-backup\<date>\`), and the summary links to it.
4. The report files get a `settings` section: what came across, what was skipped and why. Contents are never logged, only item labels.

### Learn mode (later, and only if decision S5 says so)

On the old PC: **"Learn this app's settings"** → "Open Contoso, change a setting or two, then close it" → TeaChest watches the app's likely folders (from heuristics) and the known roots, unelevated, with `FileSystemWatcher`, for that session only → it shows a draft recipe with the same Offer / Exclude / Never verdicts the prototype printed. The draft stays on that PC. It's used for this migration and, if the user wants, saved locally. Nothing is ever sent anywhere automatically.

---

## 5. Security design (owner decision 1)

**Proposed, the user decides**, all of it. The README promises Nearby is "read-only, the app list only", and settings break the second half of that promise. This is the design I'd put behind a new one: *"read-only, the app list plus the settings you pick"*.

### What can go wrong

- **A paired peer reads more than the user meant.** Today a paired peer can read the app list. With settings, a peer that's paired (or a stolen pairing key) could ask for files.
- **A malicious "old PC" sends crafted files** to the new PC: path traversal on restore, executables dressed as settings, huge files, or settings that point at something hostile (a `settings.json` that changes the terminal's command line).
- **Secrets leave the PC** inside ordinary-looking files.
- **A bad recipe** (edited on disk, or a future downloaded one) points at something it shouldn't.

### What a peer may read

1. **Only items the old PC's user ticked, in this sharing session.** The sharing PC builds an **offer**: a list of `(appId, itemId, label, size, sha256)` from its own recipes and ticks. The peer can ask for the offer and then for items **by ID from that list**. It never sends a path, a glob or a folder name, and the server never resolves one from peer input.
2. **Files are read at offer time, not request time.** The server snapshots the ticked files into memory or a temp folder under `%LOCALAPPDATA%\TeaChest` when the user confirms, and serves those bytes. A file changed or swapped for a link afterwards isn't what gets sent.
3. **No links.** Reparse points (symlinks, junctions) inside a recipe root are skipped, so a recipe root can't be used to reach elsewhere.
4. **Limits:** 50 MB per item and 200 MB per session (**proposed, the user decides**), files sent in chunks under `LanProtocol.MaxFrameBytes`.
5. **The offer ends when sharing stops,** or after the existing 30-minute idle timeout, whichever comes first.
6. **Read-only stays true:** the peer can't write anything to the sharing PC.

### Opt-in

- **Off by default.** "Share settings too" is a separate switch from "Share this PC".
- **Twice, on purpose:** the old PC chooses what to *offer*, the new PC chooses what to *take*. Neither side's ticks decide for the other.
- **A confirm dialog on the old PC** lists every offered item and its size before the offer exists: "The other PC will be able to copy these 7 items (3.4 MB). Nothing else."
- **Pairing doesn't imply settings.** A remembered pairing skips the 6-digit code for the app list, but settings always need the switch turned on in that session.

### The protocol

The spec sheet forbids changing `LanProtocol.Version`, because 1.3 has to keep talking to 1.2. Two options:

| Option | What it means | Cost |
|---|---|---|
| **A. Negotiate a capability** (recommended) | Add an optional `capabilities` list to the `hello` message (`["settings/1"]`). Old builds ignore unknown JSON fields, so 1.2 peers carry on as now. New message types (`get-settings-offer`, `settings-offer`, `get-settings-item`, `settings-chunk`) are only sent after both hellos list the capability | Small. Keeps 1.2 working. `WireCompatibilityTests` stays green, and gets a new test that a 1.2-shaped hello still works |
| **B. Bump the version to 2** | Version 2 peers refuse version 1 | Breaks 1.2 compatibility, which is one of 1.3's headline promises |

**Decided by the user on 9 Nov 2026** (decision S4): A.

### On the receiving side

The new PC treats everything from the peer as untrusted, the same way `ManifestSanitizer` treats manifests:

- **Destinations come from the receiving PC's own recipe.** The peer's offer says "vscode/settings, `settings.json`". The receiver looks up its own copy of the `vscode` recipe, resolves `{RoamingAppData}` itself, and checks the file name against that item's `include` globs. An item it has no recipe for, a name that doesn't match, a name containing `..`, `:`, a drive, a separator outside the glob, or a reserved device name (`CON`, `NUL` and so on) is refused.
- **Executable types are refused** even if a recipe matches them: `.exe`, `.dll`, `.ps1`, `.bat`, `.cmd`, `.vbs`, `.js` outside known JSON names, `.lnk`, `.url`, `.scr`, `.msi`. Plugin folders aren't settings.
- **Each file is checked against its SHA-256 from the offer,** written to a staging folder first, then moved into place after the existing file is backed up.
- **Restored files are listed in the summary,** so if a setting does something unwanted the user knows where it came from.
- **Settings never go into `teachest.json`.** Manifests travel by USB stick and email, so they stay app lists. Carrying settings by file is a separate decision (S3).

### Secret detection

On the **sharing side**, before anything goes into an offer. Three layers, all local, all pure functions so they can be tested on canned strings:

1. **Names:** the "never carry" list in `RESEARCH.md` section 5 (`Cookies`, `Login Data`, `Local Storage`, `.git-credentials`, `id_*`, `*.pem`, `*.key`, `*.pfx`, `*.kdbx`, `credentials*`, `token*`, `*.lic` and so on).
2. **Content, for text files under 1 MB:**
   - PEM blocks (`-----BEGIN … PRIVATE KEY-----`, `-----BEGIN OPENSSH PRIVATE KEY-----`).
   - Well-known token shapes: AWS access keys (`AKIA…`), GitHub tokens (`ghp_`, `gho_`, `github_pat_`), Slack (`xox[abprs]-`), Google API keys (`AIza…`), OpenAI and Anthropic keys (`sk-…`, `sk-ant-…`), Azure storage connection strings (`AccountKey=`), JWTs (`eyJ….eyJ….`).
   - Credentials in URLs (`https://user:password@…`, which is how a token hides in `.gitconfig`).
   - JSON, INI or YAML keys named like a secret (`password`, `passwd`, `secret`, `token`, `apikey`, `api_key`, `auth`, `key`, `streamkey`) with a non-empty string value longer than 8 characters.
3. **Machine-bound data:** a DPAPI blob header (`01000000D08C9DDF0115D1118C7A00C04FC297EB`, raw or base64 as `AQAAANCMnd8BFdERjHoAwE/Cl+s`) anywhere in the file means it won't decrypt on another PC, and it's often a password.

A flagged file shows as **"Looks like it holds a password or key"**. In v1 it can't be offered at all (**Decided by the user on 9 Nov 2026**, decision S9). The check is a safety net, not a guarantee, and the UI shouldn't claim otherwise.

---

## 6. Decisions

The user accepted every recommendation below as written on 9 Nov 2026, so the last column is now the decision.

| # | Decision | Options | Decided (as recommended) |
|---|---|---|---|
| S1 | Is carrying settings in scope, and when? | Next minor release / later / never | Ship the read-only **preview** first (TS-01 to TS-05): it's useful on its own ("these are the settings you'd lose") and risks nothing. Carrying comes after |
| S2 | What happens when the new PC already has the file? | Replace with backup / skip / merge | **Replace with a backup**, plus `copy-if-missing` per recipe. No merging in v1 |
| S3 | Carry settings by file (USB) as well as Nearby? | Nearby only / also an encrypted bundle / also a plain folder | **Nearby only** first. A file bundle can walk off on a USB stick, so if it comes later, encrypt it with a passphrase |
| S4 | Protocol change | Capability negotiation / version bump | **Capability negotiation**, keeping 1.2 working |
| S5 | Learn mode | None / unelevated `FileSystemWatcher` / plus an elevated journal or ETW helper | **Unelevated watcher only**, if at all. Revisit the journal only if the unprivileged read turns out to work from a standard-user token |
| S6 | An elevated helper that reads the journal | Yes / no | **No.** It would see every user's activity on the volume, for very little gain over the watcher |
| S7 | Game saves | Point people at Ludusavi / optional fetch of Ludusavi's manifest / nothing | **Point at Ludusavi** in the UI and README. Its data comes from PCGamingWiki under CC BY-NC-SA 3.0, which doesn't sit comfortably inside an MIT app |
| S8 | Registry settings (7-Zip, PuTTY) | Not in v1 / export and import whitelisted `HKCU` keys | **Not in v1.** Importing registry keys is a bigger trust step than copying files, and the spec sheet keeps implementers away from the registry |
| S9 | Files the secret check flags | Never sendable / sendable after an explicit "send anyway" | **Never sendable in v1** |
| S10 | The README's Nearby promise | Keep "app list only" until carrying ships / change it with the release | Change it **with** the release that ships TS-08 to TS-10, to "read-only, the app list plus the settings you pick" |
| S11 | Where recipes come from | Shipped only / shipped plus a signed download / plus community | **Shipped only**, in the exe. A download is a new channel to secure. Community recipes would come through pull requests |

---

## 7. The plan, as cards

Cards in the same format as `TEACHEST-SPECS.md`. The "Rules for every task" from there apply to every card. "Waits on" names the decision a card needs first. Cards in Stage 1 change nothing on the wire and write nothing outside TeaChest's own folder.

### Stage 1: the read-only preview

#### TS-01: Recipe model, path tokens and loader

**Waits on:** S1.

**Why:** everything else reads recipes, and the loader is where untrusted recipe data gets checked, the same job `ManifestSanitizer` does for manifests.

**Files:** new `src/TeaChest.Core/Settings/Recipe.cs`, new `src/TeaChest.Core/Settings/RecipeLoader.cs`, new `src/TeaChest.Core/Settings/PathTokens.cs`, new `tests/TeaChest.Core.Tests/RecipeLoaderTests.cs`

**Do:**
1. Records for the format in section 3: `Recipe`, `RecipeMatch`, `RecipeItem` (with `Action` as an enum: `Copy`, `CopyIfMissing`, `OfferUnticked`, `Never`) and `Kind` (`Files`, `Registry`).
2. `internal static Recipe? RecipeLoader.Parse(string json, List<string> warnings)`. Enforce every rule in "Rules the loader enforces". A bad item drops that item, and a bad root or `id` drops the recipe.
3. `PathTokens` turns a token root into a real path through an injected `Func<string, string?>` for each known folder, so tests never touch the real profile. The Windows wiring (`SHGetKnownFolderPath`) goes in the WPF project.
4. A glob matcher (`*`, `**`, `?` only), `internal static`, case-insensitive, `/` and `\` both accepted on input.

**Must pass:** the VS Code example from section 3 round-trips; `..`, `C:\`, `\\server\share`, `%APPDATA%`, an unknown token, an uppercase `id`, and a glob that climbs out of the root are each refused with a warning; `**` matches at any depth and `*` doesn't cross `/`; a missing `checked` block is fine.

**Out of scope:** reading files, the UI, registry items.

#### TS-02: The "never carry" rules and the secret check

**Waits on:** S1.

**Why:** these rules overrule every recipe, so they need to be one well-tested piece of code that everything calls.

**Files:** new `src/TeaChest.Core/Settings/NeverCarry.cs`, new `src/TeaChest.Core/Settings/SecretScanner.cs`, new `tests/TeaChest.Core.Tests/SecretScannerTests.cs`

**Do:**
1. `internal static (bool Blocked, string Reason) NeverCarry.Check(string relativePath)` with the name rules from `RESEARCH.md` section 5 (the prototype's `LearnedRecipe` rules are a good start).
2. `internal static SecretFinding? SecretScanner.Scan(ReadOnlySpan<byte> content, string fileName)` for the three layers in section 5. Text only: skip anything with NUL bytes in the first 8 KB, and anything over 1 MB.
3. Finding reasons are plain English for the UI ("Looks like a GitHub token"). **Never include the matched text in a reason, log or exception.**

**Must pass:** a `[Theory]` per pattern with a made-up but correctly shaped value; `.gitconfig` with `url.https://user:tok@github.com/.insteadOf` is flagged; an OBS `service.json` with `"key": "live_…"` is flagged; a normal VS Code `settings.json` isn't; `"password": ""` isn't; a DPAPI header, raw and base64, is flagged; a file named `Logbook\entries.json` isn't blocked by the `logs` rule; a binary file isn't scanned.

**Out of scope:** entropy scoring (worth a later look, but noisy), scanning registry values.

#### TS-03: The first ten recipes

**Waits on:** S1.

**Why:** the recipes are the backbone, and each one has to be right on a real PC, not just on paper.

**Files:** new `src/TeaChest.Core/Settings/Recipes/*.json` (embedded resources), `src/TeaChest.Core/TeaChest.Core.csproj` (the embed line), new `tests/TeaChest.Core.Tests/BuiltInRecipesTests.cs`

**Do:** the ten apps in section 3, in the format from TS-01. Registry items are written but marked `Kind = Registry`. Fill `checked` with the app version actually tested.

**Must pass:** every built-in recipe loads with no warnings; no two recipes share an `id` or a WinGet ID; every `never` item has a `why`; every `copy` item's include globs pass `NeverCarry.Check` for their sample names.

**Needs a human:** each recipe needs a round trip by the coordinator (install the app on a test PC or VM, change a setting, carry the files, check it took). Say which ones were and weren't in the report. **Never on the owner's own profile.**

**Out of scope:** more apps, game saves.

#### TS-04: Finding settings on this PC

**Waits on:** S1.

**Why:** turns recipes and heuristics into "these files, this big" for each scanned app, read-only.

**Files:** new `src/TeaChest.Core/Settings/SettingsFinder.cs`, new `src/TeaChest.Core/Settings/HeuristicMatcher.cs`, new `src/TeaChest/Services/KnownFolders.cs`, new `tests/TeaChest.Core.Tests/SettingsFinderTests.cs`

**Do:**
1. `SettingsFinder` takes the scanned `AppItem`s, the recipes and an `IFileSystemView` (enumerate, size, is-reparse-point), and returns per app a list of found items with sizes and verdicts. It never reads file contents except through TS-02's scanner, and never follows reparse points.
2. `HeuristicMatcher`: for apps with no recipe, look for a folder named like the normalised product or publisher (legal suffixes stripped) directly under `{RoamingAppData}`, `{LocalAppData}` and `{Documents}`, or as `<Publisher>/<Product>`. A publisher-only match under a shared vendor folder (`Microsoft`, `Google`, `Adobe`) doesn't count. Store apps use `{PackageData:<family>}/LocalState`.
3. `KnownFolders` is the Windows wiring for TS-01's token resolver.
4. Cap the walk: 10,000 files or 2 seconds per app, then report "too big to list".

**Must pass:** against a fake file system: recipe items found and sized; `never` items reported but not offered; a junction inside a root isn't followed; the heuristic finds `Roaming/obs-studio` for "OBS Studio" and doesn't match "Microsoft Teams" to `Roaming/Microsoft`; a Store app's `LocalState` is found by family; the caps hold. No test touches the real profile.

**Out of scope:** UI, copying anything.

#### TS-05: Show settings in the details panel

**Waits on:** S1, TS-04.

**Files:** `src/TeaChest/MainWindow.xaml`, `src/TeaChest/ViewModels/MainViewModel.cs`, new `src/TeaChest/ViewModels/SettingsItemViewModel.cs`, new `tests/TeaChest.Tests/SettingsPreviewTests.cs`

**Do:** the "On the old PC" steps 1 to 4 from section 4, read-only: counts in the grid, the Settings section in TC-16's panel with labels, sizes, ticks (which don't do anything yet beyond being remembered for the session), greyed `never` rows with their reasons, and "Probably Contoso's" heuristic rows. Plain warm wording, theme brushes only.

**Must pass:** view model tests for default ticks per action, the secret flag making a row unselectable, and sizes formatted as the rest of the app does. **Needs a visual check** in both themes.

**Out of scope:** Nearby, restoring.

### Stage 2: restoring

#### TS-06: The restore engine

**Waits on:** S2, TS-04.

**Why:** the one place that writes settings on the new PC, so it's where path safety, backups and staging live.

**Files:** new `src/TeaChest.Core/Settings/SettingsRestorer.cs`, new `tests/TeaChest.Core.Tests/SettingsRestorerTests.cs`

**Do:** given a recipe item, a relative file name, its bytes and an expected SHA-256: check the name against the receiving recipe (section 5, "On the receiving side"), refuse executable types, verify the hash, write to a staging folder, back up any existing file to `settings-backup\<date>\`, then move into place. Return a result per file for the report. Refuse while the app's process is running (injected check).

**Must pass:** in a temp folder: a normal restore with backup; `copy-if-missing` leaves an existing file alone; `..\x`, `C:\x`, `CON`, `a:b`, `x.exe` and a name outside the item's globs are refused; a wrong hash writes nothing; the staging folder is empty afterwards in every case.

**Out of scope:** registry, merging, the network.

### Stage 3: over Nearby (owner decision 1)

#### TS-07: Negotiate the settings capability

**Waits on:** decision 1, S4.

**Files:** `src/TeaChest.Core/Services/Lan/LanProtocol.cs`, `src/TeaChest.Core/Services/Lan/PeerClient.cs`, `src/TeaChest.Core/Services/Lan/PeerServer.cs`, new `tests/TeaChest.Core.Tests/CapabilityTests.cs`

**Do:** an optional `Capabilities` list on `PeerMessage` for `hello`. **Don't touch `LanProtocol.Version`, `DiscoveryMagic`, the pairing labels or `TargetHost`.** Both sides record what the other offered. Nothing else changes yet.

**Must pass:** `WireCompatibilityTests` unchanged; a 1.2-shaped hello with no capabilities still pairs and fetches the inventory in a loopback session; a hello with unknown capabilities is fine; capabilities from a peer are capped in count and length.

#### TS-08: Offering settings from the old PC

**Waits on:** decision 1, S9, S10, TS-05, TS-07.

**Files:** `src/TeaChest.Core/Services/Lan/PeerServer.cs`, `src/TeaChest.Core/Services/Lan/LanProtocol.cs`, new `src/TeaChest.Core/Settings/SettingsOffer.cs`, `src/TeaChest/ViewModels/NearbyViewModel.cs`, `src/TeaChest/MainWindow.xaml`, new `tests/TeaChest.Core.Tests/SettingsOfferTests.cs`

**Do:** the "Share settings too" switch, the confirm dialog, the snapshot taken at confirm time, and the `get-settings-offer` / `get-settings-item` handlers serving only snapshot items by ID, in chunks, within the limits. The offer is dropped when sharing stops or times out.

**Must pass:** loopback session tests: no offer without the switch; a request for an ID not in the offer gets an `error`; a request containing a path is refused; a file changed after confirm still sends the snapshot; the limits hold; a peer without the capability never gets the new messages. **Needs a visual check.**

#### TS-09: Taking settings on the new PC

**Waits on:** decision 1, TS-06, TS-08.

**Files:** `src/TeaChest.Core/Services/Lan/PeerClient.cs`, `src/TeaChest/ViewModels/MainViewModel.cs`, `src/TeaChest/MainWindow.xaml`, new `tests/TeaChest.Core.Tests/SettingsFetchTests.cs`

**Do:** the "Bring their settings across?" step after installs, fetching ticked items and restoring them through TS-06, with a `settings` section in the report files. Remove the settings half of the `PeerClient.cs` TODO.

**Must pass:** loopback session tests against a fake old PC that sends a traversal name, a wrong hash, an oversized item, and an item for a recipe the new PC doesn't have; each is refused and reported. **Needs a real two-PC test by the coordinator** and a visual check.

### Stage 4: learning (only if S5 says so)

#### TS-10: Unelevated learn mode

**Waits on:** S5, TS-04, TS-05.

**Files:** new `src/TeaChest/Services/LearnSession.cs`, new `src/TeaChest.Core/Settings/DraftRecipe.cs`, `src/TeaChest/MainWindow.xaml`, new `tests/TeaChest.Core.Tests/DraftRecipeTests.cs`

**Do:** the learn-mode flow from section 4, using `FileSystemWatcher` on the heuristic folders and the known roots for the session only, and turning what was written into a draft with `NeverCarry` and the exclude rules (the prototype's `LearnedRecipe` is the model). Handle buffer overflow by saying "some changes were missed" rather than guessing. Stop watching when the session ends or after 10 minutes.

**Must pass:** `DraftRecipe` tests on canned change lists (the prototype's run output makes a good fixture); the watcher is disposed on every path. **Needs a visual check.**

**Out of scope:** the journal, ETW, anything elevated (decision S6).

#### TS-11: Check the unprivileged journal read as a standard user

**Waits on:** nothing. It's a research card, in `tools/SettingsProbe` only.

**Why:** if `FSCTL_READ_UNPRIVILEGED_USN_JOURNAL` works from a standard-user token, learn mode could see changes it missed while not watching, without admin.

**Do:** add a `--standard-user` mode to `SettingsProbe run` that never opens the volume: it uses only a read-attributes handle to the scratch folder, tries `FSCTL_QUERY_USN_JOURNAL` on that handle for the start and end positions (untested whether that works), and reads with the unprivileged code. Run it from a non-admin account on a test PC or VM, never the owner's profile, and note what comes back: records, names, and whether `OpenFileById` resolves IDs for the user's own files.

**Must pass:** the same privacy rules as the prototype: only the scratch folder is ever printed.

---

## 8. Open questions for the user

1. **S1:** is a read-only "here are the settings you'd lose" preview worth shipping on its own, before carrying works?
2. **S4:** capability negotiation (keeps 1.2) or a version bump (drops it)?
3. **S5 and S6:** is learn mode wanted at all, and is "unelevated only" the right line?
4. **S7:** comfortable leaving game saves to Ludusavi, rather than taking on its data licence?
5. **S8:** leave registry settings (7-Zip, PuTTY) out of v1?
6. **Limits:** are 50 MB per item and 200 MB per session sensible?
7. **Decision 2 (smart file explorer):** happy to drop the change journal from that idea, given it only reaches back hours?
