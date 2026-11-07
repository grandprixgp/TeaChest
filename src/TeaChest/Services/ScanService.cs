using TeaChest.Models;
using TeaChest.Services.ExtraSources;

namespace TeaChest.Services;

public sealed class ScanResult
{
    public IReadOnlyList<AppItem> Apps { get; init; } = Array.Empty<AppItem>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public TimeSpan Duration { get; init; }
}

public sealed class ScanService
{
    private readonly RegistryInventoryService _registry = new();
    private readonly AppxInventoryService _appx = new();
    private readonly WingetService _winget = new();
    // Later sources (VS Code extensions, Scoop, npm and friends) go here.
    private readonly IReadOnlyList<IExtraSource> _extraSources = new IExtraSource[] { new VsCodeSource(), new ScoopSource() };
    private readonly SelectionOptions _options;
    private readonly NameCache _names;

    public ScanService(SelectionOptions? options = null)
    {
        _options = options ?? new SelectionOptions();
        _names = new NameCache(AppPaths.DataDirectory);
    }

    public async Task<ScanResult> ScanAsync(IProgress<ScanSnapshot>? progress, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        var items = new Dictionary<string, AppItem>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        object gate = new();

        Publish(items, gate, progress, "Starting inventory...", true, 0, false);

        var tasks = new List<Task<SourceBatch>>
        {
            WrapSource("Registry", _registry.ScanAsync(cancellationToken)),
            WrapSource("AppX", _appx.ScanAsync(cancellationToken)),
            WrapWinget(cancellationToken)
        };
        foreach (IExtraSource source in _extraSources) tasks.Add(WrapExtraSource(source, cancellationToken));

        bool wingetAvailable = await CollectAsync(tasks, items, gate, warnings,
            status => Publish(items, gate, progress, status, true, 0, false)).ConfigureAwait(false);

        List<AppItem> resolveTargets = wingetAvailable ? BuildResolutionTargets(items, gate) : new List<AppItem>();
        if (resolveTargets.Count > 0)
        {
            // What the lookup is allowed to change, kept so a real answer that finds nothing can
            // put the row back exactly as the inventory described it.
            var baselines = resolveTargets.ToDictionary(item => item, item => item.Clone());

            int remembered = 0;
            foreach (AppItem item in resolveTargets)
            {
                NameCacheEntry? cached = _names.Find(item.Name, item.Publisher);
                if (cached == null) continue;
                lock (gate) ApplyResolution(item, FromCache(cached));
                remembered++;
            }

            int done = 0;
            Publish(items, gate, progress,
                remembered > 0
                    ? $"Showing {remembered} remembered answers while checking again..."
                    : $"Resolving {resolveTargets.Count} package IDs in parallel...",
                true, 0, false);

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = 8,
                CancellationToken = cancellationToken
            };

            await Parallel.ForEachAsync(resolveTargets, parallelOptions, async (item, token) =>
            {
                ResolveResult result = await ResolveOneAsync(item, token).ConfigureAwait(false);
                lock (gate)
                {
                    RestoreLookupState(item, baselines[item]);
                    ApplyResolution(item, result);
                }

                // The real answer always wins, and is what goes back to disk.
                _names.Set(item.Name, item.Publisher,
                    result.WingetId?.Id,
                    result.StoreId?.Id,
                    result.WingetId?.Confidence ?? result.StoreId?.Confidence ?? MatchConfidence.None);

                int current = Interlocked.Increment(ref done);
                if (current == resolveTargets.Count || current % 4 == 0)
                {
                    Publish(items, gate, progress,
                        $"Resolving package IDs... {current}/{resolveTargets.Count}",
                        true,
                        (double)current / resolveTargets.Count,
                        false);
                }
            }).ConfigureAwait(false);

            // A cancelled scan throws out of the loop above, so only a finished one saves.
            _names.Save();
        }

