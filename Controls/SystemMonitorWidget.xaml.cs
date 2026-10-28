using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VNotch.Models;

namespace VNotch.Controls;

public partial class SystemMonitorWidget : UserControl, IDisposable
{
    public static readonly DependencyProperty SystemContentProperty = DependencyProperty.Register(
        nameof(SystemContent), typeof(UIElement), typeof(SystemMonitorWidget));

    private IReadOnlyList<StorageDriveInfo> _drives = Array.Empty<StorageDriveInfo>();
    private string[] _driveNames = Array.Empty<string>();
    private string? _preferredDriveName;
    private bool _updatingDrives;
    private bool _disposed;

    public SystemMonitorWidget()
    {
        InitializeComponent();
        ShowCapacity(null);
    }

    public UIElement? SystemContent
    {
        get => (UIElement?)GetValue(SystemContentProperty);
        set => SetValue(SystemContentProperty, value);
    }

    public bool IsStoragePage => StoragePage.Visibility == Visibility.Visible;
    public string? SelectedDriveName => DrivePicker.SelectedItem as string;

    public void SelectPage(bool storage)
    {
        if (_disposed) return;
        SystemPageButton.IsChecked = !storage;
        StoragePageButton.IsChecked = storage;
        SystemPage.Visibility = storage ? Visibility.Collapsed : Visibility.Visible;
        StoragePage.Visibility = storage ? Visibility.Visible : Visibility.Collapsed;
    }

    // The presenter dispatches here on the UI thread; no drive or counter I/O belongs in this control.
    public void Present(SystemMonitorInfo stats)
    {
        if (_disposed) return;
        if (stats == null) throw new ArgumentNullException(nameof(stats));

        _drives = stats.StorageDrives;
        bool namesChanged = _driveNames.Length != _drives.Count;
        for (int i = 0; !namesChanged && i < _driveNames.Length; i++)
            namesChanged = !string.Equals(_driveNames[i], _drives[i].Name, StringComparison.Ordinal);

        if (namesChanged)
        {
            var names = new string[_drives.Count];
            string? selection = null;
            for (int i = 0; i < _drives.Count; i++)
            {
                names[i] = _drives[i].Name;
                if (string.Equals(names[i], _preferredDriveName, StringComparison.OrdinalIgnoreCase))
                    selection = names[i];
            }
            if (selection == null && names.Length > 0) selection = names[0];

            _updatingDrives = true;
            try
            {
                _driveNames = names;
                DrivePicker.ItemsSource = names;
                DrivePicker.SelectedItem = selection;
                DrivePicker.IsEnabled = names.Length > 0;
            }
            finally
            {
                _updatingDrives = false;
            }
            if (selection != null) _preferredDriveName = selection;
        }

        ShowSelectedCapacity();
        DiskReadText.Text = FormatRate(stats.DiskReadBytesPerSec);
        DiskWriteText.Text = FormatRate(stats.DiskWriteBytesPerSec);
        DiskActiveText.Text = stats.DiskActivePercent is double active && double.IsFinite(active) && active >= 0 && active <= 100
            ? $"{active:0}%" : "—";
    }

    private void PageButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SelectPage(ReferenceEquals(sender, StoragePageButton));
    }

    private void DrivePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (_updatingDrives || _disposed) return;
        _preferredDriveName = SelectedDriveName;
        ShowSelectedCapacity();
    }

    private void ShowSelectedCapacity()
    {
        StorageDriveInfo? selected = null;
        foreach (var drive in _drives)
        {
            if (!string.Equals(drive.Name, SelectedDriveName, StringComparison.OrdinalIgnoreCase)) continue;
            selected = drive;
            break;
        }
        ShowCapacity(selected);
    }

    private void ShowCapacity(StorageDriveInfo? drive)
    {
        if (drive == null || drive.TotalBytes == 0 || drive.FreeBytes > drive.TotalBytes)
        {
            CapacityText.Text = "—";
            FreeText.Text = "—";
            CapacityText.ToolTip = "Drive capacity unavailable.";
            FreeText.ToolTip = "Free capacity unavailable.";
            CapacityBar.Value = 0;
            CapacityBar.IsEnabled = false;
            CapacityBar.Visibility = Visibility.Hidden;
            return;
        }

        ulong used = drive.TotalBytes - drive.FreeBytes;
        const double gib = 1024.0 * 1024 * 1024;
        const double tib = gib * 1024;
        double unit = drive.TotalBytes >= tib ? tib : gib;
        string unitName = drive.TotalBytes >= tib ? "TiB" : "GiB";
        CapacityText.Text = $"{used / unit:0.0} / {drive.TotalBytes / unit:0.0} {unitName} used";
        FreeText.Text = $"{drive.FreeBytes / unit:0.0} {unitName} free";
        CapacityText.ToolTip = $"{drive.Name}: {used:N0} bytes used / {drive.TotalBytes:N0} bytes total";
        FreeText.ToolTip = $"{drive.FreeBytes:N0} bytes free";
        CapacityBar.Value = used / (double)drive.TotalBytes * 100;
        CapacityBar.IsEnabled = true;
        CapacityBar.Visibility = Visibility.Visible;
    }

    private static string FormatRate(double? bytesPerSec)
    {
        if (bytesPerSec is not double rate || !double.IsFinite(rate) || rate < 0) return "—";
        if (rate >= 1024.0 * 1024 * 1024) return $"{rate / (1024.0 * 1024 * 1024):0.0} GiB/s";
        if (rate >= 1024.0 * 1024) return $"{rate / (1024.0 * 1024):0.0} MiB/s";
        if (rate >= 1024) return $"{rate / 1024:0.0} KiB/s";
        return $"{rate:0} B/s";
    }

    // Only controls consume input: background and metric areas retain calendar widget-switch gestures.
    private void InputControl_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private void InputControl_MouseUp(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private void InputControl_MouseWheel(object sender, MouseWheelEventArgs e) => e.Handled = true;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DrivePicker.IsDropDownOpen = false;
        DrivePicker.SelectionChanged -= DrivePicker_SelectionChanged;
        SystemPageButton.Click -= PageButton_Click;
        StoragePageButton.Click -= PageButton_Click;
        DrivePicker.ItemsSource = null;
        _drives = Array.Empty<StorageDriveInfo>();
        _driveNames = Array.Empty<string>();
        SystemContent = null;
    }
}
