using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Dialogs;

namespace ArcGisProBridgeAddIn;

internal sealed class StatusButton : Button
{
    protected override void OnClick()
    {
        var service = Module1.Current.Service;
        var status = service?.GetLastStatusSummary() ?? "Bridge service has not started.";
        MessageBox.Show(status, "ArcGIS Pro MCP Bridge");
    }
}
