using System.Net;

namespace TeaChest.Services.Lan;

/// <summary>
/// Keeps a repeat pairing prompt from drowning out a real request. After a decline or a timeout,
/// the same address waits a short while before it can ask again, and longer once it has been
/// turned down a few times in a row. A pairing that succeeds wipes the address's history.
/// </summary>
internal sealed class PairingThrottle
{
    private static readonly TimeSpan ShortCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LongCooldown = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DeclineWindow = TimeSpan.FromMinutes(10);
    private const int DeclinesBeforeLongCooldown = 3;
    private const int MaxTrackedAddresses = 256;

    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private readonly Dictionary<IPAddress, Entry> _entries = new();
    private long _ticks;

    public PairingThrottle(Func<DateTimeOffset> clock) => _clock = clock;

    /// <summary>False when this address asked recently and was turned down.</summary>
    public bool TryBegin(IPAddress address)
    {
        DateTimeOffset now = _clock();
        IPAddress key = Normalize(address);

        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out Entry? entry)) return true;
            entry.LastUsed = ++_ticks;
            return now >= entry.BlockedUntil;
        }
    }

    /// <summary>Records a decline or a timeout and starts the cooldown.</summary>
    public void Declined(IPAddress address)
    {
        DateTimeOffset now = _clock();
        IPAddress key = Normalize(address);

        lock (_gate)
        {
            Entry entry = GetOrAdd(key);
            entry.LastUsed = ++_ticks;
            entry.Declines.RemoveAll(declinedAt => now - declinedAt >= DeclineWindow);
            entry.Declines.Add(now);
            entry.BlockedUntil = now + (entry.Declines.Count >= DeclinesBeforeLongCooldown
                ? LongCooldown
                : ShortCooldown);
        }
    }

    /// <summary>A pairing went through, so this address starts from scratch next time.</summary>
    public void Accepted(IPAddress address)
    {
        IPAddress key = Normalize(address);
        lock (_gate) _entries.Remove(key);
    }

    private Entry GetOrAdd(IPAddress address)
    {
        if (_entries.TryGetValue(address, out Entry? existing)) return existing;

        if (_entries.Count >= MaxTrackedAddresses)
        {
            IPAddress oldest = _entries.MinBy(pair => pair.Value.LastUsed).Key;
            _entries.Remove(oldest);
        }

        var entry = new Entry();
        _entries[address] = entry;
        return entry;
    }

    // ::ffff:10.0.0.1 and 10.0.0.1 are the same caller, and must share one cooldown.
    private static IPAddress Normalize(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private sealed class Entry
    {
        public List<DateTimeOffset> Declines { get; } = new();
        public DateTimeOffset BlockedUntil { get; set; }
        public long LastUsed { get; set; }
    }
}
