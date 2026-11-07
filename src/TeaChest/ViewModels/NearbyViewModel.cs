using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Windows.Threading;
using TeaChest.Models;
using TeaChest.Services;
using TeaChest.Services.Lan;

namespace TeaChest.ViewModels;

public sealed class NearbyDevice : INotifyPropertyChanged
{
    private DiscoveredDevice _found;
    private bool _isKnown;

    public NearbyDevice(DiscoveredDevice found, bool isKnown)
    {
        _found = found;
        _isKnown = isKnown;
    }

    public string DeviceId => _found.Device.DeviceId;
    public string Name => _found.Device.Name;
    public string Detail => $"{_found.Address} · {_found.Device.AppCount} apps";
    public IPEndPoint Endpoint => _found.Endpoint;
    public DateTimeOffset LastSeen => _found.LastSeen;
    public bool IsKnown => _isKnown;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(DiscoveredDevice found, bool isKnown)
    {
        _found = found;
        _isKnown = isKnown;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public void SetKnown(bool isKnown)
    {
        if (_isKnown == isKnown) return;
        _isKnown = isKnown;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsKnown)));
    }
}

public sealed record DiffBucket(string Label, IReadOnlyList<AppItem> Items)
{
    public override string ToString() => Label;
}

/// <summary>
/// The "Nearby devices" screen: share this PC's app list, or find another PC, pair with it, and
/// compare its apps against this one.
/// </summary>
public sealed class NearbyViewModel : INotifyPropertyChanged, IInventoryProvider
{
    private static readonly TimeSpan IdleShareTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DeviceExpiry = TimeSpan.FromSeconds(8);

    private readonly MainViewModel _main;
    private readonly Dispatcher _dispatcher;
    private readonly IDialogService _dialogs;
    private readonly DispatcherTimer _idleTimer;
    private readonly DispatcherTimer _pruneTimer;
    private readonly DispatcherTimer _networkTimer;
    private string _networkWarning = "";
    private DeviceIdentity? _identity;
    private KnownPeers? _known;
    private PeerServer? _server;
    private DiscoveryResponder? _responder;
    private CancellationTokenSource? _browseCts;
    private CancellationTokenSource? _connectCts;
    private TaskCompletionSource<bool>? _pendingPairing;

    private bool _isSharing;
    private bool _isShareBusy;
    private string _shareStatus = "";
    private bool _isConnecting;
    private string _connectStatus = "";
    private string _clientPairingCode = "";
    private string _manualAddress = "";
    private bool _hasPairingRequest;
    private string _pairingRequestText = "";
    private string _pairingRequestCode = "";
    private string _diffSource = "";
    private string _diffSummary = "";
    private DiffBucket? _selectedBucket;
    private NearbyDevice? _selectedDevice;
    private List<AppItem> _missingHere = new();

