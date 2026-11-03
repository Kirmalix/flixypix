using System.Windows;
using System.Windows.Controls;
using FlixyPix.Core;
namespace FlixyPix.Desktop;
internal sealed class ParserChangesWindow : Window
{
 private readonly Func<IReadOnlyCollection<ParserChangeKey>, Task>? apply;
 private readonly WindowFrame frame;
 internal Task ApplyTask { get; private set; } = Task.CompletedTask;
 internal Button ApplyButton { get; }
 internal string FailureText => frame.Status.Text;
 private bool applying;
 private readonly List<(ParserChangeKey Key,CheckBox Box)> choices=[];
 internal IReadOnlyCollection<ParserChangeKey> Selected => choices.Where(pair=>pair.Box.IsChecked==true).Select(pair=>pair.Key).ToArray();
 internal ParserChangesWindow(Window owner,HistoricalParserPreview preview, Func<IReadOnlyCollection<ParserChangeKey>, Task>? apply = null)
 {
  this.apply = apply;
  Owner=owner; Title=T("Check parser changes"); Width=960; Height=680; MinWidth=700; MinHeight=460; ShowInTaskbar=false; WindowStartupLocation=WindowStartupLocation.CenterOwner;
  var panel=new StackPanel();
  foreach(var medium in preview.Changes)
  {
   panel.Children.Add(new TextBlock {Text=medium.Before.Path,TextWrapping=TextWrapping.Wrap,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,12,0,6)});
   foreach(var row in CatalogRepository.ParserChanges(preview with {Changes=[medium]}))
   {
    var label=row.Key.Kind switch {ParserChangeKind.Title=>T("Title"),ParserChangeKind.Tag=>T("Tags"),ParserChangeKind.CategoryTag=>row.Key.Category,_=>T("Metadata")+" / "+row.Key.Value};
    var text=new StackPanel(); text.Children.Add(new TextBlock {Text=label,FontWeight=FontWeights.SemiBold});
    text.Children.Add(new TextBlock {Text=row.Before+" \u2192 "+row.After,TextWrapping=TextWrapping.Wrap});
    var box=new CheckBox {Content=text,IsChecked=true,Margin=new Thickness(0,4,0,4)};
    choices.Add((row.Key,box));panel.Children.Add(box);
   }
  }
  frame=new WindowFrame(T("Check parser changes"),T("Apply changes"),panel);
  frame.Add(T("Cancel"),()=>DialogResult=false,cancel:true);
  ApplyButton = frame.Add(T("Apply changes"),()=>ApplyTask = ApplyAsync(),primary:true);
  Closing += (_, e) => { if (applying) e.Cancel = true; };
  Content=frame;
 }
 private async Task ApplyAsync()
 {
  if (applying) return;
  var selected = Selected; if (selected.Count == 0) return;
  applying = true; ApplyButton.IsEnabled = false;
  try
  {
   if (apply != null) await apply(selected);
   applying = false; DialogResult = true;
  }
  catch (Exception error) when (error is InvalidOperationException or ArgumentException or KeyNotFoundException or Microsoft.Data.Sqlite.SqliteException or OperationCanceledException)
  { frame.Status.Text = error.Message; }
  finally { applying = false; ApplyButton.IsEnabled = true; }
 }

}
