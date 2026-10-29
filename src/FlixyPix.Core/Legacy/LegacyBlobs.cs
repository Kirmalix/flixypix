using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;
public sealed record BlobReference(string Sha256,long Length);
public sealed partial class CatalogRepository
{
 private void StageLegacyBlob(JsonElement reference,string directory,SqliteTransaction tx,CancellationToken token)
 {
  var hash=Text(reference,"sha256");var size=Property(reference,"length");
  if(hash==null||hash.Length!=64||hash.Any(c=>!char.IsAsciiHexDigitLower(c))||size.ValueKind!=JsonValueKind.Number||!size.TryGetInt64(out var length)||length<0||length>int.MaxValue)
   throw new InvalidDataException("Invalid binary reference hash or length.");
  using(var check=Command("SELECT length FROM temp.legacy_blob_refs WHERE sha256=$hash",tx,("$hash",hash)))
  {if(check.ExecuteScalar() is long seen){if(seen!=length)throw new InvalidDataException("Conflicting binary reference lengths.");return;}}
  var path=Path.Combine(directory,hash+".bin");RejectBlobLinks(path);
  using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
  if(input.Length!=length)throw new InvalidDataException("Binary sidecar length differs from its reference.");
  StoreLegacyBlob(hash,length,input,tx,token);
  using var insert=Command("INSERT INTO temp.legacy_blob_refs VALUES($hash,$length)",tx,("$hash",hash),("$length",length));insert.ExecuteNonQuery();
 }
 private static void RejectBlobLinks(string path)
 {
  for(var current=new FileInfo(path) as FileSystemInfo;current!=null;current=current is FileInfo f?f.Directory:((DirectoryInfo)current).Parent)
   if((current.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Binary sidecars cannot pass through symbolic links or junctions.");
 }
 /// <summary>Keeps one binary value of the original: its bytes go to the picture store (verified against the hash), the
 /// row only notes hash and length.</summary>
 private void StoreLegacyBlob(string hash,long length,Stream input,SqliteTransaction tx,CancellationToken token)
 {
  using(var check=Command("SELECT length FROM legacy_blobs WHERE sha256=$hash",tx,("$hash",hash)))
   if(check.ExecuteScalar() is long known&&known!=length)throw new InvalidDataException("Stored blob length mismatch.");
  try{assets.Write(input,hash,length,token);}
  catch(InvalidDataException){throw new InvalidDataException("Binary sidecar checksum mismatch.");}
  using var create=Command("INSERT OR IGNORE INTO legacy_blobs VALUES($hash,$length)",tx,("$hash",hash),("$length",length));create.ExecuteNonQuery();
 }
 private byte[] ReadLegacyAssetBytes(JsonElement value,SqliteTransaction tx)
 {
  var inline=Property(value,"$binary");if(inline.ValueKind==JsonValueKind.String)return Convert.FromBase64String(inline.GetString()!);
  var reference=Property(value,"$binaryRef");var hash=Text(reference,"sha256")??throw new InvalidDataException("Unknown image binary representation.");
  using var command=Command("SELECT length FROM legacy_blobs WHERE sha256=$hash",tx,("$hash",hash));
  if(command.ExecuteScalar() is not long length)throw new InvalidDataException("Referenced image blob is missing.");
  if(length>MaximumAssetBytes)throw new InvalidDataException("Image exceeds the 32 MiB display-asset limit; original blob is preserved.");
  return assets.Read(hash,length)??throw new InvalidDataException("Referenced image blob is missing.");
 }
 public void ExportLegacyBlob(string sha256,string output,CancellationToken token=default)
 {
  FileAccessPolicy.Current.EnsureWritable(output);var temporary=Path.GetFullPath(output)+"."+Guid.NewGuid().ToString("N")+".tmp";
  try{lock(gate){ThrowIfDisposed();using var select=Command("SELECT length FROM legacy_blobs WHERE sha256=$hash",null,("$hash",sha256));
   if(select.ExecuteScalar() is not long length)throw new ArgumentException("Unknown legacy blob.");
   using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
   {
    try{if(!assets.CopyTo(sha256,length,stream,token))throw new InvalidDataException("Stored blob failed its checksum.");}
    catch(InvalidDataException){throw new InvalidDataException("Stored blob failed its checksum.");}
   }
   FileAccessPolicy.Current.EnsureWritable(output);File.Move(temporary,output,false);}}
  finally{if(File.Exists(temporary))File.Delete(temporary);}
 }
}