    public NearbyViewModel(MainViewModel main, Dispatcher dispatcher, IDialogService dialogs)
    {
        _main = main;
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _idleTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = IdleShareTimeout };
        _idleTimer.Tick += (_, _) =>
        {
            _main.Log("Nearby: sharing turned off after 30 minutes without activity.");
            _ = StopSharingAsync();
        };
        _pruneTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(2) };
        _pruneTimer.Tick += (_, _) => PruneDevices();
        // Re-checked while sharing so the warning clears as soon as the network is switched to Private.
        _networkTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(5) };
        _networkTimer.Tick += (_, _) => RefreshNetworkWarning();

        // Async lambdas on purpose, like MainViewModel's: these were async void click handlers, and
        // AsyncRelayCommand would grey Connect for the whole connection, which the redesign can decide.
        ShareCommand = new RelayCommand(async () => await ToggleSharingAsync());
        ConfirmPairingCommand = new RelayCommand(() => AnswerPairing(true));
        RejectPairingCommand = new RelayCommand(() => AnswerPairing(false));
        ConnectCommand = new RelayCommand(async () => await ConnectSelectedAsync());
        ConnectManualCommand = new RelayCommand(async () => await ConnectManualAsync());
        CancelConnectCommand = new RelayCommand(CancelConnect);
        OpenNetworkSettingsCommand = new RelayCommand(OpenNetworkSettings);
        UseMissingCommand = new RelayCommand(UseMissingWithConfirm);
    }

    public RelayCommand ShareCommand { get; }
    public RelayCommand ConfirmPairingCommand { get; }
    public RelayCommand RejectPairingCommand { get; }
    public RelayCommand ConnectCommand { get; }
    public RelayCommand ConnectManualCommand { get; }
    public RelayCommand CancelConnectCommand { get; }
    public RelayCommand OpenNetworkSettingsCommand { get; }
    public RelayCommand UseMissingCommand { get; }

    public ObservableCollection<NearbyDevice> Devices { get; } = new();
    public ObservableCollection<DiffBucket> Buckets { get; } = new();

    /// <summary>Raised when another PC asks to pair, so the window can bring this screen forward.</summary>
    public event EventHandler? PairingRequested;

    /// <summary>Raised after the missing apps were loaded into the install list.</summary>
    public event EventHandler? InstallListReady;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsSharing
    {
        get => _isSharing;
        private set
        {
            if (SetField(ref _isSharing, value)) OnPropertyChanged(nameof(ShareButtonText));
        }
    }

    public bool IsShareBusy
    {
        get => _isShareBusy;
        private set => SetField(ref _isShareBusy, value);
    }

    public string ShareButtonText => IsSharing ? "Stop sharing" : "Share this PC";

    public string ShareStatus
    {
        get => _shareStatus;
        private set
        {
            if (SetField(ref _shareStatus, value)) OnPropertyChanged(nameof(HasShareStatus));
        }
    }

    public bool HasShareStatus => ShareStatus.Length > 0;

    public string NetworkWarning
    {
        get => _networkWarning;
        private set
        {
            if (SetField(ref _networkWarning, value)) OnPropertyChanged(nameof(HasNetworkWarning));
        }
    }

    public bool HasNetworkWarning => NetworkWarning.Length > 0;

    public bool IsConnecting
    {
        get => _isConnecting;
        private set => SetField(ref _isConnecting, value);
    }

    public string ConnectStatus
    {
        get => _connectStatus;
        private set => SetField(ref _connectStatus, value);
    }

    public string ClientPairingCode
    {
        get => _clientPairingCode;
        private set
        {
            if (SetField(ref _clientPairingCode, value)) OnPropertyChanged(nameof(HasClientPairingCode));
        }
    }

    public bool HasClientPairingCode => ClientPairingCode.Length > 0;

    public string ManualAddress
    {
        get => _manualAddress;
        set => SetField(ref _manualAddress, value);
    }

    public bool HasPairingRequest
    {
        get => _hasPairingRequest;
        private set => SetField(ref _hasPairingRequest, value);
    }

    public string PairingRequestText
    {
        get => _pairingRequestText;
        private set => SetField(ref _pairingRequestText, value);
    }

    public string PairingRequestCode
    {
        get => _pairingRequestCode;
        private set => SetField(ref _pairingRequestCode, value);
    }

    public string DiffSource
    {
        get => _diffSource;
        private set => SetField(ref _diffSource, value);
    }

    public string DiffSummary
    {
        get => _diffSummary;
        private set => SetField(ref _diffSummary, value);
    }

    public bool HasDiff => Buckets.Count > 0;
    public bool HasMissing => _missingHere.Count > 0;

    public DiffBucket? SelectedBucket
    {
        get => _selectedBucket;
        set => SetField(ref _selectedBucket, value);
    }

    /// <summary>The device picked in the list, which Connect and compare uses.</summary>
    public NearbyDevice? SelectedDevice
    {
        get => _selectedDevice;
        set => SetField(ref _selectedDevice, value);
    }

    public string LocalAddresses
    {
        get
        {
            string addresses = string.Join(", ", GetLocalAddresses());
            return addresses.Length == 0 ? "No network connection" : addresses;
        }
    }

    int IInventoryProvider.AppCount => _main.TotalCount;

    async Task<ManifestDocument> IInventoryProvider.GetInventoryAsync(CancellationToken cancellationToken)
    {
        return await _dispatcher.InvokeAsync(() =>
        {
            RestartIdleTimer();
            return _main.CreateShareDocument();
        }, DispatcherPriority.Normal, cancellationToken);
    }

    // ---- Browsing -------------------------------------------------------------------------

    public void StartBrowsing()
    {
        if (_browseCts != null) return;
        EnsureIdentity();
        _browseCts = new CancellationTokenSource();
        CancellationToken token = _browseCts.Token;
        _pruneTimer.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                await Discovery.BrowseAsync(found => _dispatcher.InvokeAsync(() => OnDeviceFound(found)), token).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                await _dispatcher.InvokeAsync(() => _main.Log("Nearby: could not search the network: " + ex.Message));
            }
        });
    }

    public void StopBrowsing()
    {
        _browseCts?.Cancel();
        _browseCts?.Dispose();
        _browseCts = null;
        _pruneTimer.Stop();
    }

    private void OnDeviceFound(DiscoveredDevice found)
    {
        if (found.Device.DeviceId == _identity?.DeviceId) return;
        bool known = _known!.All().Any(p => p.DeviceId == found.Device.DeviceId);
        NearbyDevice? existing = Devices.FirstOrDefault(d => d.DeviceId == found.Device.DeviceId);
        if (existing != null)
        {
            existing.Update(found, known);
        }
        else
        {
            Devices.Add(new NearbyDevice(found, known));
        }
    }

    private void PruneDevices()
    {
        DateTimeOffset cutoff = DateTimeOffset.Now - DeviceExpiry;
        foreach (NearbyDevice stale in Devices.Where(d => d.LastSeen < cutoff).ToList()) Devices.Remove(stale);
    }

    // ---- Sharing --------------------------------------------------------------------------

    public async Task ToggleSharingAsync()
    {
        if (IsShareBusy) return;
        if (IsSharing)
        {
            await StopSharingAsync();
            return;
        }

        IsShareBusy = true;
        try
        {
            if (_main.TotalCount == 0)
            {
                ShareStatus = "Scanning this PC first...";
                await _main.ScanAsync();
                if (_main.TotalCount == 0)
                {
                    ShareStatus = "There is nothing to share yet. Scan this PC or load a manifest first.";
                    return;
                }
            }

            ShareStatus = "Checking the Windows firewall (you may see a UAC prompt)...";
            bool firewallReady = await FirewallService.EnsureRuleAsync(CancellationToken.None);

            EnsureIdentity();
            var server = new PeerServer(_identity!, _known!, this, ConfirmPairingAsync);
            server.Activity += line => _dispatcher.InvokeAsync(() => _main.Log("Nearby: " + line));
            server.Start();
            _server = server;

            bool discoverable = true;
            try
            {
                _responder = new DiscoveryResponder(() => _identity!.Describe(_main.TotalCount, server.Port));
                _responder.Start();
            }
            catch (SocketException)
            {
                // Another copy of TeaChest is probably sharing already. Direct connections still work.
                _responder?.Dispose();
                _responder = null;
                discoverable = false;
            }

            IsSharing = true;
            RestartIdleTimer();
            RefreshNetworkWarning();
            _networkTimer.Start();
            if (HasNetworkWarning) _main.Log("Nearby: " + NetworkWarning);
            string where = $"{_identity!.Name} at {LocalAddresses} (port {server.Port})";
            ShareStatus = !firewallReady
                ? $"Sharing as {where}, but the firewall rule was not added, so other PCs may be blocked. Approve the UAC prompt next time, or make sure this network is set to Private."
                : discoverable
                    ? $"Sharing as {where}. Open Nearby devices on the other PC."
                    : $"Sharing as {where}. Discovery is unavailable, so connect from the other PC by address.";
            _main.Log("Nearby: " + ShareStatus);
        }
        catch (Exception ex) when (ex is SocketException or IOException or System.Security.Cryptography.CryptographicException)
        {
            ShareStatus = "Could not start sharing: " + ex.Message;
            await StopSharingAsync(keepStatus: true);
        }
        finally
        {
            IsShareBusy = false;
        }
    }

    public async Task StopSharingAsync(bool keepStatus = false)
    {
        _idleTimer.Stop();
        _networkTimer.Stop();
        NetworkWarning = "";
        _pendingPairing?.TrySetResult(false);
        _responder?.Dispose();
        _responder = null;
        if (_server != null)
        {
            await _server.DisposeAsync();
            _server = null;
        }
        if (IsSharing) _main.Log("Nearby: stopped sharing.");
        IsSharing = false;
        if (!keepStatus) ShareStatus = "";
    }

    private async Task<bool> ConfirmPairingAsync(PairingRequest request, CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool>? pending = await _dispatcher.InvokeAsync(() =>
        {
            // One prompt at a time; a second request while one is open is simply refused.
            if (_pendingPairing != null) return null;
            _pendingPairing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            PairingRequestText = $"{request.Peer.Name} ({request.Address}) wants to see this PC's app list. " +
                                 "Only confirm if the other screen shows exactly this code.";
            PairingRequestCode = request.Code;
            HasPairingRequest = true;
            RestartIdleTimer();
            PairingRequested?.Invoke(this, EventArgs.Empty);
            return _pendingPairing;
        });
        if (pending == null) return false;

        using CancellationTokenRegistration registration = cancellationToken.Register(() => pending.TrySetResult(false));
        try
        {
            return await pending.Task.ConfigureAwait(false);
        }
        finally
        {
            await _dispatcher.InvokeAsync(() =>
            {
                _pendingPairing = null;
                HasPairingRequest = false;
                PairingRequestCode = "";
            });
        }
    }

    public void AnswerPairing(bool accept) => _pendingPairing?.TrySetResult(accept);

    private void RefreshNetworkWarning()
    {
        List<LanNetwork> publicNetworks = NetworkProfiles.GetLanNetworks().Where(n => n.IsPublic).ToList();
        NetworkWarning = publicNetworks.Count == 0
            ? ""
            : $"Windows treats {string.Join(" and ", publicNetworks.Select(n => $"\"{n.Name}\" ({n.Adapter})"))} as a Public network, " +
              "so it will block other PCs from connecting here. If this is your home or office network, set it to Private: " +
              "Settings → Network & internet → your connection → Network profile type.";
    }

    public void OpenNetworkSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:network-status") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Settings app unavailable (e.g. Server Core); the warning text already says where to go.
        }
    }

    // ---- Connecting -----------------------------------------------------------------------

    public Task ConnectAsync(NearbyDevice device) => ConnectAsync(device.Endpoint, device.Name);

    /// <summary>Connects to the picked device, or to the only one there is when nothing is picked.</summary>
    public async Task ConnectSelectedAsync()
    {
        if (SelectedDevice is { } device) await ConnectAsync(device);
        else if (Devices.Count == 1) await ConnectAsync(Devices[0]);
    }

    public Task ConnectManualAsync()
    {
        string text = ManualAddress.Trim();
        if (!IPEndPoint.TryParse(text, out IPEndPoint? endpoint))
        {
            ConnectStatus = "Enter an address like 192.168.1.20 or 192.168.1.20:47821.";
            return Task.CompletedTask;
        }
        if (endpoint.Port == 0) endpoint.Port = LanProtocol.DefaultTcpPort;
        return ConnectAsync(endpoint, endpoint.ToString());
    }

    public void CancelConnect() => _connectCts?.Cancel();

    private async Task ConnectAsync(IPEndPoint endpoint, string label)
    {
        if (IsConnecting) return;
        EnsureIdentity();
        IsConnecting = true;
        ClientPairingCode = "";
        ConnectStatus = $"Connecting to {label}...";
        _connectCts = new CancellationTokenSource();
        try
        {
            var client = new PeerClient(_identity!, _known!);
            InventoryResult result = await client.FetchInventoryAsync(
                endpoint,
                _main.TotalCount,
                code => _dispatcher.InvokeAsync(() =>
                {
                    ClientPairingCode = code;
                    ConnectStatus = $"Check that {label} shows this code, then click Confirm on that PC.";
                }),
                _connectCts.Token);
            ClientPairingCode = "";

            List<string> warnings = ManifestSanitizer.Sanitize(result.Inventory, ManifestTrust.Network);
            foreach (string warning in warnings) _main.Log($"Nearby: {result.Device.Name}: {warning}");

            ConnectStatus = $"Got {result.Inventory.Apps.Count} apps from {result.Device.Name}. Comparing with this PC...";
            List<AppItem> remote = ManifestService.ToItems(result.Inventory);
            foreach (AppItem item in remote) AppClassifier.ApplyClassification(item);
            InstalledIndex index = await InstalledIndex.BuildAsync(_connectCts.Token);
            List<AppItem>? local = _main.Apps.Count > 0 ? _main.Apps.ToList() : null;
            InventoryDiffResult diff = InventoryDiff.Compare(remote, index, local);

            ShowDiff(result.Device.Name, diff, local != null);
            ConnectStatus = (result.NewlyPaired ? $"Paired with {result.Device.Name}. " : "") +
                            $"{diff.MissingHere.Count} of its apps are missing here.";
            _main.Log($"Nearby: compared with {result.Device.Name}: {DiffSummary}.");
        }
        catch (OperationCanceledException)
        {
            ConnectStatus = "Cancelled.";
        }
        catch (Exception ex) when (ex is PeerRejectedException or TimeoutException or SocketException or IOException
                                       or AuthenticationException or InvalidDataException or System.Text.Json.JsonException)
        {
            ConnectStatus = ex switch
            {
                SocketException => $"Could not reach {label}. Check that it is sharing and on the same network.",
                AuthenticationException => $"Could not set up a secure connection with {label}: {ex.Message}",
                _ => ex.Message
            };
        }
        finally
        {
            ClientPairingCode = "";
            IsConnecting = false;
            _connectCts?.Dispose();
            _connectCts = null;
            RefreshKnownFlags();
        }
    }

    private void ShowDiff(string source, InventoryDiffResult diff, bool hasLocalInventory)
    {
        _missingHere = diff.MissingHere;
        Buckets.Clear();
        Buckets.Add(new DiffBucket($"Missing here ({diff.MissingHere.Count})", diff.MissingHere));
        Buckets.Add(new DiffBucket($"Already here ({diff.AlreadyHere.Count})", diff.AlreadyHere));
        if (hasLocalInventory) Buckets.Add(new DiffBucket($"Only on this PC ({diff.OnlyHere.Count})", diff.OnlyHere));
        SelectedBucket = Buckets[0];
        DiffSource = source;
        DiffSummary = $"{diff.MissingHere.Count} missing here · {diff.AlreadyHere.Count} already installed" +
                      (hasLocalInventory ? $" · {diff.OnlyHere.Count} only on this PC" : "");
        OnPropertyChanged(nameof(HasDiff));
        OnPropertyChanged(nameof(HasMissing));
    }

    /// <summary>Loads the missing apps (with their checkboxes as ticked here) into the install list.</summary>
    public bool UseMissingAsInstallList()
    {
        if (_missingHere.Count == 0) return false;
        if (!_main.LoadItems(_missingHere.Select(a => a.Clone()), DiffSource)) return false;
        InstallListReady?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void UseMissingWithConfirm()
        => UseMissingFlow(_dialogs, _main.IsBusy, _main.Apps.Count > 0, () => UseMissingAsInstallList());

    /// <summary>Asks before the missing apps replace a list that is already there. Returns whether they were used.</summary>
    internal static bool UseMissingFlow(IDialogService dialogs, bool isBusy, bool hasApps, Func<bool> useMissing)
    {
        if (isBusy)
        {
            dialogs.Show("Wait for the current operation to finish first.", "TeaChest", DialogIcon.Information);
            return false;
        }
        if (hasApps)
        {
            bool replace = dialogs.Confirm(
                "This replaces the current app list with the apps missing on this PC. Save a manifest first if you need the current list.\n\nContinue?",
                "Use missing apps",
                ConfirmButtons.OkCancel,
                DialogIcon.Question);
            if (!replace) return false;
        }
        return useMissing();
    }

    public async Task ShutdownAsync()
    {
        StopBrowsing();
        CancelConnect();
        await StopSharingAsync();
        _identity?.Dispose();
    }

    private void EnsureIdentity()
    {
        _known ??= KnownPeers.Load();
        _identity ??= DeviceIdentity.LoadOrCreate();
    }

    private void RefreshKnownFlags()
    {
        if (_known == null) return;
        var knownIds = _known.All().Select(p => p.DeviceId).ToHashSet();
        foreach (NearbyDevice device in Devices) device.SetKnown(knownIds.Contains(device.DeviceId));
    }

    private void RestartIdleTimer()
    {
        if (!IsSharing) return;
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    private static IEnumerable<string> GetLocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(u => u.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && LanProtocol.IsLocalAddress(a) && !a.ToString().StartsWith("169.254."))
                .Select(a => a.ToString())
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return Array.Empty<string>();
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
