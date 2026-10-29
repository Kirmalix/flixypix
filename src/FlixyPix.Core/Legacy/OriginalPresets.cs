using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Resources;
using System.Text;

namespace FlixyPix.Core;

/// <summary>Reads the adult name lists the original ships inside its program file: resource <c>tag_category_presets</c>,
/// a ZIP with one text list per preset. FlixyPix does not distribute these lists. The program file is read as data;
/// nothing of the original runs.</summary>
public static class OriginalPresets
{
    public const string ResourceFile = "MyMediaViewer.Resources.resources";
    public const string PresetsKey = "tag_category_presets";
    public const string AdultList = "Adult.txt";
    public const string Genres = "Genres (adult)", Performers = "Performers (adult)", Studios = "Studios (adult)";

    /// <summary>The program file in <paramref name="folder"/>, else the first installation found; null when there is none.</summary>
    public static string? FindProgram(string? folder = null, LegacyInstallationSearch? search = null)
    {
        if (folder != null && File.Exists(Path.Combine(folder, LegacyInstallations.ProgramFile))) return Path.GetFullPath(Path.Combine(folder, LegacyInstallations.ProgramFile));
        return LegacyInstallations.ProgramFolders(search).Select(f => Path.Combine(f, LegacyInstallations.ProgramFile)).FirstOrDefault();
    }

    /// <summary>Category → names in the original order.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadAdult(string programFile)
    {
        try
        {
            var presets = ByteArray(ManifestResource(programFile, ResourceFile), PresetsKey);
            using var zip = new ZipArchive(new MemoryStream(presets), ZipArchiveMode.Read);
            var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals(AdultList, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException(AdultList + " is missing.");
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var lists = ParseList(reader.ReadToEnd());
            foreach (var category in new[] { Genres, Performers, Studios })
                if (!lists.ContainsKey(category)) throw new InvalidDataException(category + " is missing.");
            return lists;
        }
        catch (Exception e) when (e is BadImageFormatException or InvalidOperationException or ArgumentException or EndOfStreamException)
        {
            throw new InvalidDataException(T("The program file contains no readable name lists."), e);
        }
    }

    /// <summary><c>Category=[Name]</c> starts a list; every other non-empty line is one name.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseList(string text)
    {
        var lists = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var category = "";
        foreach (var line in text.Split('\n').Select(l => l.Trim()))
        {
            if (line.StartsWith("Category=[", StringComparison.Ordinal) && line.EndsWith(']')) category = line[10..^1];
            else if (line.Length > 0)
            {
                if (!lists.TryGetValue(category, out var names)) lists[category] = names = [];
                names.Add(line);
            }
        }
        return lists.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value);
    }

    /// <summary>An embedded manifest resource of a .NET program file.</summary>
    internal static byte[] ManifestResource(string programFile, string name)
    {
        using var stream = File.OpenRead(programFile);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        foreach (var handle in metadata.ManifestResources)
        {
            var resource = metadata.GetManifestResource(handle);
            if (!resource.Implementation.IsNil || !metadata.StringComparer.Equals(resource.Name, name)) continue;
            var block = pe.GetSectionData(pe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress).GetReader();
            block.Offset = checked((int)resource.Offset);
            return block.ReadBytes(block.ReadInt32());
        }
        throw new InvalidDataException(name + " is missing.");
    }

    /// <summary>A byte array entry of a .resources file, read without deserializing anything.</summary>
    internal static byte[] ByteArray(byte[] resources, string key)
    {
        using var reader = new ResourceReader(new MemoryStream(resources));
        reader.GetResourceData(key, out var type, out var data);
        if (type is not ("ResourceTypeCode.ByteArray" or "ResourceTypeCode.Stream")) throw new InvalidDataException(key + " has type " + type + ".");
        // Byte arrays and streams carry a 32-bit length before their bytes.
        return data.AsSpan(4, BitConverter.ToInt32(data, 0)).ToArray();
    }
}
