using System.Buffers.Binary;
using System.Text;
using ImageMagick;

namespace FlixyPix.Imaging;

/// <summary>Writes a small, valid camera RAW file (DNG) for tests: a Bayer mosaic of a colour gradient and, as cameras do,
/// an embedded JPEG preview. The preview carries a white square in its top left corner that the mosaic lacks, so a test
/// can tell which of the two it was shown.</summary>
internal static class SyntheticDng
{
    /// <param name="previewWidth">Width of the embedded preview; 0 writes a RAW file without one.</param>
    /// <param name="orientation">EXIF orientation (1 to 8) of the RAW file; the preview is stored unturned, as cameras do.</param>
    internal static void Write(string path, int width = 96, int height = 64, int previewWidth = 96, int orientation = 1, string model = "FlixyPix Test Camera", string taken = "2024:05:17 14:03:21", string? xmp = null)
    {
        if (width % 2 != 0 || height % 2 != 0 || width < 8 || height < 8) throw new ArgumentOutOfRangeException(nameof(width));
        var mosaic = new byte[width * height * 2];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                // RGGB: red grows to the right, green downward, blue stays in the middle.
                var value = (y % 2, x % 2) switch { (0, 0) => 6000 + 50000 * x / width, (1, 1) => 30000, _ => 6000 + 50000 * y / height };
                BinaryPrimitives.WriteUInt16LittleEndian(mosaic.AsSpan((y * width + x) * 2), (ushort)value);
            }
        byte[] preview = [];
        var previewHeight = previewWidth * height / width;
        if (previewWidth > 0)
        {
            using var picture = new MagickImage(new MagickColor("#3c6ea0"), (uint)previewWidth, (uint)previewHeight);
            new ImageMagick.Drawing.Drawables().FillColor(MagickColors.White).Rectangle(0, 0, previewWidth / 4.0, previewHeight / 4.0).Draw(picture);
            picture.Quality = 90; preview = picture.ToByteArray(MagickFormat.Jpeg);
        }

        var file = new MemoryStream(); var writer = new BinaryWriter(file);
        writer.Write("II"u8); writer.Write((ushort)42); writer.Write(0u);
        long Put(byte[] bytes) { if (file.Position % 2 == 1) writer.Write((byte)0); var at = file.Position; writer.Write(bytes); return at; }
        var mosaicAt = Put(mosaic); var previewAt = preview.Length > 0 ? Put(preview) : 0;

        var raw = new Ifd();
        raw.Long(254, 0); raw.Long(256, (uint)width); raw.Long(257, (uint)height); raw.Short(258, 16); raw.Short(259, 1); raw.Short(262, 32803);
        raw.Long(273, (uint)mosaicAt); raw.Short(277, 1); raw.Long(278, (uint)height); raw.Long(279, (uint)mosaic.Length); raw.Short(284, 1);
        raw.Short(33421, 2, 2); raw.Bytes(33422, 0, 1, 1, 2); raw.Bytes(50710, 0, 1, 2); raw.Short(50711, 1);
        raw.Short(50713, 1, 1); raw.Long(50714, 0); raw.Long(50717, 65535);
        var exif = new Ifd(); exif.Ascii(36867, taken); exif.Rational(33434, 1, 250); exif.Rational(33437, 28, 10); exif.Short(34855, 200);

        var main = new Ifd();
        if (preview.Length > 0)
        {
            main.Long(254, 1); main.Long(256, (uint)previewWidth); main.Long(257, (uint)previewHeight); main.Short(258, 8, 8, 8); main.Short(259, 7); main.Short(262, 6);
            main.Long(273, (uint)previewAt); main.Short(277, 3); main.Long(278, (uint)previewHeight); main.Long(279, (uint)preview.Length);
        }
        main.Ascii(271, "FlixyPix"); main.Ascii(272, model); main.Short(274, (ushort)orientation); main.Ascii(306, taken);
        if (xmp != null) main.Bytes(700, Encoding.UTF8.GetBytes(xmp));
        main.Bytes(50706, 1, 4, 0, 0); main.Bytes(50707, 1, 1, 0, 0); main.Ascii(50708, model);
        // sRGB to XYZ (D65), so the colours of the mosaic come out as they went in.
        main.SignedRational(50721, [(32405, 10000), (-15371, 10000), (-4985, 10000), (-9693, 10000), (18760, 10000), (416, 10000), (556, 10000), (-2040, 10000), (10572, 10000)]);
        main.Rational(50728, 1, 1, 1, 1, 1, 1); main.Short(50778, 21);

