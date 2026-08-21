using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using ArcGisProBridgeAddIn.Terminal;
using ArcGisProBridgeContracts;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ArcGisProBridgeAddIn.Pane;

internal enum ClaudeSessionStatus
{
    Starting,
    Running,
    Stopped,
    Failed
}

/// <summary>
/// Joins the two halves of the pane: a web view rendering xterm.js, and a
/// Claude Code process running under a pseudo console. Everything crossing
/// between them goes through the small JSON protocol described in terminal.js.
/// </summary>
internal sealed class ClaudeTerminalHost : IDisposable
{
    private const string VirtualHost = "arcgis-pro-mcp-terminal.invalid";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly WebView2 _webView;
    private readonly StringBuilder _pendingOutput = new();
    private readonly object _outputLock = new();

    private ClaudePaneTheme _theme = ClaudePaneTheme.Current();
    private PseudoConsoleSession? _session;
    private short _columns = 80;
    private short _rows = 24;
    private bool _flushScheduled;
    private bool _disposed;

    public ClaudeTerminalHost(WebView2 webView)
    {
        _webView = webView;
    }

    public event Action<ClaudeSessionStatus, string>? StatusChanged;

    /// <summary>
    /// Prepares the web view and navigates to the terminal page. The Claude Code
    /// process is not started here: the page reports its measured size first, so
    /// the session is created already knowing its real dimensions.
    /// </summary>
    public async Task InitializeAsync()
    {
        _theme = ClaudePaneTheme.Current();

        // Paint the pane in the theme colour before navigation so a dark Pro
        // does not flash a white rectangle while the page loads.
        _webView.DefaultBackgroundColor = ClaudePaneTheme.DrawingColor(_theme.Surface);

        var assets = TerminalAssets.EnsureExtracted();
        var environment = await CreateEnvironmentAsync();
        await _webView.EnsureCoreWebView2Async(environment);

        var core = _webView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;

        // Without this the browser swallows shortcuts such as Ctrl+P and Ctrl+F
        // that belong to the terminal.
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;

        core.SetVirtualHostNameToFolderMapping(VirtualHost, assets, CoreWebView2HostResourceAccessKind.Deny);
        core.WebMessageReceived += OnWebMessageReceived;
        core.PermissionRequested += OnPermissionRequested;
        core.NewWindowRequested += OnNewWindowRequested;

        core.Navigate($"https://{VirtualHost}/terminal.html");
    }

    /// <summary>
    /// ArcGIS Pro hosts web views of its own, and a process is limited in which
    /// user data folders it can use at once. Prefer the folder Pro has already
    /// chosen, fall back to a private one, and fall back again to the default
    /// rather than leaving the pane dead if either is refused.
    /// </summary>
    private static async Task<CoreWebView2Environment?> CreateEnvironmentAsync()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER")))
        {
            return null;
        }

        var folder = Path.Combine(BridgeConfiguration.GetDefaultConfigDirectory(), "pane", "webview");

