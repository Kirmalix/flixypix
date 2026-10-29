using System.IO;
using System.Text.Json;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

internal static class SourceProtection
{
 internal static void Configure(IEnumerable<string> additionalPaths)
 {
  var paths=additionalPaths.ToList();
  var root=FindCheckout(AppContext.BaseDirectory)??FindCheckout(Environment.CurrentDirectory);
  if(root!=null)
  {
   var original=Directory.GetParent(root)!.FullName;
   using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"archive","manifest.json")));
   var entries=manifest.RootElement.GetProperty("directories").EnumerateArray().Select(e=>e.GetString()!)
    .Concat(manifest.RootElement.GetProperty("files").EnumerateArray().Select(e=>e.GetProperty("path").GetString()!));
   foreach(var top in entries.Select(e=>e.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase))
   {
    if(string.IsNullOrWhiteSpace(top)||top is "." or ".."||Path.IsPathRooted(top)||top.Contains('\\')||top.Contains(':')||top.Equals("preservation",StringComparison.OrdinalIgnoreCase))
     throw new InvalidDataException(T("Invalid original path in the preservation manifest."));
    paths.Add(Path.Combine(original,top));
   }
   paths.AddRange([Path.Combine(root,"archive","original"),Path.Combine(original,"mmvdb.sdf"),Path.Combine(original,"mmvdb-password.txt")]);
  }
  FileAccessPolicy.ConfigureCurrent(paths);
 }
 private static string? FindCheckout(string start)
 {
  var folder=new DirectoryInfo(Path.GetFullPath(start));
  for(var depth=0;folder!=null&&depth<10;depth++,folder=folder.Parent)
   if(File.Exists(Path.Combine(folder.FullName,"FlixyPix.slnx"))&&File.Exists(Path.Combine(folder.FullName,"archive","manifest.json")))return folder.FullName;
  return null;
 }
}
