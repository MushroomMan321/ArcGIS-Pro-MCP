using System.Windows.Media;
using ArcGIS.Desktop.Framework;

namespace ArcGisProBridgeAddIn.Pane;

/// <summary>
/// The colours the pane uses, in both halves of it: WPF brushes for the header
/// strip and hex strings for the CSS custom properties and xterm theme in the
/// web view. Deriving both from one source is what keeps the native chrome and
/// the terminal looking like a single surface rather than a page embedded in a
/// panel.
/// </summary>
internal sealed class ClaudePaneTheme
{
    private ClaudePaneTheme(
        bool isDark,
        string surface,
        string surfaceMuted,
        string foreground,
        string foregroundMuted,
        string border,
        string accent,
        string scrollbar,
        string scrollbarHover,
        IReadOnlyDictionary<string, string> terminalColors)
    {
        IsDark = isDark;
        Surface = surface;
        SurfaceMuted = surfaceMuted;
        Foreground = foreground;
        ForegroundMuted = foregroundMuted;
        Border = border;
        Accent = accent;
        Scrollbar = scrollbar;
        ScrollbarHover = scrollbarHover;
        TerminalColors = terminalColors;
    }

    public bool IsDark { get; }

    public string Surface { get; }

    public string SurfaceMuted { get; }

    public string Foreground { get; }

    public string ForegroundMuted { get; }

    public string Border { get; }

    public string Accent { get; }

    public string Scrollbar { get; }

    public string ScrollbarHover { get; }

    /// <summary>The xterm theme object, passed through to the web view as-is.</summary>
    public IReadOnlyDictionary<string, string> TerminalColors { get; }

    /// <summary>
    /// Reads the current ArcGIS Pro application theme. Pro can raise this from a
    /// background context during startup, so failures fall back to the dark
    /// palette rather than propagating.
    /// </summary>
    public static ClaudePaneTheme Current()
    {
        var isDark = true;
        try
        {
            isDark = FrameworkApplication.ApplicationTheme != ApplicationTheme.Default;
        }
        catch (Exception)
        {
            // Pro's theme is unavailable this early; the dark palette is the
            // safer default because Pro ships dark out of the box.
        }

        return isDark ? Dark() : Light();
    }

    private static ClaudePaneTheme Dark()
    {
        return new ClaudePaneTheme(
            isDark: true,
            surface: "#252526",
            surfaceMuted: "#2D2D30",
            foreground: "#D4D4D4",
            foregroundMuted: "#9A9A9A",
            border: "#3F3F46",
            accent: "#D97757",
            scrollbar: "#4E4E52",
            scrollbarHover: "#6A6A70",
            terminalColors: new Dictionary<string, string>
            {
                ["background"] = "#252526",
                ["foreground"] = "#D4D4D4",
                ["cursor"] = "#D97757",
                ["cursorAccent"] = "#252526",
                ["selectionBackground"] = "#264F78",
                ["selectionInactiveBackground"] = "#3A3D41",
                ["black"] = "#3A3A3A",
                ["red"] = "#E06C60",
                ["green"] = "#8CC265",
                ["yellow"] = "#D5B06B",
                ["blue"] = "#61AFEF",
                ["magenta"] = "#C678DD",
                ["cyan"] = "#56B6C2",
                ["white"] = "#D4D4D4",
                ["brightBlack"] = "#5A5A5A",
                ["brightRed"] = "#FF7A70",
                ["brightGreen"] = "#A5E075",
                ["brightYellow"] = "#E6C77C",
                ["brightBlue"] = "#7FC0FF",
                ["brightMagenta"] = "#D9A0EA",
                ["brightCyan"] = "#6FD3DE",
                ["brightWhite"] = "#FFFFFF"
            });
    }

    private static ClaudePaneTheme Light()
    {
        return new ClaudePaneTheme(
            isDark: false,
            surface: "#FFFFFF",
            surfaceMuted: "#F3F3F3",
            foreground: "#1F1F1F",
            foregroundMuted: "#6A6A6A",
            border: "#D6D6D6",
            accent: "#C15F3C",
            scrollbar: "#C4C4C4",
            scrollbarHover: "#A8A8A8",
            terminalColors: new Dictionary<string, string>
            {
                ["background"] = "#FFFFFF",
                ["foreground"] = "#1F1F1F",
                ["cursor"] = "#C15F3C",
                ["cursorAccent"] = "#FFFFFF",
                ["selectionBackground"] = "#ADD6FF",
                ["selectionInactiveBackground"] = "#E4E6F1",
                ["black"] = "#1F1F1F",
                ["red"] = "#C72E2E",
                ["green"] = "#237C24",
                ["yellow"] = "#8A6A00",
                ["blue"] = "#1A56C4",
                ["magenta"] = "#9A2FA8",
                ["cyan"] = "#0E6E7A",
                ["white"] = "#6A6A6A",
                ["brightBlack"] = "#4A4A4A",
                ["brightRed"] = "#E24B4B",
                ["brightGreen"] = "#2E9B2F",
                ["brightYellow"] = "#A8830B",
                ["brightBlue"] = "#2C6FE0",
                ["brightMagenta"] = "#B44BC2",
                ["brightCyan"] = "#1590A0",
                ["brightWhite"] = "#1F1F1F"
            });
    }

    /// <summary>The CSS custom properties consumed by terminal.css.</summary>
    public IReadOnlyDictionary<string, string> ToCssVariables()
    {
        return new Dictionary<string, string>
        {
            ["surface"] = Surface,
            ["surface-muted"] = SurfaceMuted,
            ["foreground"] = Foreground,
            ["foreground-muted"] = ForegroundMuted,
            ["border"] = Border,
            ["accent"] = Accent,
            ["scrollbar"] = Scrollbar,
            ["scrollbar-hover"] = ScrollbarHover
        };
    }

    public static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public static System.Drawing.Color DrawingColor(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        return System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
    }
}
