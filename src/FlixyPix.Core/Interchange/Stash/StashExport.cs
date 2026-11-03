using System.Text.Json;

namespace FlixyPix.Core;

public enum StashEntityKind { Scene,Image,Gallery,Performer,Studio,Tag,Group,File,Folder,SavedFilter }
public enum StashIssueSeverity { Warning,Error }
public sealed record StashReadIssue(string Code,StashIssueSeverity Severity,string Message);
public sealed record StashSourceDocument(long Ordinal,StashEntityKind Kind,string RelativePath,string Sha256,string? NaturalKey,
 byte[] OriginalBytes,JsonElement? Document,IReadOnlyList<StashReadIssue> Issues)
{
 public bool Valid=>Document!=null&&!Issues.Any(issue=>issue.Severity==StashIssueSeverity.Error);
}
public sealed record StashReadProgress(string RelativePath,int Files,long Bytes);
public sealed record StashReadLimits(int MaximumFiles=400_000,int MaximumEntityBytes=64*1024*1024,long MaximumTotalBytes=16L*1024*1024*1024)
{
 internal void Validate()
 {if(MaximumFiles is <1 or >400_000||MaximumEntityBytes is <1024 or >64*1024*1024||MaximumTotalBytes<MaximumEntityBytes||MaximumTotalBytes>64L*1024*1024*1024)throw new ArgumentOutOfRangeException(nameof(StashReadLimits));}
}

/// <summary>Pinned folder export contract; native references and unsupported source fields are retained verbatim.</summary>
public static partial class StashExport
{
 public const string Version="0.31.1";
 public const string SourceCommit="4de2351e7cc990d7ccd7cb6c84c275cd53bf6e55";
 internal static readonly IReadOnlyDictionary<string,StashEntityKind> Sections=new Dictionary<string,StashEntityKind>(StringComparer.Ordinal)
 { ["scenes"]=StashEntityKind.Scene,["images"]=StashEntityKind.Image,["galleries"]=StashEntityKind.Gallery,["performers"]=StashEntityKind.Performer,
  ["studios"]=StashEntityKind.Studio,["tags"]=StashEntityKind.Tag,["movies"]=StashEntityKind.Group,["files"]=StashEntityKind.File,["saved_filters"]=StashEntityKind.SavedFilter };
}
