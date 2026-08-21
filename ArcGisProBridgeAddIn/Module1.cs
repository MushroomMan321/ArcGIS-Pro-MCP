using ArcGisProBridgeAddIn.Pane;
using ArcGisProBridgeContracts;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;

namespace ArcGisProBridgeAddIn;

internal sealed class Module1 : Module
{
    private static Module1? _this;
    private ProBridgeService? _service;

    public static Module1 Current => _this ??= (Module1)FrameworkApplication.FindModule("ArcGisProBridgeAddIn_Module");

    public ProBridgeService? Service => _service;

    /// <summary>
    /// The configuration the bridge service is running with. The Claude pane
    /// reads it so a session it starts is wired to the same pipe and policy as
    /// the bridge itself, rather than re-resolving the config independently.
    /// </summary>
    public BridgeConfiguration? Configuration { get; private set; }

    protected override bool Initialize()
    {
        var config = BridgeConfiguration.Load();
        Configuration = config;
        _service = new ProBridgeService(config);
        _service.Start();
        return true;
    }

    protected override bool CanUnload()
    {
        // The pane owns a child process; end it before the bridge so it cannot
        // outlive the session it was talking to.
        ClaudePaneView.ShutdownActiveHost();
        _service?.Dispose();
        _service = null;
        return true;
    }
}
