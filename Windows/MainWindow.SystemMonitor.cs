using VNotch.Presenters;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    #region System Monitor Widget

    private SystemMonitorPresenter? _systemMonitorPresenter;

    internal void InitializeSystemMonitorPresenter()
    {
        if (_systemMonitorPresenter != null) return;

        // Keep legacy named elements in the window's XAML namescope, then host them in the page.
        SystemMonitorWidgetContent.Children.Remove(SysMonSystemContent);
        SysMonSystemContent.Visibility = System.Windows.Visibility.Visible;
        SysMonWidgetView.SystemContent = SysMonSystemContent;

        var refs = new SystemMonitorViewRefs(
            SysMonCpuValueText,
            SysMonCpuBar,
            SysMonRamValueText,
            SysMonRamBar,
            SysMonNetDownText,
            SysMonNetUpText)
        {
            Widget = SysMonWidgetView
        };

        _systemMonitorPresenter = new SystemMonitorPresenter(_systemMonitorModule, new DispatcherService(Dispatcher), refs);
    }

    internal void DisposeSystemMonitorPresenter()
    {
        _systemMonitorPresenter?.Dispose();
        SysMonWidgetView.Dispose();
        _systemMonitorPresenter = null;
    }

    private double _lastNetDownBytesPerSec => _systemMonitorPresenter?.LastNetDownBytesPerSec ?? 0;
    private double _lastNetUpBytesPerSec => _systemMonitorPresenter?.LastNetUpBytesPerSec ?? 0;

    #endregion
}
