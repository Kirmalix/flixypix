using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace FlixyPix.Core;

public sealed record VideoStack(Guid Id, string? Name, Guid MasterId, IReadOnlyList<Guid> MediaIds, long Revision = 0);

public sealed partial class CatalogRepository
{
    private void InitializeVideoStacks(SqliteTransaction transaction)
    {
        using var command = Command("""
            CREATE TABLE IF NOT EXISTS video_stacks(id TEXT PRIMARY KEY,name TEXT,master_id TEXT NOT NULL,revision INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS video_stack_members(stack_id TEXT NOT NULL REFERENCES video_stacks(id) ON DELETE CASCADE,
                media_id TEXT NOT NULL UNIQUE REFERENCES media(id) ON DELETE CASCADE,PRIMARY KEY(stack_id,media_id));
            CREATE TRIGGER IF NOT EXISTS video_stack_removed AFTER DELETE ON video_stack_members BEGIN
                UPDATE video_stacks SET revision=revision+1,master_id=CASE WHEN master_id=OLD.media_id THEN
                  coalesce((SELECT s.media_id FROM video_stack_members s LEFT JOIN media_query q ON q.id=s.media_id WHERE s.stack_id=OLD.stack_id ORDER BY q.title COLLATE EFP_ORDINAL,q.path COLLATE EFP_ORDINAL,q.id LIMIT 1),master_id)
                  ELSE master_id END WHERE id=OLD.stack_id;
                DELETE FROM video_stacks WHERE id=OLD.stack_id AND (SELECT count(*) FROM video_stack_members WHERE stack_id=OLD.stack_id)<2;
            END;
            """, transaction);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<VideoStack> GetVideoStacks() => GetVideoStacksCore(null);
    public IReadOnlyList<VideoStack> GetVideoStacksFor(IReadOnlyList<Guid> mediaIds)=>GetVideoStacksCore(null,mediaIds);
    private IReadOnlyList<VideoStack> GetVideoStacksCore(SqliteTransaction? transaction,IReadOnlyList<Guid>? mediaIds=null)
    {
        lock(gate)
        {
            ThrowIfDisposed();
            var groups = new List<(Guid Id,string? Name,Guid Master,long Revision)>();
            using(var c=Command("SELECT id,name,master_id,revision FROM video_stacks "+(mediaIds is null ? "" : "WHERE id IN (SELECT stack_id FROM video_stack_members WHERE media_id IN (SELECT value FROM json_each($ids))) ")+"ORDER BY id",transaction,mediaIds is null ? [] : [("$ids",(object)JsonSerializer.Serialize(mediaIds))]))
            using(var r=c.ExecuteReader()) while(r.Read()) groups.Add((Guid.Parse(r.GetString(0)),r.IsDBNull(1)?null:r.GetString(1),Guid.Parse(r.GetString(2)),r.GetInt64(3)));
            return groups.Select(g => new VideoStack(g.Id,g.Name,g.Master,StackMembers(g.Id,transaction),g.Revision)).ToArray();
        }
    }
    private Guid[] StackMembers(Guid id,SqliteTransaction? transaction)
    {
        using var c=Command("SELECT media_id FROM video_stack_members WHERE stack_id=$id ORDER BY media_id",transaction,("$id",id.ToString()));
        using var r=c.ExecuteReader();var ids=new List<Guid>();while(r.Read())ids.Add(Guid.Parse(r.GetString(0)));return ids.ToArray();
    }
    public VideoStack SaveVideoStack(VideoStack stack)
    {
        ArgumentNullException.ThrowIfNull(stack);
        lock(gate)
        {
            ThrowIfDisposed();using var tx=connection.BeginTransaction();SaveVideoStackCore(stack,tx);tx.Commit();
            return stack with {Name=string.IsNullOrWhiteSpace(stack.Name)?null:stack.Name.Trim(),Revision=stack.Revision+1};
        }
    }
    private void SaveVideoStackCore(VideoStack stack,SqliteTransaction tx)
    {
        if(stack.Id==Guid.Empty || stack.MediaIds is null || stack.MediaIds.Count<2 || stack.MediaIds.Count>100000 ||
           stack.MediaIds.Distinct().Count()!=stack.MediaIds.Count || !stack.MediaIds.Contains(stack.MasterId) || stack.Name?.Length>256)
            throw new ArgumentException(T("Invalid video stack."));
        foreach(var id in stack.MediaIds)
        {
            if(ReadMediaDocument(id,tx) is not {Kind:MediaKind.Video})throw new ArgumentException(T("Stacks require existing videos."));
            using var check=Command("SELECT stack_id FROM video_stack_members WHERE media_id=$media AND stack_id<>$stack",tx,("$media",id.ToString()),("$stack",stack.Id.ToString()));
            if(check.ExecuteScalar()!=null)throw new InvalidOperationException(T("A video already belongs to another stack. Remove it from that stack first."));
        }
        using(var read=Command("SELECT revision FROM video_stacks WHERE id=$id",tx,("$id",stack.Id.ToString())))
        {
            var current=read.ExecuteScalar();
            if(current is not null && Convert.ToInt64(current)!=stack.Revision || current is null && stack.Revision!=0)
                throw new InvalidOperationException(T("The stack changed. Refresh before editing it again."));
        }
        // Replacing the parent also clears membership without firing the member-removal normalization trigger.
        using(var clear=Command("DELETE FROM video_stacks WHERE id=$id",tx,("$id",stack.Id.ToString())))clear.ExecuteNonQuery();
        using(var add=Command("INSERT INTO video_stacks VALUES($id,$name,$master,$revision)",tx,("$id",stack.Id.ToString()),("$name",string.IsNullOrWhiteSpace(stack.Name)?DBNull.Value:(object)stack.Name.Trim()),("$master",stack.MasterId.ToString()),("$revision",stack.Revision+1)))add.ExecuteNonQuery();
        foreach(var id in stack.MediaIds){using var add=Command("INSERT INTO video_stack_members VALUES($stack,$media)",tx,("$stack",stack.Id.ToString()),("$media",id.ToString()));add.ExecuteNonQuery();}
    }
    public VideoStack MoveVideoStackMembers(VideoStack target)
    {
        lock(gate)
        {
            ThrowIfDisposed();using var tx=connection.BeginTransaction();
            foreach(var id in target.MediaIds)
            {
                using var remove=Command("DELETE FROM video_stack_members WHERE media_id=$id AND stack_id<>$stack",tx,("$id",id.ToString()),("$stack",target.Id.ToString()));remove.ExecuteNonQuery();
            }
            SaveVideoStackCore(target,tx);tx.Commit();return target with{Revision=target.Revision+1};
        }
    }
    private void CheckVideoStackMerge(IEnumerable<Guid> ids,SqliteTransaction tx)
    {
        var values=JsonSerializer.Serialize(ids);
        using var c=Command("SELECT count(DISTINCT stack_id) FROM video_stack_members WHERE media_id IN (SELECT value FROM json_each($ids))",tx,("$ids",values));
        if(Convert.ToInt32(c.ExecuteScalar())>1)throw new InvalidOperationException(T("Choose one video stack for these videos before combining their files."));
    }
    private void RedirectVideoStackMembers(IReadOnlySet<Guid> removed,Guid keep,SqliteTransaction tx)
    {
        CheckVideoStackMerge(removed.Append(keep),tx);
        using var find=Command("SELECT stack_id FROM video_stack_members WHERE media_id IN (SELECT value FROM json_each($ids)) LIMIT 1",tx,("$ids",JsonSerializer.Serialize(removed)));
        if(find.ExecuteScalar() is not string stack)return;
        using(var add=Command("INSERT OR IGNORE INTO video_stack_members VALUES($stack,$keep)",tx,("$stack",stack),("$keep",keep.ToString())))add.ExecuteNonQuery();
        using var master=Command("UPDATE video_stacks SET master_id=$keep,revision=revision+1 WHERE id=$stack AND master_id IN (SELECT value FROM json_each($ids))",tx,("$keep",keep.ToString()),("$stack",stack),("$ids",JsonSerializer.Serialize(removed)));master.ExecuteNonQuery();
    }

    public void RemoveVideoStackMembers(Guid id,long revision,IReadOnlyList<Guid> members)
    {
        lock(gate)
        {
            ThrowIfDisposed();using var tx=connection.BeginTransaction();
            using(var check=Command("SELECT revision FROM video_stacks WHERE id=$id",tx,("$id",id.ToString())))
                if(check.ExecuteScalar() is not {} current || Convert.ToInt64(current)!=revision)throw new InvalidOperationException(T("The stack changed. Refresh before editing it again."));
            using var remove=Command("DELETE FROM video_stack_members WHERE stack_id=$id AND media_id IN (SELECT value FROM json_each($members))",tx,("$id",id.ToString()),("$members",JsonSerializer.Serialize(members)));remove.ExecuteNonQuery();tx.Commit();
        }
    }

    public void DeleteVideoStack(Guid id,long revision)
    {
        lock(gate){ThrowIfDisposed();using var c=Command("DELETE FROM video_stacks WHERE id=$id AND revision=$revision",null,("$id",id.ToString()),("$revision",revision));if(c.ExecuteNonQuery()!=1)throw new InvalidOperationException(T("The stack changed. Refresh before editing it again."));}
    }

    public (bool Show,Guid[] Expanded) GetVideoStackPresentation()
    {
        lock(gate){using var c=Command("SELECT document FROM ui_state WHERE id='video-stacks'",null);return c.ExecuteScalar() is string json ? JsonSerializer.Deserialize<StackPresentation>(json) is {} state ? (state.Show,state.Expanded) : (true,[]) : (true,[]);}
    }
    private sealed record StackPresentation(bool Show,Guid[] Expanded);
    public void SaveVideoStackPresentation(bool show,Guid[] expanded)
    {
        lock(gate){ThrowIfDisposed();using var c=Command("INSERT INTO ui_state VALUES('video-stacks',$value) ON CONFLICT(id) DO UPDATE SET document=excluded.document",null,("$value",JsonSerializer.Serialize(new StackPresentation(show,expanded))));c.ExecuteNonQuery();}
    }

    public IReadOnlyDictionary<Guid,(Guid Representative,int Count)> GetVideoStackMatches(MediaQuery query,IReadOnlyList<Guid>? stackIds=null)
    {
        lock(gate)
        {
            ThrowIfDisposed();using var tx=connection.BeginTransaction();var compiled=BuildQuery(query,tx);var order=MediaQueries.OrderSql(query);
            var filter=stackIds is null ? "" : (string.IsNullOrWhiteSpace(compiled.Where)?" WHERE ":" AND ")+"s.stack_id IN (SELECT value FROM json_each($stacks))";
            var parameters=stackIds is null ? compiled.Parameters : compiled.Parameters.Concat(new[]{("$stacks",(object)JsonSerializer.Serialize(stackIds))}).ToArray();
            using var c=Command($"WITH hits AS (SELECT s.stack_id,q.id,count(*) OVER(PARTITION BY s.stack_id) n,row_number() OVER(PARTITION BY s.stack_id ORDER BY CASE WHEN q.id=v.master_id THEN 0 ELSE 1 END,{order}) r FROM media_query q JOIN video_stack_members s ON s.media_id=q.id JOIN video_stacks v ON v.id=s.stack_id {compiled.Where}{filter}) SELECT stack_id,id,n FROM hits WHERE r=1",tx,parameters);
            var result=new Dictionary<Guid,(Guid,int)>();using(var r=c.ExecuteReader())while(r.Read())result.Add(Guid.Parse(r.GetString(0)),(Guid.Parse(r.GetString(1)),r.GetInt32(2)));tx.Commit();return result;
        }
    }
    private bool HasVideoStacks()
    {
        lock(gate){ThrowIfDisposed();using var c=Command("SELECT 1 FROM video_stacks LIMIT 1",null);return c.ExecuteScalar()!=null;}
    }

    private const string VideoStackProblems="SELECT count(*) FROM video_stacks v WHERE NOT EXISTS(SELECT 1 FROM video_stack_members s WHERE s.stack_id=v.id AND s.media_id=v.master_id) OR (SELECT count(*) FROM video_stack_members s WHERE s.stack_id=v.id)<2 OR EXISTS(SELECT 1 FROM video_stack_members s LEFT JOIN media m ON m.id=s.media_id WHERE s.stack_id=v.id AND (m.id IS NULL OR json_extract(m.document,'$.kind')<>'Video'))";
    private int RepairVideoStacks(SqliteTransaction tx)
    {
        using var count=Command(VideoStackProblems,tx);var problems=Convert.ToInt32(count.ExecuteScalar());
        using var fix=Command("DELETE FROM video_stack_members WHERE NOT EXISTS(SELECT 1 FROM video_stacks v WHERE v.id=stack_id) OR NOT EXISTS(SELECT 1 FROM media m WHERE m.id=media_id AND json_extract(m.document,'$.kind')='Video'); UPDATE video_stacks SET master_id=(SELECT media_id FROM video_stack_members s WHERE s.stack_id=video_stacks.id ORDER BY media_id LIMIT 1),revision=revision+1 WHERE NOT EXISTS(SELECT 1 FROM video_stack_members s WHERE s.stack_id=video_stacks.id AND s.media_id=master_id) AND EXISTS(SELECT 1 FROM video_stack_members s WHERE s.stack_id=video_stacks.id); DELETE FROM video_stacks WHERE (SELECT count(*) FROM video_stack_members s WHERE s.stack_id=video_stacks.id)<2;",tx);fix.ExecuteNonQuery();return problems;
    }

    private string StackSql(MediaQuery query, SqliteTransaction tx, out (string Name,object Value)[] parameters)
    {
        var compiled=BuildQuery(query,tx);parameters=compiled.Parameters.Concat(new[]{("$expanded",(object)JsonSerializer.Serialize(query.ExpandedVideoStacks))}).ToArray();
        var order=MediaQueries.OrderSql(query);
        return $"""
            WITH candidates AS (SELECT {MediaQueries.OrderColumns(query)},s.stack_id,CASE WHEN s.stack_id IS NULL THEN 'media:'||q.id ELSE 'stack:'||s.stack_id END bucket,v.master_id FROM media_query q
              LEFT JOIN video_stack_members s ON s.media_id=q.id LEFT JOIN video_stacks v ON v.id=s.stack_id {compiled.Where}),
            ranked AS (SELECT q.*,row_number() OVER(PARTITION BY bucket ORDER BY CASE WHEN q.id=q.master_id THEN 0 ELSE 1 END,{order}) member_rank FROM candidates q),
            anchors AS (SELECT * FROM ranked WHERE member_rank=1),
            visible AS (SELECT q.id,row_number() OVER(ORDER BY {order.Replace("q.","a.")},CASE WHEN q.member_rank=1 THEN 0 ELSE 1 END,{order})-1 position
              FROM ranked q JOIN anchors a ON a.bucket=q.bucket WHERE q.member_rank=1 OR q.stack_id IN (SELECT value FROM json_each($expanded)))
            """;
    }
    public IReadOnlySet<Guid> VisibleVideoStackMedia(MediaQuery query,IReadOnlyList<Guid> ids,CancellationToken token=default)
    {
        if(!query.GroupVideoStacks || !HasVideoStacks())return MatchingMedia(query,ids).ToHashSet();
        return database.Run<IReadOnlySet<Guid>>(token,()=>{
            EnsureStackOrder(query,token);using var c=Command("SELECT id FROM temp.video_stack_order WHERE id IN (SELECT value FROM json_each($ids))",null,("$ids",JsonSerializer.Serialize(ids)));
            using var r=c.ExecuteReader();var result=new HashSet<Guid>();while(r.Read())result.Add(Guid.Parse(r.GetString(0)));return result;
        });
    }

    private string? stackCacheKey;
    private (long Changes,long DataVersion)? stackCacheStamp;
    private long stackCacheMatching;
    private void EnsureStackOrder(MediaQuery query,CancellationToken token)
    {
        var cacheKey=JsonSerializer.Serialize(query with{Offset=0,Limit=120},Json);
        if(stackCacheKey==cacheKey && stackCacheStamp==database.ChangeStamp())return;
        token.ThrowIfCancellationRequested();
        var beforeStamp=database.ChangeStamp();
        using var tx=connection.BeginTransaction();
        using(var create=Command("CREATE TEMP TABLE IF NOT EXISTS video_stack_order(position INTEGER PRIMARY KEY,id TEXT NOT NULL UNIQUE); DELETE FROM temp.video_stack_order;",tx))create.ExecuteNonQuery();
        var sql=StackSql(query,tx,out var parameters);
        using(var fill=Command(sql+" INSERT INTO temp.video_stack_order SELECT position,id FROM visible",tx,parameters))fill.ExecuteNonQuery();
        var compiled=BuildQuery(query,tx);long matching;
        using(var count=Command(compiled.Count,tx,compiled.Parameters))matching=Convert.ToInt64(count.ExecuteScalar());
        token.ThrowIfCancellationRequested();tx.Commit();
        stackCacheMatching=matching;stackCacheStamp=database.ChangeStamp();
        stackCacheKey=beforeStamp.DataVersion==stackCacheStamp.Value.DataVersion ? cacheKey : null;
    }
    private MediaPage QueryStackPage(MediaQuery query,CancellationToken token,bool count=true)
    {
        return database.Run(token,()=>{
            if(query.Offset<0 || query.Limit is <1 or >1000)throw new ArgumentOutOfRangeException(nameof(query));
            EnsureStackOrder(query,token);
            using var tx=connection.BeginTransaction();
            long total=0;if(count){using var c=Command("SELECT count(*) FROM temp.video_stack_order",tx);total=Convert.ToInt64(c.ExecuteScalar());}
            using var page=Command("SELECT m.document FROM (SELECT position,id FROM temp.video_stack_order ORDER BY position LIMIT $limit OFFSET $offset) v JOIN media m ON m.id=v.id ORDER BY v.position",tx,("$limit",query.Limit),("$offset",query.Offset));
            var items=new List<MediaItem>();using(var r=page.ExecuteReader())while(r.Read()){token.ThrowIfCancellationRequested();items.Add(JsonSerializer.Deserialize<MediaItem>(r.GetString(0),Json)!);}
            tx.Commit();return new MediaPage(items,total){MatchingMediaCount=stackCacheMatching};
        });
    }
    private int? StackPosition(MediaQuery query,Guid id,CancellationToken token)=>database.Run<int?>(token,()=>{
        EnsureStackOrder(query,token);
        using var c=Command("SELECT position FROM temp.video_stack_order WHERE id=$id",null,("$id",id.ToString()));var result=c.ExecuteScalar();return result is null?null:Convert.ToInt32(result);
    });
}