        try
        {
            Directory.CreateDirectory(folder);
            return await CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: folder);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Restart()
    {
        StopSession(waitForExit: false);
        Post(new { kind = "reset" });
        Post(new { kind = "overlay", visible = true, title = "Restarting Claude Code…", detail = string.Empty });
        StartSession();
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (_disposed)
        {
            return;
        }

        JsonElement message;
        try
        {
            message = JsonDocument.Parse(args.WebMessageAsJson).RootElement;
        }
        catch (JsonException)
        {
            return;
        }

        if (!message.TryGetProperty("kind", out var kindProperty) || kindProperty.ValueKind != JsonValueKind.String)
        {
            return;
        }

        switch (kindProperty.GetString())
        {
            case "hello":
                SendInitialize();
                break;
            case "ready":
                ReadSize(message);
                StartSession();
                break;
            case "input":
                if (message.TryGetProperty("data", out var input) && input.ValueKind == JsonValueKind.String)
                {
                    _session?.Write(input.GetString() ?? string.Empty);
                }

                break;
            case "inputBytes":
                if (message.TryGetProperty("data", out var bytes) && bytes.ValueKind == JsonValueKind.String)
                {
                    WriteBinary(bytes.GetString());
                }

                break;
            case "resize":
                ReadSize(message);
                _session?.Resize(_columns, _rows);
                break;
            case "link":
                if (message.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                {
                    OpenExternally(url.GetString());
                }

                break;
        }
    }

    private void WriteBinary(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return;
        }

        try
        {
            _session?.Write(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            // Malformed payloads can only come from a broken page; drop them.
        }
    }

    private void ReadSize(JsonElement message)
    {
        if (message.TryGetProperty("cols", out var cols) && cols.TryGetInt32(out var columnCount))
        {
            _columns = (short)Math.Clamp(columnCount, 1, short.MaxValue);
        }

        if (message.TryGetProperty("rows", out var rows) && rows.TryGetInt32(out var rowCount))
        {
            _rows = (short)Math.Clamp(rowCount, 1, short.MaxValue);
        }
    }

    private void SendInitialize()
    {
        var options = LoadConfiguration().ClaudePane;

        Post(new
        {
            kind = "init",
            chrome = _theme.ToCssVariables(),
            theme = _theme.TerminalColors,
            font = new
            {
                family = options.FontFamily,
                size = options.FontSize,
                lineHeight = options.LineHeight
            },
            conptyBuildNumber = Environment.OSVersion.Version.Build
        });
    }

    private static BridgeConfiguration LoadConfiguration()
    {
        return Module1.Current.Configuration ?? BridgeConfiguration.Load();
    }

    private void StartSession()
    {
        if (_disposed || _session is not null)
        {
            return;
        }

        SetStatus(ClaudeSessionStatus.Starting, "Starting Claude Code…");

        try
        {
            var configuration = LoadConfiguration();
            var plan = ClaudeLauncher.CreatePlan(configuration, ResolveWorkingDirectory(configuration));

            var session = PseudoConsoleSession.Start(new PseudoConsoleStartInfo(
                plan.CommandLine,
                plan.WorkingDirectory,
                plan.EnvironmentOverrides,
                _columns,
                _rows));

            session.OutputReceived += OnOutputReceived;
            session.Exited += OnSessionExited;
            _session = session;

            Post(new { kind = "overlay", visible = false });
            Post(new { kind = "focus" });
            SetStatus(ClaudeSessionStatus.Running, $"Claude Code running in {plan.WorkingDirectory}");
        }
        catch (Exception exception)
        {
            ShowFailure(exception);
        }
    }

    /// <summary>
    /// Starts the session in the open project's home folder so Claude Code's
    /// own file tools land on the user's data by default.
    /// </summary>
    private static string ResolveWorkingDirectory(BridgeConfiguration configuration)
    {
        var configured = configuration.ClaudePane.WorkingDirectory;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var expanded = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
            if (Directory.Exists(expanded))
            {
                return expanded;
            }
        }

        try
        {
            var home = ArcGIS.Desktop.Core.Project.Current?.HomeFolderPath;
            if (!string.IsNullOrWhiteSpace(home) && Directory.Exists(home))
            {
                return home;
            }
        }
        catch (Exception)
        {
            // No project is open yet, or Pro is still loading one.
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private void ShowFailure(Exception exception)
    {
        var detail = exception is ClaudeLaunchException
            ? exception.Message
            : $"{exception.Message}\n\nSee the add-in log for details.";

        Post(new
        {
            kind = "overlay",
            visible = true,
            title = "Claude Code could not start",
            detail
        });

        SetStatus(ClaudeSessionStatus.Failed, exception.Message);
    }

    /// <summary>
    /// Buffers output and flushes it from a single queued dispatcher callback.
    /// A busy session produces reads far faster than the UI thread can service
    /// them individually, and posting each one separately makes the pane stutter.
    /// </summary>
    private void OnOutputReceived(string text)
    {
        lock (_outputLock)
        {
            _pendingOutput.Append(text);
            if (_flushScheduled)
            {
                return;
            }

            _flushScheduled = true;
        }

        _webView.Dispatcher.BeginInvoke(new Action(FlushOutput), DispatcherPriority.Background);
    }

    private void FlushOutput()
    {
        string chunk;
        lock (_outputLock)
        {
            chunk = _pendingOutput.ToString();
            _pendingOutput.Clear();
            _flushScheduled = false;
        }

        if (chunk.Length > 0)
        {
            Post(new { kind = "output", data = chunk });
        }
    }

    private void OnSessionExited(int exitCode)
    {
        _webView.Dispatcher.BeginInvoke(new Action(() =>
        {
            // Any output the process produced on its way out is still queued, so
            // flush it before the exit notice covers the terminal.
            FlushOutput();

            var detail = exitCode == 0
                ? "The session ended. Use Restart to start a new one."
                : $"The session ended with exit code {exitCode}. Use Restart to start a new one.";

            Post(new { kind = "overlay", visible = true, title = "Claude Code exited", detail });
            SetStatus(ClaudeSessionStatus.Stopped, detail);
        }));
    }

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs args)
    {
        // The page reads the clipboard only for the explicit paste shortcuts, and
        // it is our own local page, so answer without prompting the user inside
        // a dock pane that has nowhere sensible to show a prompt.
        args.State = args.PermissionKind == CoreWebView2PermissionKind.ClipboardRead
            ? CoreWebView2PermissionState.Allow
            : CoreWebView2PermissionState.Deny;
        args.Handled = true;
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        OpenExternally(args.Uri);
    }

    /// <summary>
    /// Opens a link from the terminal in the user's browser. Only http and https
    /// are followed: terminal output is not a trusted source, and handing an
    /// arbitrary scheme to the shell would let it launch local handlers.
    /// </summary>
    private static void OpenExternally(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)
            || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // The user has no browser association; nothing useful to report.
        }
    }

    private void Post(object message)
    {
        if (_disposed || _webView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
        }
        catch (Exception)
        {
            // The web view can be torn down between the check and the post while
            // ArcGIS Pro is shutting down.
        }
    }

    private void SetStatus(ClaudeSessionStatus status, string detail)
    {
        StatusChanged?.Invoke(status, detail);
    }

    /// <summary>
    /// Ends the current session. Tearing one down waits for the child to exit,
    /// so a restart hands that off to a background thread rather than freezing
    /// ArcGIS Pro for a couple of seconds on a button click. Shutdown waits, so
    /// the process cannot outlive the application.
    /// </summary>
    private void StopSession(bool waitForExit)
    {
        var session = _session;
        _session = null;

        if (session is null)
        {
            return;
        }

        session.OutputReceived -= OnOutputReceived;
        session.Exited -= OnSessionExited;

        if (waitForExit)
        {
            session.Dispose();
        }
        else
        {
            Task.Run(() => session.Dispose());
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopSession(waitForExit: true);

        if (_webView.CoreWebView2 is { } core)
        {
            core.WebMessageReceived -= OnWebMessageReceived;
            core.PermissionRequested -= OnPermissionRequested;
            core.NewWindowRequested -= OnNewWindowRequested;
        }
    }
}
