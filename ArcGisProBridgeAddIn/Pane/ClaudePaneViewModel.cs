using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;

namespace ArcGisProBridgeAddIn.Pane;

/// <summary>
/// Dock pane registration for the Claude terminal. The pane holds no state of
/// its own: the session lives with the view, so hiding and re-showing the pane
/// does not restart Claude Code.
/// </summary>
internal class ClaudePaneViewModel : DockPane
{
    internal const string DockPaneId = "ArcGisProBridgeAddIn_ClaudePane";

    /// <summary>Shows the pane, docking it wherever the user last left it.</summary>
    internal static void Show()
    {
        FrameworkApplication.DockPaneManager.Find(DockPaneId)?.Activate();
    }
}
