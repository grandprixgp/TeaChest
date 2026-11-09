namespace SettingsProbe.Usn;

/// <summary>What happened to one file or folder under the scope, folded from all its records.</summary>
internal sealed class ScopedChange
{
    public required UInt128 FileId { get; init; }
    public string RelativePath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public UsnReason Reasons { get; set; }
    public int RecordCount { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }

    /// <summary>Set when the item was moved out of the scope; its new name is never kept.</summary>
    public bool MovedOut { get; set; }

    public bool Created => Reasons.HasFlag(UsnReason.FileCreate);
    public bool Deleted => Reasons.HasFlag(UsnReason.FileDelete);
    public bool Written => (Reasons & UsnReason.AnyWrite) != 0;
    public bool Renamed => (Reasons & (UsnReason.RenameNewName | UsnReason.RenameOldName)) != 0;
}

/// <summary>
/// Keeps only the journal records under one folder, and never builds a path for anything else.
///
/// The journal covers the whole volume, so this is the privacy boundary: a record is kept only if
/// its parent is the root or a folder already known to be inside it. Folders created (or moved)
/// inside the root join the known set as their records arrive, which works because records come
/// in USN order and a folder's creation always precedes its children's. Anything else is counted
/// and dropped without its name being looked at.
/// </summary>
internal sealed class ScopeFilter
{
    private readonly UInt128 _root;
    private readonly Dictionary<UInt128, (UInt128 Parent, string Name)> _known = new();
    private readonly Dictionary<UInt128, ScopedChange> _changes = new();
    private readonly List<UInt128> _order = new();

    public ScopeFilter(UInt128 rootId)
    {
        _root = rootId;
    }

    public int Kept { get; private set; }
    public int Skipped { get; private set; }

    /// <summary>Feeds one record. Returns true if it was inside the scope and kept.</summary>
    public bool Add(UsnRecord record)
    {
        bool parentInside = record.ParentId == _root || _known.ContainsKey(record.ParentId);

        if (!parentInside)
        {
            // The new-name half of a move out of the scope. Forget the item, keep nothing of its
            // new location, and mark what we had as moved out.
            if (record.Reason.HasFlag(UsnReason.RenameNewName) && _known.Remove(record.FileId))
            {
                if (_changes.TryGetValue(record.FileId, out ScopedChange? gone)) gone.MovedOut = true;
                Kept++;
                return true;
            }
            Skipped++;
            return false;
        }

        _known[record.FileId] = (record.ParentId, record.FileName);

        if (!_changes.TryGetValue(record.FileId, out ScopedChange? change))
        {
            change = new ScopedChange { FileId = record.FileId, FirstSeenUtc = record.TimeStampUtc };
            _changes[record.FileId] = change;
            _order.Add(record.FileId);
        }

        change.IsDirectory |= record.IsDirectory;
        change.Reasons |= record.Reason;
        change.RecordCount++;
        change.LastSeenUtc = record.TimeStampUtc;
        change.MovedOut = false;
        // Remember the path now too, so an item later moved out still has its last inside path.
        change.RelativePath = RelativePath(record.FileId) ?? change.RelativePath;
        Kept++;
        return true;
    }

    /// <summary>
    /// The path of a known item relative to the root, built only from names seen inside the scope.
    /// Returns null if the chain doesn't reach the root, which shouldn't happen but would mean
    /// something outside the scope is involved.
    /// </summary>
    public string? RelativePath(UInt128 id)
    {
        var parts = new List<string>();
        UInt128 current = id;
        for (int depth = 0; depth < 256; depth++)
        {
            if (current == _root)
            {
                parts.Reverse();
                return string.Join('\\', parts);
            }
            if (!_known.TryGetValue(current, out var entry)) return null;
            parts.Add(entry.Name);
            current = entry.Parent;
        }
        return null;
    }

    /// <summary>
    /// Every item touched under the scope, in the order first seen, with its latest path.
    /// Items moved out keep their last path inside the scope.
    /// </summary>
    public IReadOnlyList<ScopedChange> Changes()
    {
        var result = new List<ScopedChange>();
        foreach (UInt128 id in _order)
        {
            ScopedChange change = _changes[id];
            string? path = RelativePath(id);
            if (path != null) change.RelativePath = path;
            if (change.RelativePath.Length == 0) continue;
            result.Add(change);
        }
        return result;
    }
}
