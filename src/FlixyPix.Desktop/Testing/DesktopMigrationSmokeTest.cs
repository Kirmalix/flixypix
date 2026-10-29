using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using FlixyPix.Core;
using Microsoft.Win32;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 private async Task RunMigrationSmokeTestAsync(string directory)
 {
  object Schema(string table,params string[] names)=>new { type="schema",table,columns=names.Select(name=>new { name }) };
  object Row(string table,object values)=>new { type="row",table,values };
  var input=Path.Combine(directory,"synthetic-legacy.jsonl");
  object[] records=[new { type="header",format="flixypix-legacy",version=1,sourceSha256=new string('f',64) },
   Schema("movies","ID","fullpath","filename","title"),Row("movies",new { ID=1,fullpath=@"U:\offline",filename="never-opened.mp4",title="Bewahrtes Offline-Medium" }),
   Schema("keyword","ID","type","keyword","keytags","remoteID"),Row("keyword",new { ID=2,type=4,keyword="Historische Taggruppe",keytags="\"Vorschau\"",remoteID=0 }),
   Schema("search_settings","ID","searchtype","xml"),Row("search_settings",new { ID=3,searchtype=500,xml="<SearchSettings_Video><UnknownOption>retained</UnknownOption></SearchSettings_Video>" }),
   Schema("cliptovideo_queue","ID","sourcefile","outputfile","cmd","totalseconds"),Row("cliptovideo_queue",new { ID=4,sourcefile=@"U:\offline\never-opened.mp4",outputfile=@"U:\never-created.mp4",cmd="ARCHIVAL TEXT - DO NOT EXECUTE",totalseconds=4 }),
   new { type="footer",rows=4,tables=4,tableRows=new Dictionary<string,int> { ["movies"]=1,["keyword"]=1,["search_settings"]=1,["cliptovideo_queue"]=1 } }];
  File.WriteAllLines(input,records.Select(r=>JsonSerializer.Serialize(r)));
  var before=_repository.QueryPage(new() { Limit=1 }).TotalCount;var target=Path.Combine(directory,"separate-migrated-library");
  var migrated=await MigrateLegacyToNewLibraryAsync(input,target,"desktop-migration-smoke");
  if(migrated.Result.Migration?.Rows!=4||!File.Exists(migrated.ReportPath)||_repository.QueryPage(new() { Limit=1 }).TotalCount!=before)
   throw new InvalidOperationException("Separate migration changed the current catalog or omitted its report.");
  try { await MigrateLegacyToNewLibraryAsync(input,target,"desktop-migration-smoke");throw new InvalidOperationException("Existing migration directory was accepted."); }
  catch(IOException) { }
  using(var cancelled=new CancellationTokenSource())
  {
   cancelled.Cancel();var cancelledTarget=Path.Combine(directory,"cancelled-migration");
   try{await MigrateLegacyToNewLibraryAsync(input,cancelledTarget,"cancelled",cancelled.Token);throw new InvalidOperationException("Migration ignored cancellation.");}catch(OperationCanceledException){}
   if(Directory.Exists(cancelledTarget))throw new InvalidOperationException("Cancelled migration published a destination.");
  }
  using var catalog=new CatalogRepository(Path.Combine(target,"catalog.db"));var media=catalog.QueryPage(new() { Limit=1 }).Items.Single();
  if(media.Path!=@"U:\offline\never-opened.mp4"||media.Tags.Count!=0||catalog.GetSavedSearches().Count!=0||catalog.GetLegacyJobDrafts().Any(j=>j.IsExecutable))
   throw new InvalidOperationException("Historical migration activated data or altered offline paths.");
  catalog.Upsert(media with { Title="Meine Bearbeitung",Rating=9 });
  var snapshot=Path.Combine(directory,"synthetic-legacy-snapshot.jsonl");
  var changed=records.Select(r=>System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(r))!).ToArray();changed[0]["sourceSha256"]=new string('e',64);changed[2]["values"]!["title"]="Titel aus neuem Snapshot";
  File.WriteAllLines(snapshot,changed.Select(r=>r.ToJsonString()));var updated=catalog.ImportLegacyJsonl(snapshot,new LegacyImportOptions { SourceId="desktop-migration-smoke" });
  if(updated.Migration==null||catalog.GetMedia(media.Id)!.Title!="Meine Bearbeitung")throw new InvalidOperationException("Snapshot import overwrote local changes.");
  var conflict=catalog.PreviewMigrationMediaConflict(updated.Migration.Batch,media.Id);var conflictWindow=new MigrationConflictWindow(this,conflict);conflictWindow.SelectField("title");conflictWindow.Show();
  await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);Capture((FrameworkElement)conflictWindow.Content,Path.Combine(directory,"migration-conflict.png"));conflictWindow.Close();
  var accepted=catalog.ResolveMigrationMediaConflict(updated.Migration.Batch,media.Id,conflictWindow.SelectedFields,conflict.CurrentSha256);
  if(accepted.Title!="Titel aus neuem Snapshot"||accepted.Rating!=9)throw new InvalidOperationException("Selected conflict fields changed unrelated metadata.");
  var stale=catalog.PreviewMigrationMediaConflict(updated.Migration.Batch,media.Id);catalog.Upsert(accepted with { Description="Edited after preview" });var staleRejected=false;
  try{catalog.ResolveMigrationMediaConflict(updated.Migration.Batch,media.Id,["rating"],stale.CurrentSha256);}catch(InvalidOperationException){staleRejected=true;}
  if(!staleRejected)throw new InvalidOperationException("Stale conflict preview was accepted.");
  var reportWindow=new MigrationReportWindow(this,catalog);reportWindow.Show();reportWindow.ShowRecords();
  await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
  if(reportWindow.VisibleRowCount!=4)throw new InvalidOperationException("Migration report did not render its records.");
  Capture((FrameworkElement)reportWindow.Content,Path.Combine(directory,"migration-report.png"));reportWindow.Close();
  var history=new HistoricalLibraryWindow(this,catalog,[media.Id],(_,_)=>Task.CompletedTask);history.Show();
  await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
  if(history.EntryCount!=1)throw new InvalidOperationException("Historical preset list did not render.");
  Capture((FrameworkElement)history.Content,Path.Combine(directory,"historical-library.png"));
  catalog.UpsertLegacyParserDraft(LegacyObjectReader.ReadParserSettings([0x45,0x46,0x50],"desktop-migration-smoke","opaque-parser"));history.ShowParserDrafts();
  await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);if(history.EntryCount!=1)throw new InvalidOperationException("Historical parser inventory did not render.");Capture((FrameworkElement)history.Content,Path.Combine(directory,"historical-parser.png"));history.Close();
  var functions=new HistoricalFunctionsWindow(this,catalog,[media.Id],_=>throw new InvalidOperationException("Smoke must not enqueue historical jobs."),()=>Task.CompletedTask);functions.Show();
  await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
  if(functions.SearchDraftCount!=1||functions.ParserDraftCount!=1||functions.JobDraftCount!=1)throw new InvalidOperationException("Historical translation choices did not render.");
  if(catalog.PreviewLegacySearchTranslation(catalog.GetLegacySearchDrafts().Single().Id).CanApply)throw new InvalidOperationException("Unknown search option became active.");
  Capture((FrameworkElement)functions.Content,Path.Combine(directory,"historical-functions.png"));functions.Close();
  var mapping=catalog.PlanPathMapping([new(@"U:\offline",@"U:\neu-zugeordnet")]);if(mapping.Changes.Count!=1||mapping.Conflicts.Count!=0)throw new InvalidOperationException("Offline prefix mapping failed.");
  var mappingWindow=new PathMappingPreviewWindow(this,mapping);mappingWindow.Show();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);Capture((FrameworkElement)mappingWindow.Content,Path.Combine(directory,"migration-paths.png"));mappingWindow.Close();
  catalog.ApplyPathMappingPlan(mapping);if(catalog.GetMedia(media.Id)!.Path!=@"U:\neu-zugeordnet\never-opened.mp4")throw new InvalidOperationException("Prefix mapping failed to update the catalog.");
  var availability=new MediaAvailabilityWindow(this,catalog);availability.Show();await availability.ReadyTask;if(availability.OfflineCount!=1)throw new InvalidOperationException("Configured offline source was reported missing.");
  await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);Capture((FrameworkElement)availability.Content,Path.Combine(directory,"migration-offline.png"));availability.Close();
  await RefreshAsync(includePreviews:false);
  if(MediaList.Items.OfType<MediaCard>().Any(c=>c.Thumbnail!=null))throw new InvalidOperationException("Metadata-only refresh loaded media thumbnails.");
 }
}
