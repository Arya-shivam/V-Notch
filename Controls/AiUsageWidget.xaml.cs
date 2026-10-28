using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using VNotch.Services.AiUsage;

namespace VNotch.Controls;

public partial class AiUsageWidget : UserControl, IDisposable
{
    private readonly AiUsageService _service = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private CancellationTokenSource? _request;
    private IReadOnlyList<ProviderUsage> _providers = Array.Empty<ProviderUsage>();
    private DateTimeOffset _lastAttempt;
    private bool _enabled;
    private bool _disposed;
    private bool _refreshing;

    public AiUsageWidget()
    {
        InitializeComponent();
        _timer.Tick += OnTimerTick;
        IsVisibleChanged += OnVisibilityChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // The surrounding calendar surface treats presses as drag/tap navigation.
        // Keep provider selection, scrolling and refresh local to this widget.
        MouseDown += (_, e) => e.Handled = true;
        MouseUp += (_, e) => e.Handled = true;
        MouseWheel += (_, e) => e.Handled = true;
    }

    public void ApplySettings(bool enabled)
    {
        if (_disposed) return;
        _enabled = enabled;
        RefreshButton.IsEnabled = enabled && !_refreshing;
        if (!enabled)
        {
            _request?.Cancel();
            _providers = Array.Empty<ProviderUsage>();
            ProviderPicker.ItemsSource = null;
            WindowsList.ItemsSource = null;
            ShowStatus("Online AI access is disabled in Privacy settings.");
            UpdatedText.Text = string.Empty;
        }
        UpdatePolling();
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdatePolling();
    private void OnLoaded(object sender, RoutedEventArgs e) => UpdatePolling();
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        _request?.Cancel();
    }

    private void UpdatePolling()
    {
        if (_disposed) return;
        if (_enabled && IsLoaded && IsVisible)
        {
            _timer.Start();
            if (_providers.Count == 0 || DateTimeOffset.UtcNow - _lastAttempt >= _timer.Interval)
                _ = RefreshAsync();
        }
        else
        {
            _timer.Stop();
            _request?.Cancel();
        }
    }

    private void OnTimerTick(object? sender, EventArgs e) => _ = RefreshAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await RefreshAsync();
    }

    internal async Task RefreshAsync()
    {
        if (_disposed || !_enabled || !IsLoaded || !IsVisible || _refreshing) return;
        _refreshing = true;
        RefreshButton.IsEnabled = false;
        _lastAttempt = DateTimeOffset.UtcNow;
        using var request = new CancellationTokenSource();
        _request = request;
        if (_providers.Count == 0) ShowStatus("Reading local provider logins…");
        UpdatedText.Text = "Refreshing…";
        try
        {
            var providers = await _service.FetchAsync(request.Token);
            if (_disposed || !_enabled || request.IsCancellationRequested) return;
            string? selected = (ProviderPicker.SelectedItem as ProviderUsage)?.ProviderId;
            _providers = providers;
            ProviderPicker.ItemsSource = _providers;
            ProviderPicker.SelectedItem = _providers.FirstOrDefault(p => p.ProviderId == selected)
                ?? _providers.FirstOrDefault(p => p.State == UsageProviderState.Available)
                ?? _providers.FirstOrDefault();
            PresentSelected();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            if (!_disposed && _enabled) PresentSelected();
        }
        catch (Exception)
        {
            // Never send exception details from credential-backed HTTP to logs or UI.
            _providers = Array.Empty<ProviderUsage>();
            ProviderPicker.ItemsSource = null;
            WindowsList.ItemsSource = null;
            ShowStatus("Could not read provider usage. Try refresh.");
            UpdatedText.Text = "Refresh failed";
        }
        finally
        {
            _request = null;
            _refreshing = false;
            if (!_disposed) RefreshButton.IsEnabled = _enabled;
        }
    }

    private void ProviderPicker_SelectionChanged(object sender, SelectionChangedEventArgs e) => PresentSelected();

    private void PresentSelected()
    {
        if (ProviderPicker.SelectedItem is not ProviderUsage provider)
        {
            ShowStatus("No usage providers configured.");
            UpdatedText.Text = string.Empty;
            return;
        }
        if (provider.State != UsageProviderState.Available || provider.Windows.Count == 0)
        {
            WindowsList.ItemsSource = null;
            ShowStatus(provider.Status);
        }
        else
        {
            WindowsList.ItemsSource = provider.Windows.Select(window => new WindowRow(window)).ToArray();
            StatusText.Visibility = Visibility.Collapsed;
            WindowScroller.Visibility = Visibility.Visible;
        }
        UpdatedText.Text = provider.UpdatedAt is { } updated
            ? $"Updated {updated.ToLocalTime():HH:mm}"
            : "Usage unavailable";
        UpdatedText.ToolTip = provider.Status;
    }

    private void ShowStatus(string status)
    {
        StatusText.Text = status;
        StatusText.ToolTip = status;
        StatusText.Visibility = Visibility.Visible;
        WindowScroller.Visibility = Visibility.Collapsed;
    }

    private sealed class WindowRow(UsageWindow window)
    {
        public string Name => window.Name;
        public double RemainingPercent => 100 - window.UsedPercent;
        public string RemainingText => RemainingPercent.ToString("0.#", CultureInfo.CurrentCulture) + "% left";
        public string ResetText => window.ResetsAt is { } reset
            ? $"{window.UsedPercent:0.#}% used · Resets {reset.ToLocalTime():g}"
            : $"{window.UsedPercent:0.#}% used · Reset time unavailable";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        IsVisibleChanged -= OnVisibilityChanged;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        _request?.Cancel();
        _service.Dispose();
    }
}
