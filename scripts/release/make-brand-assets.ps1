# Builds the app icon and logo images from the source art in assets/branding.
# Output (committed): src/FlixyPix.Desktop/Assets/flixypix.ico (16-256 px, PNG frames, from the tile logo)
# and flixypix-mark-<size>.png (transparent logo for the header and the empty library).
param([int]$AlphaFloor = 96)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class BrandArt
{
    // Faint specks around the art come from the cut-out; alpha below the floor becomes fully transparent.
    public static Bitmap Load(string path, int alphaFloor)
    {
        var image = new Bitmap(path);
        var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap)) g.DrawImage(image, 0, 0, image.Width, image.Height);
        image.Dispose();
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        var bytes = new byte[data.Stride * data.Height];
        Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
        for (var i = 3; i < bytes.Length; i += 4) if (bytes[i] < alphaFloor) { bytes[i] = 0; bytes[i - 1] = bytes[i - 2] = bytes[i - 3] = 0; }
        Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
        bitmap.UnlockBits(data);
        // Crop to the visible art (square, 2 % margin) so small icons use their whole area.
        int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                if (bytes[y * data.Stride + x * 4 + 3] != 0) { left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
        if (right < 0) return bitmap;
        var side = Math.Max(right - left + 1, bottom - top + 1);
        var padded = (int)Math.Ceiling(side * 1.04);
        var square = new Bitmap(padded, padded, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(square))
            g.DrawImage(bitmap, new Rectangle((padded - (right - left + 1)) / 2, (padded - (bottom - top + 1)) / 2, right - left + 1, bottom - top + 1),
                left, top, right - left + 1, bottom - top + 1, GraphicsUnit.Pixel);
        bitmap.Dispose();
        return square;
    }

    // Halving steps before the last resize keep small sizes crisp.
    public static byte[] Png(Bitmap source, int size)
    {
        Bitmap current = source;
        while (current.Width / 2 >= size * 2)
        {
            var half = Draw(current, current.Width / 2);
            if (!ReferenceEquals(current, source)) current.Dispose();
            current = half;
        }
        using (var final = Draw(current, size))
        using (var stream = new MemoryStream())
        {
            if (!ReferenceEquals(current, source)) current.Dispose();
            final.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
    }

    private static Bitmap Draw(Bitmap source, int size)
    {
        var target = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(target))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.CompositingQuality = CompositingQuality.HighQuality;
            using (var attributes = new ImageAttributes())
            {
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(source, new Rectangle(0, 0, size, size), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
            }
        }
        return target;
    }

    // ICO with PNG frames (Windows Vista and later).
    public static void Ico(string path, byte[][] frames, int[] sizes)
    {
        using (var stream = File.Create(path))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write((short)0); writer.Write((short)1); writer.Write((short)frames.Length);
            var offset = 6 + 16 * frames.Length;
            for (var i = 0; i < frames.Length; i++)
            {
                writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                writer.Write((byte)0); writer.Write((byte)0); writer.Write((short)1); writer.Write((short)32);
                writer.Write(frames[i].Length); writer.Write(offset); offset += frames[i].Length;
            }
            foreach (var frame in frames) writer.Write(frame);
        }
    }
}
'@

$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$source = Join-Path $root 'assets\branding'
$out = Join-Path $root 'src\FlixyPix.Desktop\Assets'
New-Item -ItemType Directory -Force $out | Out-Null

$tile = [BrandArt]::Load((Join-Path $source 'flixypix-logo.png'), $AlphaFloor)
$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$frames = foreach ($size in $sizes) { ,([BrandArt]::Png($tile, $size)) }
[BrandArt]::Ico((Join-Path $out 'flixypix.ico'), $frames, $sizes)
[IO.File]::WriteAllBytes((Join-Path $out 'flixypix-tile-256.png'), [BrandArt]::Png($tile, 256))
$tile.Dispose()

$mark = [BrandArt]::Load((Join-Path $source 'flixypix-logo-transparent.png'), $AlphaFloor)
foreach ($size in 96, 192) { [IO.File]::WriteAllBytes((Join-Path $out "flixypix-mark-$size.png"), [BrandArt]::Png($mark, $size)) }
$mark.Dispose()
Get-ChildItem $out | Select-Object Name, Length
