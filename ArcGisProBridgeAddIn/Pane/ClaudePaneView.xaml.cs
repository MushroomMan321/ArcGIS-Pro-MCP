using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ArcGisProBridgeAddIn.Pane;

/// <summary>
/// Content of the Claude dock pane: a native header strip over a web view
/// running the terminal.
///
/// Header state is set imperatively rather than through bindings. There is very
/// little of it, it changes from exactly one event, and keeping it out of the
/// dock pane view model means the session is not rebuilt when ArcGIS Pro
/// re-creates the pane's data context.
/// </summary>
public partial class ClaudePaneView : UserControl
{
    private static ClaudeTerminalHost? _activeHost;

    private ClaudePaneTheme _theme = ClaudePaneTheme.Current();
    private bool _initialized;

    public ClaudePaneView()
    {
        InitializeComponent();

        ApplyTheme();
        RestartButton.Click += OnRestartClicked;
        Loaded += OnLoaded;
        IsVisibleChanged += (_, _) => UpdateProjectName();

        SetStatus(ClaudeSessionStatus.Starting, "Preparing the pane…");
        UpdateProjectName();
    }

    /// <summary>
    /// Ends the session that the pane is running, if any. Called when the module
    /// unloads so the Claude Code process does not outlive ArcGIS Pro.
    /// </summary>
    internal static void ShutdownActiveHost()
    {
        var host = _activeHost;
        _activeHost = null;
        host?.Dispose();
    }

    private void ApplyTheme()
    {
        Resources["PaneSurface"] = ClaudePaneTheme.Brush(_theme.Surface);
        Resources["PaneSurfaceMuted"] = ClaudePaneTheme.Brush(_theme.SurfaceMuted);
        Resources["PaneForeground"] = ClaudePaneTheme.Brush(_theme.Foreground);
        Resources["PaneForegroundMuted"] = ClaudePaneTheme.Brush(_theme.ForegroundMuted);
        Resources["PaneBorder"] = ClaudePaneTheme.Brush(_theme.Border);
        Resources["PaneAccent"] = ClaudePaneTheme.Brush(_theme.Accent);
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        var host = new ClaudeTerminalHost(Browser);
        host.StatusChanged += OnStatusChanged;
        _activeHost = host;

        try
        {
            await host.InitializeAsync();
        }
        catch (Exception exception)
        {
            // Almost always a missing or blocked WebView2 runtime. The terminal
            // page cannot report it, so the fallback text has to.
            Browser.Visibility = Visibility.Collapsed;
            FallbackText.Visibility = Visibility.Visible;
            FallbackText.Text =
                "The Claude pane could not start its web view.\n\n" +
                exception.Message +
                "\n\nThe Microsoft Edge WebView2 runtime is required. ArcGIS Pro normally " +
                "installs it, so this usually means it has been removed or blocked by policy.";
            SetStatus(ClaudeSessionStatus.Failed, exception.Message);
        }
    }

    private void OnRestartClicked(object sender, RoutedEventArgs args)
    {
        UpdateProjectName();
        _activeHost?.Restart();
    }

    private void OnStatusChanged(ClaudeSessionStatus status, string detail)
    {
        Dispatcher.Invoke(() => SetStatus(status, detail));
    }

    private void SetStatus(ClaudeSessionStatus status, string detail)
    {
        StatusDot.Fill = status switch
        {
            ClaudeSessionStatus.Running => ClaudePaneTheme.Brush(_theme.Accent),
            ClaudeSessionStatus.Starting => ClaudePaneTheme.Brush(_theme.ForegroundMuted),
            ClaudeSessionStatus.Failed => ClaudePaneTheme.Brush("#E05252"),
            _ => ClaudePaneTheme.Brush(_theme.Border)
        };

        StatusDot.ToolTip = detail;
        RestartButton.IsEnabled = status != ClaudeSessionStatus.Starting;
    }

    /// <summary>
    /// Shows which project the session is attached to. Refreshed when the pane
    /// becomes visible and on restart, which covers every point at which the
    /// answer can have changed and the user can see the header.
    /// </summary>
    private void UpdateProjectName()
    {
        string? name = null;
        try
        {
            name = ArcGIS.Desktop.Core.Project.Current?.Name;
        }
        catch (Exception)
        {
            // No project is open.
        }

        ProjectText.Text = string.IsNullOrWhiteSpace(name) ? "No project open" : name;
        ProjectText.ToolTip = ProjectText.Text;
    }
}
