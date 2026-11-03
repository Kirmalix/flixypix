using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FlixyPix.Core;
using Microsoft.Data.Sqlite;
namespace FlixyPix.Desktop;
public partial class MainWindow
{
 private async Task RunParserApplySmokeAsync(string directory)
 {
  foreach(var encrypted in new[]{false,true})
  {
   var root=Path.Combine(directory,encrypted?"parser-modal-encrypted":"parser-modal-plain");Directory.CreateDirectory(root);
   var path=Path.Combine(root,"catalog.db");using(var empty=new CatalogRepository(path)) { }
   if(encrypted)CatalogEncryption.EnableEncryption(path,"synthetic-modal-parser-secret");
   using var key=encrypted?CatalogEncryption.Unlock(path,"synthetic-modal-parser-secret"):null;
   using var db=new CatalogRepository(path,key);
   var definition=new HistoricalParserDefinition {Id=Guid.NewGuid(),SourceDraftId=Guid.NewGuid(),Name="Synthetic modal parser",SourceId="owned-test",LegacyId="1",SourcePayloadSha256=new string('a',64),
    Formatting=new(){Confirmed=true,SourceDescription="Synthetic fixture",CultureName="en-US"},Options=new(){Mode=HistoricalParserMode.Standard,ParseFilename=true,ApplyTagSepToFilename=true,TagSeparator=0}};
   // Seed only an owned synthetic fixture. Production definitions still require explicit reviewed activation.
   using(var sql=new SqliteConnection(CatalogEncryption.ConnectionString(path,key).ToString()))
   {sql.Open();using var command=sql.CreateCommand();command.CommandText="CREATE TABLE IF NOT EXISTS historical_parser_definitions(id TEXT PRIMARY KEY,document TEXT NOT NULL); INSERT INTO historical_parser_definitions(id,document) VALUES($id,$json)";
    command.Parameters.AddWithValue("$id",definition.Id.ToString());command.Parameters.AddWithValue("$json",JsonSerializer.Serialize(definition,CatalogRepository.Json));command.ExecuteNonQuery();}
   var medium=db.Upsert(new MediaItem {Path=Path.Combine(root,"Alice Bob.mp4"),Title="Manual",ReviewState=ReviewState.Reviewed});
   var draft=new MetadataDraft(medium.Id,"Unsaved","","Keep draft","",MetadataValues.From(medium));db.SaveMetadataDrafts([draft]);
   var preview=db.PreviewHistoricalParser(definition.Id,[medium.Id]);
   var window=new ParserChangesWindow(this,preview,selection=>Task.Run(()=>db.ApplyHistoricalParserSelected(preview,selection)));
   Exception? failure=null;
   window.ContentRendered+=async(_,_)=>
   {
    try
    {
     var boxes=Descendants<CheckBox>((FrameworkElement)window.Content).ToArray();
     if(boxes.Length!=2)throw new InvalidOperationException("Modal parser expected two tag proposals.");
     boxes[1].IsChecked=false; Capture((FrameworkElement)window.Content,Path.Combine(root,"selected-preview.png"));
     window.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await window.ApplyTask;
    }
    catch(Exception error){failure=error;window.Close();}
   };
   if(window.ShowDialog()!=true || failure!=null)throw new InvalidOperationException("Modal parser apply failed.",failure);
   var saved=db.GetMedia(medium.Id)!;
   if(!saved.Tags.SequenceEqual(new[]{"Alice"})||saved.Title!="Manual"||saved.ReviewState!=ReviewState.Reviewed||db.GetMetadataDrafts().Single()!=draft)
    throw new InvalidOperationException("Modal parser apply changed unselected data or draft.");
   var cancel=new ParserChangesWindow(this,db.PreviewHistoricalParser(definition.Id,[medium.Id]),_=>throw new InvalidOperationException("Cancelled dialog must not apply"));
   cancel.ContentRendered+=(_,_)=>cancel.Close();cancel.ShowDialog();
   if(!db.GetMedia(medium.Id)!.Tags.SequenceEqual(new[]{"Alice"}))throw new InvalidOperationException("Cancel wrote parser changes.");
   File.WriteAllText(Path.Combine(root,"result.txt"),"PASS: modal selected apply and cancellation, saved metadata/draft preserved.");
  }
 }
}
