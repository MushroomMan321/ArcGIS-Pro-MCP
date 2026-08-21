using System.IO;
using System.Reflection;
using ArcGisProBridgeContracts;

namespace ArcGisProBridgeAddIn.Pane;

/// <summary>
/// Unpacks the terminal web assets that ship inside the add-in assembly.
///
/// They are served to the web view through a virtual host mapping rather than
/// inlined into the page, which needs them on disk. The add-in install
/// directory is not writable, so they go under the same per-user folder the
/// bridge already uses for its logs and configuration.
/// </summary>
internal static class TerminalAssets
{
    private const string ResourcePrefix = "TerminalWeb/";

    private static readonly object Gate = new();
    private static string? _extractedFolder;

    /// <summary>
    /// Returns the folder holding the extracted assets, extracting them on the
    /// first call. The folder is keyed on the assembly's module version, so a
    /// rebuilt add-in never serves the previous build's page out of a stale
    /// cache, and older builds' folders are cleaned up as they are superseded.
    /// </summary>
    public static string EnsureExtracted()
    {
        lock (Gate)
        {
            if (_extractedFolder is not null)
            {
                return _extractedFolder;
            }

            var assembly = typeof(TerminalAssets).Assembly;
            var root = Path.Combine(BridgeConfiguration.GetDefaultConfigDirectory(), "pane", "web");
            var folder = Path.Combine(root, assembly.ManifestModule.ModuleVersionId.ToString("n"));

            if (!File.Exists(Path.Combine(folder, "terminal.html")))
            {
                Extract(assembly, folder);
                RemoveSupersededFolders(root, folder);
            }

            _extractedFolder = folder;
            return folder;
        }
    }

    private static void Extract(Assembly assembly, string folder)
    {
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var relativePath = resource[ResourcePrefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            var destination = Path.Combine(folder, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            using var source = assembly.GetManifestResourceStream(resource);
            if (source is null)
            {
                continue;
            }

            using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            source.CopyTo(target);
        }
    }

    private static void RemoveSupersededFolders(string root, string current)
    {
        try
        {
            foreach (var directory in Directory.GetDirectories(root))
            {
                if (!string.Equals(directory, current, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }
        catch (Exception)
        {
            // Another ArcGIS Pro instance may still be serving an older build's
            // assets. Leaving them behind is harmless.
        }
    }
}
