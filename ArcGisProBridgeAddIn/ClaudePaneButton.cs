using ArcGIS.Desktop.Framework.Contracts;
using ArcGisProBridgeAddIn.Pane;

namespace ArcGisProBridgeAddIn;

internal sealed class ClaudePaneButton : Button
{
    protected override void OnClick()
    {
        ClaudePaneViewModel.Show();
    }

    protected override void OnUpdate()
    {
        var enabled = Module1.Current.Configuration?.ClaudePane.Enabled ?? true;
        Enabled = enabled;

        if (!enabled)
        {
            DisabledTooltip = "The Claude pane is turned off in the bridge configuration file (claudePane.enabled).";
        }
    }
}
