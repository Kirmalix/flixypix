using System.Security.Cryptography;
using System.Text;
using FlixyPix.LegacyExporter;
using Xunit;

namespace FlixyPix.LegacyExporter.Tests;

public sealed class BlobSidecarWriterTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(),"flixypix-exporter-test-"+Guid.NewGuid().ToString("N"));
    public BlobSidecarWriterTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root,true);

    [Fact]
    public void StreamsBeyondArtworkLimitWithBoundedReads()
    {
        var directory=Path.Combine(root,"blobs"); var writer=new BlobSidecarWriter(directory);
        const long length=33L*1024*1024+17; var largest=0; long readBytes=0;
        var reference=writer.Write(length,(offset,buffer,start,count)=>
        {
            Assert.Equal(readBytes,offset); Assert.Equal(0,start); Assert.InRange(count,1,1024*1024);
            largest=Math.Max(largest,count); Array.Fill(buffer,(byte)(offset/1024/1024),0,count); readBytes+=count; return count;
        });
        var value=Assert.IsType<Dictionary<string,object>>(reference["$binaryRef"]);
        var hash=Assert.IsType<string>(value["sha256"]); Assert.Matches("^[0-9a-f]{64}$",hash);
        using var file=File.OpenRead(Path.Combine(directory,hash+".bin"));
        Assert.Equal(length,file.Length); Assert.Equal(hash,Convert.ToHexStringLower(SHA256.HashData(file)));
        Assert.Equal(length,readBytes); Assert.Equal(BlobSidecarWriter.BufferBytes,largest); Assert.Equal(1,writer.Count); Assert.Equal(length,writer.Bytes);
    }

    [Fact]
    public void IdenticalPayloadsDeduplicateAndEmptyBlobIsPreserved()
    {
        var directory=Path.Combine(root,"blobs"); var writer=new BlobSidecarWriter(directory); var data=Encoding.UTF8.GetBytes("shared bytes");
        long Read(long offset,byte[] buffer,int start,int count) { data.AsSpan((int)offset,count).CopyTo(buffer.AsSpan(start,count));return count; }
        var first=writer.Write(data.Length,Read); var second=writer.Write(data.Length,Read);
        Assert.Equal(((Dictionary<string,object>)first["$binaryRef"])["sha256"],((Dictionary<string,object>)second["$binaryRef"])["sha256"]);
        writer.Write(0,(_,_,_,_)=>throw new InvalidOperationException("Empty blobs must not invoke the source."));
        Assert.Equal(2,writer.Count);Assert.Equal(data.Length,writer.Bytes);Assert.Equal(2,Directory.GetFiles(directory).Length);
    }

    [Fact]
    public void TruncatedOrOverlongReadsNeverPublishABlob()
    {
        var directory=Path.Combine(root,"blobs"); var writer=new BlobSidecarWriter(directory);
        Assert.Throws<InvalidDataException>(()=>writer.Write(100,(_,_,_,_)=>0));
        Assert.Throws<InvalidDataException>(()=>writer.Write(100,(_,_,_,count)=>count+1));
        Assert.Throws<IOException>(()=>writer.Write(100,(_,_,_,_)=>throw new IOException("test interruption")));
        Assert.Empty(Directory.GetFileSystemEntries(directory));Assert.Equal(0,writer.Count);Assert.Equal(0,writer.Bytes);
    }

    [Fact]
    public void PublicationDoesNotOverwriteAndCommitsJsonlLast()
    {
        var temporary=Path.Combine(root,"staged.jsonl");var blobs=temporary+".blobs";var output=Path.Combine(root,"catalog.jsonl");
        File.WriteAllText(temporary,"complete\n");Directory.CreateDirectory(blobs);File.WriteAllText(Path.Combine(blobs,"blob.bin"),"bytes");
        File.WriteAllText(output,"existing");
        Assert.Throws<IOException>(()=>BlobSidecarWriter.Publish(temporary,blobs,output));Assert.Equal("existing",File.ReadAllText(output));Assert.True(Directory.Exists(blobs));
        File.Delete(output);BlobSidecarWriter.Publish(temporary,blobs,output);
        Assert.Equal("complete\n",File.ReadAllText(output));Assert.True(File.Exists(Path.Combine(output+".blobs","blob.bin")));Assert.False(Directory.Exists(blobs));
    }

    [Fact]
    public void FailedJsonlCommitRollsBackOnlyOwnedSidecars()
    {
        var blobs=Path.Combine(root,"staged.blobs");Directory.CreateDirectory(blobs);File.WriteAllText(Path.Combine(blobs,"blob.bin"),"bytes");
        var output=Path.Combine(root,"catalog.jsonl");
        Assert.Throws<FileNotFoundException>(()=>BlobSidecarWriter.Publish(Path.Combine(root,"missing.jsonl"),blobs,output));
        Assert.False(Directory.Exists(output+".blobs"));Assert.False(File.Exists(output));
    }
}
