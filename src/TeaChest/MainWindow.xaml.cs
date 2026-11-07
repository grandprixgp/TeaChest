using System.Windows;
using System.Windows.Controls;
using TeaChest.Models;
using TeaChest.Services;
using TeaChest.ViewModels;

namespace TeaChest;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly NearbyViewModel _nearby;

    public MainWindow()
    {
        InitializeComponent();
        var dialogs = new WpfDialogService(this);
        _viewModel = new MainViewModel(dialogs);
        _viewModel.Error += ViewModel_Error;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.CollectViewRequested += (_, _) => CollectNav.IsChecked = true;
        DataContext = _viewModel;

        _nearby = new NearbyViewModel(_viewModel, Dispatcher, dialogs);
        _nearby.PairingRequested += (_, _) =>
        {
            NearbyNav.IsChecked = true;
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        };
        _nearby.InstallListReady += (_, _) => InstallNav.IsChecked = true;
        NearbyView.DataContext = _nearby;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        bool dark = ThemeService.LoadPreference();
        ThemeService.Apply(dark);
        UpdateThemeButton();
        VersionText.Text = _viewModel.VersionText;

        if (ManifestService.FindDefault(AppContext.BaseDirectory) is { } manifest)
        {
            await _viewModel.LoadManifestAsync(manifest);
        }
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
        => ThemeService.ApplyWindowTheme(this, ThemeService.IsDark);

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_viewModel.IsBusy)
        {
            _ = _nearby.ShutdownAsync();
            return;
        }
        e.Cancel = true;
        MessageBox.Show(this, "An operation is still running. Wait for it to finish, or use Cancel in the status bar.", "TeaChest", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CollectNav_Checked(object sender, RoutedEventArgs e) => ShowView(CollectView);

    private void InstallNav_Checked(object sender, RoutedEventArgs e) => ShowView(InstallView);

    private void NearbyNav_Checked(object sender, RoutedEventArgs e) => ShowView(NearbyView);

    private void ShowView(Grid? view)
    {
        // Checked fires for the default tab while InitializeComponent is still building the views.
        if (CollectView == null || InstallView == null || NearbyView == null || view == null) return;
        CollectView.Visibility = view == CollectView ? Visibility.Visible : Visibility.Collapsed;
        InstallView.Visibility = view == InstallView ? Visibility.Visible : Visibility.Collapsed;
        NearbyView.Visibility = view == NearbyView ? Visibility.Visible : Visibility.Collapsed;

        // Only search the network while someone is looking at the list.
        if (view == NearbyView) _nearby?.StartBrowsing();
        else _nearby?.StopBrowsing();
    }

    // The double-clicks stay as events: MouseDoubleClick fires even after the grid has handled the
    // click, and for any mouse button, which a MouseBinding wouldn't match.
    private async void DevicesList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DevicesList.SelectedItem is NearbyDevice device) await _nearby.ConnectAsync(device);
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        ThemeService.Apply(!ThemeService.IsDark);
        ThemeService.SavePreference();
        UpdateThemeButton();
        ThemeService.ApplyWindowTheme(this, ThemeService.IsDark);
    }

    private void UpdateThemeButton()
        => ThemeButton.Content = ThemeService.IsDark ? "Light theme" : "Dark theme";

    private void ViewModel_Error(object? sender, AppErrorEventArgs e)
    {
        MessageBox.Show(this, e.Message, e.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void AppsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (AppsGrid.SelectedItem is AppItem) _viewModel.IsDetailsOpen = true;
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsDetailsOpen)) return;
        // The panel shows these three, and the grid needs their width back to fit beside it at 1120.
        Visibility columns = _viewModel.IsDetailsOpen ? Visibility.Collapsed : Visibility.Visible;
        AppsCategoryColumn.Visibility = columns;
        AppsIdColumn.Visibility = columns;
        AppsNotesColumn.Visibility = columns;
    }

    private void PlanGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (PlanGrid.SelectedItem is PlanItem item) _viewModel.ShowPlanItem(item);
    }

    private void LogBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (LogBox.IsVisible) LogBox.ScrollToEnd();
    }
}
