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

    protected override bool Initialize()
    {
        var config = BridgeConfiguration.Load();
        _service = new ProBridgeService(config);
        _service.Start();
        return true;
    }

    protected override bool CanUnload()
    {
        _service?.Dispose();
        _service = null;
        return true;
    }
}