        lock (gate)
        {
            DeduplicateById(items);
            foreach (AppItem item in items.Values)
            {
                if (item.IsWinget && string.IsNullOrWhiteSpace(item.Id))
                {
                    item.InstallType = "manual";
                    item.Source = "winget";
                    item.Notes = "WinGet reported this app, but its package ID could not be resolved. Reinstall it manually.";
                }
                AppClassifier.ApplyClassification(item);
            }
        }

        IReadOnlyList<AppItem> finalApps = Publish(items, gate, progress, "", false, 1, true);

        return new ScanResult
        {
            Apps = finalApps,
            Warnings = warnings,
            Duration = DateTime.UtcNow - started
        };
    }

    private IReadOnlyList<AppItem> Publish(
        Dictionary<string, AppItem> items,
        object gate,
        IProgress<ScanSnapshot>? progress,
        string status,
        bool indeterminate,
        double value,
        bool complete)
    {
        List<AppItem> snapshot;
        lock (gate)
        {
            snapshot = items.Values
                .Select(i => i.Clone())
                .OrderBy(i => i.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        foreach (AppItem item in snapshot) AppClassifier.ApplyDefaults(item, _options);

        string finalStatus = complete
            ? $"Scan complete · {snapshot.Count} apps · {snapshot.Count(i => i.IsSelected)} selected"
            : status;

        progress?.Report(new ScanSnapshot
        {
            Apps = snapshot,
            Status = finalStatus,
            IsComplete = complete,
            Progress = value,
            IsIndeterminate = indeterminate
        });
        return snapshot;
    }

    /// <summary>
    /// Merges each source in as it finishes. A source that fails becomes a warning and the scan
    /// carries on with the rest; only cancellation stops it. Returns false when WinGet was unavailable.
    /// </summary>
    internal static async Task<bool> CollectAsync(
        List<Task<SourceBatch>> tasks,
        Dictionary<string, AppItem> items,
        object gate,
        List<string> warnings,
        Action<string> report)
    {
        bool wingetAvailable = true;
        while (tasks.Count > 0)
        {
            Task<SourceBatch> finished = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(finished);
            try
            {
                SourceBatch batch = await finished.ConfigureAwait(false);
                lock (gate) MergeRange(items, batch.Items);
                if (batch.Warning != null)
                {
                    warnings.Add(batch.Warning);
                    if (batch.Name == "WinGet" && batch.Items.Count == 0) wingetAvailable = false;
                }
                int count;
                lock (gate) count = items.Count;
                report(batch.Warning == null
                    ? $"{batch.Name}: {batch.Items.Count} items · {count} apps so far"
                    : $"{batch.Name} unavailable; continuing with other sources.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                warnings.Add(ex.Message);
                report(ex.Message);
            }
        }
        return wingetAvailable;
    }

    internal static Task<SourceBatch> WrapExtraSource(IExtraSource source, CancellationToken cancellationToken)
    {
        return WrapSource(source.Name, RunAsync());

        // Async so a source that throws before its first await still fails only its own task.
        async Task<IReadOnlyList<AppItem>> RunAsync()
        {
            try
            {
                IReadOnlyList<AppItem> found = await source.ScanAsync(cancellationToken).ConfigureAwait(false);
                return ExtraSourceTools.Prepare(source, found);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Its own timeout would otherwise look like the user cancelling the whole scan.
                throw new InvalidOperationException($"{source.Name} couldn't be read: {ex.Message}", ex);
            }
        }
    }

    private static Task<SourceBatch> WrapSource(string name, Task<IReadOnlyList<AppItem>> task)
    {
        return task.ContinueWith(t =>
        {
            if (t.IsFaulted) throw t.Exception!.GetBaseException();
            if (t.IsCanceled) throw new OperationCanceledException();
            return new SourceBatch(name, t.Result, null);
        }, TaskScheduler.Default);
    }

    private async Task<SourceBatch> WrapWinget(CancellationToken cancellationToken)
    {
        WingetInventory inventory = await _winget.GetInventoryAsync(cancellationToken).ConfigureAwait(false);
        var mapped = inventory.Rows
            .Where(row => !row.IsMsix)
            .Select(MapWingetRow)
            .ToList();
        return new SourceBatch("WinGet", mapped, inventory.Warning);
    }

    private static AppItem MapWingetRow(WingetRow row)
    {
        string id = row.Id;
        if (row.IsMsix || id.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase))
        {
            return new AppItem
            {
                Name = row.Name,
                Id = "",
                Source = "msix",
                InstallType = "manual",
                Version = row.Version,
                PackageFullName = id.Length > 5 ? id[5..] : "",
                Notes = "MSIX package."
            };
        }
        if (row.IsArp || id.StartsWith("ARP\\", StringComparison.OrdinalIgnoreCase))
        {
            return new AppItem
            {
                Name = row.Name,
                Id = id,
                Source = "registry",
                InstallType = "manual",
                Version = row.Version,
                Notes = ""
            };
        }
        if (string.Equals(row.Source, "msstore", StringComparison.OrdinalIgnoreCase) ||
            (id.Length == 12 && id.StartsWith("9", StringComparison.Ordinal) && id.All(char.IsAsciiLetterOrDigit)))
        {
            return new AppItem
            {
                Name = row.Name,
                Id = id,
                Source = "msstore",
                InstallType = "msstore",
                Version = row.Version,
                Notes = id.Length == 0 ? "Microsoft Store package ID was truncated." : ""
            };
        }

        return new AppItem
        {
            Name = row.Name,
            Id = string.IsNullOrWhiteSpace(id) ? "" : id,
            Source = "winget",
            InstallType = "winget",
            Version = row.Version,
            Notes = string.IsNullOrWhiteSpace(id) ? "WinGet package ID was truncated; resolving by name." : ""
        };
    }

    private static void MergeRange(Dictionary<string, AppItem> items, IEnumerable<AppItem> incoming)
    {
        foreach (AppItem item in incoming)
        {
            AppClassifier.ApplyClassification(item);
            string key = TextUtil.NormalizeStrict(item.Name);
            if (string.IsNullOrWhiteSpace(key)) key = TextUtil.NormalizeStrict(item.Id);
            if (string.IsNullOrWhiteSpace(key)) key = Guid.NewGuid().ToString("N");

            if (items.TryGetValue(key, out AppItem? existing))
            {
                existing.MergeFrom(item);
                AppClassifier.ApplyClassification(existing);
            }
            else
            {
                items[key] = item;
            }
        }
    }

    internal static List<AppItem> BuildResolutionTargets(Dictionary<string, AppItem> items, object gate)
    {
        lock (gate)
        {
            return items.Values
                .Where(item =>
                {
                    // An npm package or VS Code extension has no WinGet package to find.
                    if (AppClassifier.IsAddOn(item)) return false;
                    if (AppClassifier.IsInbox(item)) return false;
                    string category = AppClassifier.GetCategory(item.Name, item.InstallType);
                    if (category is "Runtime" or "Driver") return false;

                    if (item.IsWinget && string.IsNullOrWhiteSpace(item.Id)) return true;
                    if (item.IsManual && (item.Source.Equals("registry", StringComparison.OrdinalIgnoreCase) ||
                                          item.Source.Equals("msix", StringComparison.OrdinalIgnoreCase))) return true;
                    return false;
                })
                .ToList();
        }
    }

    private async Task<ResolveResult> ResolveOneAsync(AppItem item, CancellationToken cancellationToken)
    {
        NameMatch? wingetMatch = await _winget.ResolveIdAsync(item.Name, cancellationToken).ConfigureAwait(false);
        if (wingetMatch != null) return new ResolveResult(wingetMatch, null);

        if (item.Source.Equals("msix", StringComparison.OrdinalIgnoreCase) && !AppClassifier.IsInbox(item))
        {
            NameMatch? storeMatch = await _winget.ResolveStoreIdAsync(item.Name, cancellationToken).ConfigureAwait(false);
            if (storeMatch != null) return new ResolveResult(null, storeMatch);
        }
        return new ResolveResult(null, null);
    }

    /// <summary>
    /// Puts one lookup answer onto a row. Cached answers and fresh ones go through here, so both
    /// leave the same ID, type, confidence, and note behind.
    /// </summary>
    internal static void ApplyResolution(AppItem item, ResolveResult result)
    {
        if (result.WingetId != null)
        {
            item.Id = result.WingetId.Id;
            item.Source = "winget";
            item.InstallType = "winget";
            item.MatchConfidence = result.WingetId.Confidence;
            item.Notes = result.WingetId.Confidence == MatchConfidence.Loose
                ? "Matched to a WinGet package by a loose name match. Worth a quick check."
                : "Matched to a WinGet package by name.";
        }
        else if (result.StoreId != null)
        {
            item.Id = result.StoreId.Id;
            item.Source = "msstore";
            item.InstallType = "msstore";
            item.MatchConfidence = result.StoreId.Confidence;
            item.Notes = result.StoreId.Confidence == MatchConfidence.Loose
                ? "Matched to a Microsoft Store product ID by a loose name match. Worth a quick check."
                : "Matched to a Microsoft Store product ID.";
        }
        else if (item.IsWinget)
        {
            item.Id = "";
            item.InstallType = "manual";
            item.Source = "winget";
            item.MatchConfidence = MatchConfidence.None;
            item.Notes = "WinGet reported this app, but its package ID could not be resolved. Reinstall it manually.";
        }
        else if (item.IsStore)
        {
            // A remembered Store ID is being dropped, so put the package back how it was found.
            item.Id = item.PackageFamilyName.Length > 0 ? "MSIX\\" + item.PackageFamilyName : "";
            item.Source = "msix";
            item.InstallType = "manual";
            item.MatchConfidence = MatchConfidence.None;
            item.Notes = "Store app could not be matched automatically. Reinstall it from the Microsoft Store.";
        }
        else
        {
            item.Notes = "No WinGet package found. Reinstall manually.";
        }
        AppClassifier.ApplyClassification(item);
    }

    private static ResolveResult FromCache(NameCacheEntry entry)
        => new(
            entry.WingetId == null ? null : new NameMatch(entry.WingetId, entry.Confidence),
            entry.StoreId == null ? null : new NameMatch(entry.StoreId, entry.Confidence));

    private static void RestoreLookupState(AppItem item, AppItem pristine)
    {
        item.Id = pristine.Id;
        item.Source = pristine.Source;
        item.InstallType = pristine.InstallType;
        item.MatchConfidence = pristine.MatchConfidence;
        item.Notes = pristine.Notes;
    }

    private static void DeduplicateById(Dictionary<string, AppItem> items)
    {
        var byId = new Dictionary<string, AppItem>(StringComparer.OrdinalIgnoreCase);
        var result = new List<AppItem>();
        foreach (AppItem item in items.Values)
        {
            if (!string.IsNullOrWhiteSpace(item.Id) &&
                (item.IsWinget || string.Equals(item.InstallType, "msstore", StringComparison.OrdinalIgnoreCase)))
            {
                string idKey = item.InstallType + "|" + item.Id.ToLowerInvariant();
                if (byId.TryGetValue(idKey, out AppItem? primary))
                {
                    if (item.Name.Length < primary.Name.Length) primary.Name = item.Name;
                    primary.MergeFrom(item);
                    AppClassifier.ApplyClassification(primary);
                    continue;
                }
                byId[idKey] = item;
            }
            result.Add(item);
        }

        items.Clear();
        foreach (AppItem item in result)
        {
            string key = TextUtil.NormalizeStrict(item.Name) + "|" + TextUtil.NormalizeStrict(item.Version);
            if (items.ContainsKey(key)) key += "|" + item.IdentityKey;
            if (items.ContainsKey(key)) key += "|" + Guid.NewGuid().ToString("N");
            items[key] = item;
        }
    }

    internal sealed record SourceBatch(string Name, IReadOnlyList<AppItem> Items, string? Warning);
    internal sealed record ResolveResult(NameMatch? WingetId, NameMatch? StoreId);
}
