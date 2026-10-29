using System.IO;
using System.Runtime.InteropServices;
using FlixyPix.Core;
using FlixyPix.Imaging;

namespace FlixyPix.Desktop;

/// <summary>Metadata of the files themselves: images through <see cref="ImageMetadataReader"/>, videos through the
/// Windows file properties as the original read them (title, subject, comment, tags, rating, director, publisher).</summary>
internal sealed class DesktopMetadataReader : IFileMetadataReader
{
 private readonly ImageMetadataReader images = new();
 public FileMetadata? Read(string path, MediaKind kind) => kind == MediaKind.Image ? images.Read(path, kind) : kind == MediaKind.Video ? WindowsProperties.Read(path) : null;
}

/// <summary>Reads Windows file properties through the shell's property store; nothing is written.</summary>
internal static class WindowsProperties
{
 private static readonly Guid Summary = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), Media = new("64440492-4C8B-11D1-8B70-080036B11A03");
 private static readonly PropertyKey Title = new(Summary, 2), Subject = new(Summary, 3), Keywords = new(Summary, 5), Comment = new(Summary, 6),
  Rating = new(Media, 9), Director = new(Media, 20), Publisher = new(Media, 30);

 internal static FileMetadata? Read(string path)
 {
  var iid = typeof(IPropertyStore).GUID;
  if (SHGetPropertyStoreFromParsingName(Path.GetFullPath(path), 0, 0, ref iid, out var store) != 0 || store == null) return null;
  try
  {
   string? Text(PropertyKey key)
   {
    var value = new PropVariant();
    if (store.GetValue(ref key, ref value) != 0) return null;
    try { return value.Type != 0 && PropVariantToStringAlloc(ref value, out var text) == 0 ? Take(text) : null; }
    finally { PropVariantClear(ref value); }
   }
   // Windows keeps the rating as 1–99 (one star = 1, five stars = 99).
   var stars = uint.TryParse(Text(Rating), out var rating) && rating > 0 ? rating switch { < 13 => 1, < 38 => 2, < 63 => 3, < 88 => 4, _ => 5 } : (int?)null;
   var people = new[] { (CatalogRepository.NfoLabel("director"), Director), (CatalogRepository.NfoLabel("studio"), Publisher) }
    .SelectMany(p => FileMetadata.SplitKeywords(Text(p.Item2)).Select(name => p.Item1 + "|" + name));
   var metadata = new FileMetadata
   {
    Title = FileMetadata.Clean(Text(Title)), Description = FileMetadata.Clean(Text(Subject)) ?? FileMetadata.Clean(Text(Comment)), Stars = stars,
    Keywords = FileMetadata.SplitKeywords(Text(Keywords)).ToArray(), HierarchicalKeywords = people.ToArray(),
   };
   return metadata.IsEmpty ? null : metadata;
  }
  finally { Marshal.ReleaseComObject(store); }
 }

 private static string? Take(nint text)
 {
  try { return Marshal.PtrToStringUni(text); } finally { Marshal.FreeCoTaskMem(text); }
 }

 [StructLayout(LayoutKind.Sequential)] private struct PropertyKey(Guid format, uint id) { public Guid Format = format; public uint Id = id; }
 [StructLayout(LayoutKind.Sequential)] private struct PropVariant { public ushort Type; public ushort Reserved1, Reserved2, Reserved3; public nint Value, Value2; }

 [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
 private interface IPropertyStore
 {
  [PreserveSig] int GetCount(out uint count);
  [PreserveSig] int GetAt(uint index, out PropertyKey key);
  [PreserveSig] int GetValue(ref PropertyKey key, ref PropVariant value);
  [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
  [PreserveSig] int Commit();
 }

 [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
 private static extern int SHGetPropertyStoreFromParsingName(string path, nint bindContext, int flags, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);
 [DllImport("propsys.dll")] private static extern int PropVariantToStringAlloc(ref PropVariant value, out nint text);
 [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
}
