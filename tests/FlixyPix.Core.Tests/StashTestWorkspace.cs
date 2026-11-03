namespace FlixyPix.Core.Tests;

internal static class StashTestWorkspace
{
 private static readonly FileStream? Lease=OpenLease();
 private static FileStream? OpenLease()
 {
  var parent=Environment.GetEnvironmentVariable("FLIXYPIX_TEST_RUN_ROOT");
  return string.IsNullOrEmpty(parent)?null:File.Open(Path.Combine(parent,"run.lock"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
 }
 public static string Create(string prefix)
 {
  var parent=Environment.GetEnvironmentVariable("FLIXYPIX_TEST_RUN_ROOT");
  GC.KeepAlive(Lease);
  return Path.Combine(string.IsNullOrEmpty(parent)?Path.GetTempPath():parent,prefix+Guid.NewGuid().ToString("N"));
 }
}
