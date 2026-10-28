using System.Windows;
using System.Windows.Controls;
using VNotch.Controls;
using VNotch.Models;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SystemMonitorStorageTests
{
    [Fact]
    public void SelectedDriveSurvivesReorderAndCapacityRefreshThenFallsBackOnRemoval() => SharedStaTestRunner.Run(() =>
    {
        using var widget = new SystemMonitorWidget();
        var picker = (ComboBox)widget.FindName("DrivePicker");
        var bar = (ProgressBar)widget.FindName("CapacityBar");
        widget.Present(new SystemMonitorInfo { StorageDrives = [new("C:\\", 1000, 600), new("D:\\", 2000, 500)] });
        picker.SelectedItem = "D:\\";
        Assert.Equal(75, bar.Value);
        widget.Present(new SystemMonitorInfo { StorageDrives = [new("D:\\", 2000, 800), new("C:\\", 1000, 600)] });
        Assert.Equal("D:\\", widget.SelectedDriveName);
        Assert.Equal(60, bar.Value);
        widget.Present(new SystemMonitorInfo { StorageDrives = [new("C:\\", 1000, 600)] });
        Assert.Equal("C:\\", widget.SelectedDriveName);
        Assert.Equal(40, bar.Value);
    });

    [Fact]
    public void UnavailableSnapshotClearsOldCapacityAndDoesNotImplyIdleDisk() => SharedStaTestRunner.Run(() =>
    {
        using var widget = new SystemMonitorWidget();
        widget.Present(new SystemMonitorInfo
        {
            StorageDrives = [new("C:\\", 1000, 0)],
            DiskReadBytesPerSec = 1024, DiskWriteBytesPerSec = 0, DiskActivePercent = 100
        });
        Assert.Equal(100, ((ProgressBar)widget.FindName("CapacityBar")).Value);
        Assert.Equal("0 B/s", ((TextBlock)widget.FindName("DiskWriteText")).Text);
        widget.Present(new SystemMonitorInfo());
        Assert.Null(widget.SelectedDriveName);
        Assert.Equal(Visibility.Hidden, ((ProgressBar)widget.FindName("CapacityBar")).Visibility);
        Assert.Equal("—", ((TextBlock)widget.FindName("CapacityText")).Text);
        Assert.Equal("—", ((TextBlock)widget.FindName("DiskReadText")).Text);
        Assert.Equal("—", ((TextBlock)widget.FindName("DiskActiveText")).Text);
    });
}
