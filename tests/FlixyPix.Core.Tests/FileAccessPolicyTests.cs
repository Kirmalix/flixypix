using System.Diagnostics;
using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

[Collection("Process file access policy")]
public sealed class FileAccessPolicyTests : IDisposable
{
 private readonly string root=Path.Combine(Path.GetTempPath(),"flixypix-access-"+Guid.NewGuid().ToString("N"));
 public FileAccessPolicyTests()=>Directory.CreateDirectory(root);
 public void Dispose()=>Directory.Delete(root,true);

 [Fact]
 public void ProtectedTreeUsesCaseInsensitiveDirectoryBoundariesAndNormalizedPaths()
 {
  var source=Path.Combine(root,"source");Directory.CreateDirectory(source);
  var policy=new FileAccessPolicy([source]);
  Assert.Throws<UnauthorizedAccessException>(()=>policy.EnsureWritable(Path.Combine(source,"media.mp4")));
  Assert.Throws<UnauthorizedAccessException>(()=>policy.EnsureWritable(Path.Combine(source.ToUpperInvariant(),"child","..","media.mp4")));
  policy.EnsureWritable(Path.Combine(root,"source-other","new.mp4"));
  Assert.True(policy.IsWriteProtected(source));
 }

 [Fact]
 public void IndividualSourcesStayReadOnlyAndCopiesOutsideRemainPossible()
 {
  var source=Path.Combine(root,"source.sdf");File.WriteAllBytes(source,[1,2,3]);
  var destination=Path.Combine(root,"work-copy.sdf");var policy=new FileAccessPolicy([source]);
  Assert.Throws<UnauthorizedAccessException>(()=>policy.EnsureWritable(source));
  Assert.Equal(new byte[]{1,2,3},File.ReadAllBytes(source));
  policy.EnsureWritable(destination);File.Copy(source,destination,false);
  Assert.Equal(File.ReadAllBytes(source),File.ReadAllBytes(destination));
  Assert.Single(Directory.GetFiles(root,"work-copy.sdf"));
 }

 [Fact]
 public void ProtectedOfflineDriveIsRejectedLexicallyWithoutOpeningIt()
 {
  var policy=new FileAccessPolicy([@"U:\"]);
  Assert.Throws<UnauthorizedAccessException>(()=>policy.EnsureWritable(@"u:\offline\file.mp4"));
  Assert.Throws<UnauthorizedAccessException>(()=>policy.EnsureWritable(@"U:\offline\..\file.mp4"));
 }

 [Fact]
 public void NoDriveIsProtectedUnlessTheUserConfiguresIt()
 {
  Assert.Empty(FileAccessPolicy.Current.ProtectedPaths);
  var protectedFolder=Path.Combine(root,"protected");
  try
  {
   FileAccessPolicy.ConfigureCurrent([protectedFolder]);
   Assert.Equal(protectedFolder,Assert.Single(FileAccessPolicy.Current.ProtectedPaths));
   Assert.True(FileAccessPolicy.Current.IsWriteProtected(Path.Combine(protectedFolder,"file.mp4")));
   Assert.False(FileAccessPolicy.Current.IsWriteProtected(Path.Combine(root,"other","file.mp4")));
  }
  finally { FileAccessPolicy.ConfigureCurrent([]); }
 }

 [Fact]
 public void ProtectedFoldersCannotCoverFlixyPixsOwnData()
 {
  var data=Path.Combine(root,"app","Data");var media=Path.Combine(root,"media");
  Directory.CreateDirectory(data);Directory.CreateDirectory(media);
  Assert.True(ProtectedFolders.CollidesWithOwnData(data,[data]));
  Assert.True(ProtectedFolders.CollidesWithOwnData(root,[data]));
  Assert.True(ProtectedFolders.CollidesWithOwnData(Path.Combine(data,"Library"),[data]));
  Assert.True(ProtectedFolders.CollidesWithOwnData(Path.GetPathRoot(data)!,[data]));
  Assert.False(ProtectedFolders.CollidesWithOwnData(media,[data]));
  Assert.False(ProtectedFolders.CollidesWithOwnData(media,[@"Q:\absent-drive\library"]));
  Assert.True(ProtectedFolders.CollidesWithOwnData(@"\\?\C:\bypass",[data]));
  Assert.Equal(media,Assert.Single(ProtectedFolders.Usable([data,media,"  "],[data])));
 }

 [Fact]
 public void ALinkToOwnDataCannotBeProtected()
 {
  var data=Path.Combine(root,"Data");Directory.CreateDirectory(data);
  var alias=Path.Combine(root,"alias");Junction(alias,data);
  Assert.True(ProtectedFolders.CollidesWithOwnData(alias,[data]));
  Directory.Delete(alias);
 }

 [Fact]
 public void DeviceAndAlternateStreamOutputPathsAreRejected()
 {
  var policy=new FileAccessPolicy([]);
  Assert.Throws<UnauthorizedAccessException>(()=>policy.EnsureWritable(@"\\?\C:\bypass.txt"));
  Assert.Throws<UnauthorizedAccessException>(()=>policy.EnsureWritable(Path.Combine(root,"file.txt")+":stream"));
  Assert.Throws<UnauthorizedAccessException>(()=>policy.EnsureWritable(Path.Combine(root,"source. ","file.txt")));
  Assert.Throws<UnauthorizedAccessException>(()=>policy.EnsureWritable(Path.Combine(root,"NUL.txt")));
 }

 [Fact]
 public void JunctionAliasCannotWriteIntoAProtectedTree()
 {
  var source=Path.Combine(root,"source");Directory.CreateDirectory(source);
  var alias=Path.Combine(root,"alias");Junction(alias,source);
  var policy=new FileAccessPolicy([source]);
  Assert.Throws<UnauthorizedAccessException>(()=>policy.EnsureWritable(Path.Combine(alias,"new.txt")));
  // A protected alias also protects writes through the ordinary physical name.
  var aliasPolicy=new FileAccessPolicy([alias]);
  Assert.Throws<UnauthorizedAccessException>(()=>aliasPolicy.EnsureWritable(Path.Combine(source,"new.txt")));
  Directory.Delete(alias);Assert.True(Directory.Exists(source));
 }

 [Fact]
 public void DanglingJunctionFailsClosed()
 {
  var target=Path.Combine(root,"missing");Directory.CreateDirectory(target);
  var alias=Path.Combine(root,"dangling");Junction(alias,target);Directory.Delete(target);
  try
  {
   var policy=new FileAccessPolicy([]);
   Assert.Throws<UnauthorizedAccessException>(()=>policy.EnsureWritable(Path.Combine(alias,"new.txt")));
  }
  finally { Directory.CreateDirectory(target);Directory.Delete(alias);Directory.Delete(target); }
 }

 [Fact]
 public void CreatingAnInstanceCannotChangeProcessPolicy()
 {
  var before=FileAccessPolicy.Current;
  _=new FileAccessPolicy([root]);
  Assert.Same(before,FileAccessPolicy.Current);
 }

 private static void Junction(string alias,string target)
 {
  var start=new ProcessStartInfo("cmd.exe") { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
  start.ArgumentList.Add("/d");start.ArgumentList.Add("/c");
  start.ArgumentList.Add("mklink");start.ArgumentList.Add("/J");start.ArgumentList.Add(alias);start.ArgumentList.Add(target);
  using var process=Process.Start(start)!;var output=process.StandardOutput.ReadToEnd();var error=process.StandardError.ReadToEnd();process.WaitForExit();
  Assert.True(process.ExitCode==0,output+error);
 }
}
