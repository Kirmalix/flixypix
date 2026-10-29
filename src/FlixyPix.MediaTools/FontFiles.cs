using Microsoft.Win32;

namespace FlixyPix.MediaTools;

/// <summary>Finds the file of an installed font family for FFmpeg's text drawing, as the original's getFontFile did.</summary>
public static class FontFiles
{
    /// <summary>Families offered for contact sheets; only installed ones are shown.</summary>
    public static IReadOnlyList<string> Suggested { get; } = ["Segoe UI", "Arial", "Verdana", "Tahoma", "Calibri", "Consolas", "Georgia"];

    /// <summary>The regular TrueType or OpenType file of <paramref name="family"/>, or null when it is not installed.</summary>
    public static string? Find(string family)
    {
        if (string.IsNullOrWhiteSpace(family) || !OperatingSystem.IsWindows()) return null;
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var userFonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
            if (key is null) continue;
            foreach (var name in key.GetValueNames())
            {
                // Entries look like "Segoe UI (TrueType)"; bold or italic faces carry their style in the name.
                var face = name.Split(" (")[0].Trim();
                if (!string.Equals(face, family.Trim(), StringComparison.OrdinalIgnoreCase) || key.GetValue(name) is not string file) continue;
                var path = Path.IsPathRooted(file) ? file : File.Exists(Path.Combine(fonts, file)) ? Path.Combine(fonts, file) : Path.Combine(userFonts, file);
                if (File.Exists(path) && Path.GetExtension(path).ToLowerInvariant() is ".ttf" or ".otf") return path;
            }
        }
        return null;
    }

    /// <summary>The family's file, else Segoe UI's or Arial's, so a sheet can always be labelled.</summary>
    public static string? FindOrDefault(string family) => Find(family) ?? Find("Segoe UI") ?? Find("Arial");
}
