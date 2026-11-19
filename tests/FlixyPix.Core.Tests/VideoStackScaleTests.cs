using System.Diagnostics;
using System.Text.Json;
using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace FlixyPix.Core.Tests;

public sealed class VideoStackScaleTests(ITestOutputHelper output)
{
    [Fact,Trait("Category","Slow")]
    public void DenseStacksRemainPagedAtOneHundredThousandVideos()
    {
        var root=Path.Combine(Path.GetTempPath(),"flixypix-stack-scale-"+Guid.NewGuid().ToString("N"));
        try
        {
            using var catalog=new CatalogRepository(Path.Combine(root,"catalog.db"));
            var stackIds=new List<Guid>();
            using(var seed=new SqliteConnection($"Data Source={catalog.DatabasePath};Pooling=False"))
            {
                seed.Open();CatalogRepository.AddOrdinalCollation(seed);using var tx=seed.BeginTransaction();
                using var insert=seed.CreateCommand();insert.Transaction=tx;insert.CommandText="INSERT INTO media VALUES($id,$path,$document)";
                var id=insert.Parameters.Add("$id",SqliteType.Text);var path=insert.Parameters.Add("$path",SqliteType.Text);var document=insert.Parameters.Add("$document",SqliteType.Text);insert.Prepare();
                Guid group=default;
                for(var i=0;i<100000;i++)
                {
                    var media=new MediaItem{Path=Path.Combine(root,$"video-{i:D6}.mp4"),Title=$"Video {i:D6}",Kind=MediaKind.Video};
                    id.Value=media.Id.ToString();path.Value=media.Path.ToUpperInvariant();document.Value=JsonSerializer.Serialize(media,CatalogRepository.Json);insert.ExecuteNonQuery();
                    if(i>=50000)continue;
                    if(i%50==0)
                    {
                        group=Guid.NewGuid();stackIds.Add(group);using var add=seed.CreateCommand();add.Transaction=tx;
                        add.CommandText="INSERT INTO video_stacks VALUES($id,NULL,$master,1)";add.Parameters.AddWithValue("$id",group.ToString());add.Parameters.AddWithValue("$master",media.Id.ToString());add.ExecuteNonQuery();
                    }
                    using var member=seed.CreateCommand();member.Transaction=tx;member.CommandText="INSERT INTO video_stack_members VALUES($stack,$media)";member.Parameters.AddWithValue("$stack",group.ToString());member.Parameters.AddWithValue("$media",media.Id.ToString());member.ExecuteNonQuery();
                }
                tx.Commit();
            }
            var query=new MediaQuery{GroupVideoStacks=true,Limit=120};var watch=Stopwatch.StartNew();var first=catalog.QueryPage(query);var firstMs=watch.Elapsed.TotalMilliseconds;
            Assert.Equal(51000,first.TotalCount);Assert.Equal(100000,first.MatchingMediaCount);Assert.Equal(120,first.Items.Count);
            watch.Restart();var deep=catalog.QueryPage(query with{Offset=50000});var warmMs=watch.Elapsed.TotalMilliseconds;Assert.Equal(120,deep.Items.Count);
            watch.Restart();var expanded=catalog.QueryPage(query with{ExpandedVideoStacks=[stackIds[0]],Offset=0});var expandedMs=watch.Elapsed.TotalMilliseconds;
            Assert.Equal(51049,expanded.TotalCount);Assert.Equal(120,expanded.Items.Count);
            output.WriteLine($"100000 videos, 1000 stacks: first={firstMs:F0}ms, deep={warmMs:F0}ms, expanded={expandedMs:F0}ms");
            Assert.True(firstMs<2000,$"First grouped page took {firstMs:F0}ms");Assert.True(warmMs<1000 && expandedMs<1000,$"Warm grouped pages took {warmMs:F0}/{expandedMs:F0}ms");
        }
        finally{SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