        // Without a preview the mosaic is the first image of the file; otherwise it hangs below the preview, as in camera files.
        long mainAt;
        if (preview.Length == 0)
        {
            foreach (var entry in raw.Entries.Where(entry => entry.Tag != 254)) main.Entries.Add(entry);
            main.Long(254, 0); main.Long(34665, 0);
            var exifAt = exif.WriteTo(file, writer); main.Entries.RemoveAll(entry => entry.Tag == 34665); main.Long(34665, (uint)exifAt);
            mainAt = main.WriteTo(file, writer);
        }
        else
        {
            var rawAt = raw.WriteTo(file, writer); var exifAt = exif.WriteTo(file, writer);
            main.Long(330, (uint)rawAt); main.Long(34665, (uint)exifAt);
            mainAt = main.WriteTo(file, writer);
        }
        file.Position = 4; writer.Write((uint)mainAt);
        File.WriteAllBytes(path, file.ToArray());
    }

    private sealed class Ifd
    {
        internal readonly List<(ushort Tag, ushort Type, uint Count, byte[] Data)> Entries = [];
        internal void Short(ushort tag, params ushort[] values) => Add(tag, 3, values.Length, values.SelectMany(BitConverter.GetBytes));
        internal void Long(ushort tag, params uint[] values) => Add(tag, 4, values.Length, values.SelectMany(BitConverter.GetBytes));
        internal void Bytes(ushort tag, params byte[] values) => Add(tag, 1, values.Length, values);
        internal void Ascii(ushort tag, string text) { var bytes = Encoding.ASCII.GetBytes(text + "\0"); Add(tag, 2, bytes.Length, bytes); }
        internal void Rational(ushort tag, params uint[] parts) => Add(tag, 5, parts.Length / 2, parts.SelectMany(BitConverter.GetBytes));
        internal void SignedRational(ushort tag, (int Numerator, int Denominator)[] values) => Add(tag, 10, values.Length, values.SelectMany(value => BitConverter.GetBytes(value.Numerator).Concat(BitConverter.GetBytes(value.Denominator))));
        private void Add(ushort tag, ushort type, int count, IEnumerable<byte> data) => Entries.Add((tag, type, (uint)count, data.ToArray()));

        /// <summary>Writes the values that do not fit into an entry, then the directory; returns where the directory starts.</summary>
        internal long WriteTo(MemoryStream file, BinaryWriter writer)
        {
            var placed = new List<(ushort Tag, ushort Type, uint Count, byte[] Value)>();
            foreach (var entry in Entries.OrderBy(entry => entry.Tag))
            {
                if (entry.Data.Length <= 4) { var inline = new byte[4]; entry.Data.CopyTo(inline, 0); placed.Add((entry.Tag, entry.Type, entry.Count, inline)); continue; }
                if (file.Position % 2 == 1) writer.Write((byte)0);
                var at = (uint)file.Position; writer.Write(entry.Data);
                placed.Add((entry.Tag, entry.Type, entry.Count, BitConverter.GetBytes(at)));
            }
            if (file.Position % 2 == 1) writer.Write((byte)0);
            var start = file.Position;
            writer.Write((ushort)placed.Count);
            foreach (var entry in placed) { writer.Write(entry.Tag); writer.Write(entry.Type); writer.Write(entry.Count); writer.Write(entry.Value); }
            writer.Write(0u);
            return start;
        }
    }
}
