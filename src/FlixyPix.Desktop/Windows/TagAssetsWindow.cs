using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using FlixyPix.Core;
using Microsoft.Win32;

namespace FlixyPix.Desktop;
public partial class MainWindow
{
 private void ShowTagArtwork()=>new TagAssetsWindow(this,_repository,Path.Combine(_settings.DataDirectory,"tag-assets"),_tagGroup,CurrentTag).ShowDialog();
}
internal sealed class TagAssetsWindow:Window
{
 private readonly CatalogRepository repository;private readonly string cache;
 private readonly ListView list=MigrationViews.List();
 private readonly Image preview=new(){Stretch=Stretch.Uniform,Margin=new Thickness(12)};
 private readonly TextBlock details=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(12)};
 private readonly TextBox category=new(){MinWidth=140},tag=new(){MinWidth=140};
 private int page;private long selectionVersion;
 internal Task ReadyTask{get;private set;}=Task.CompletedTask;
 internal int AssetCount=>list.Items.Count;
 internal bool HasPreview=>preview.Source!=null;
 internal TagAssetsWindow(Window owner,CatalogRepository repository,string cache,string? selectedCategory=null,string? selectedTag=null)
 {
  Owner=owner;this.repository=repository;this.cache=cache;Title=T("Tag images · FlixyPix");Width=1080;Height=720;MinWidth=760;MinHeight=480;WindowStartupLocation=WindowStartupLocation.CenterOwner;ShowInTaskbar=false;
  category.Text=selectedCategory??"";tag.Text=selectedTag??"";
  var root=new DockPanel{LastChildFill=true};var top=new WrapPanel{Margin=new Thickness(0,0,0,8)};DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);
  category.Margin=tag.Margin=new Thickness(0,0,12,0);category.VerticalContentAlignment=tag.VerticalContentAlignment=VerticalAlignment.Center;
  top.Children.Add(new TextBlock{Text=T("Category"),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,8,0)});top.Children.Add(category);top.Children.Add(new TextBlock{Text=T("Tag"),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,8,0)});top.Children.Add(tag);
  Add(top,T("Assign image file …"),Import);Add(top,T("Check old tag image folder …"),ImportLegacyFolder);Add(top,T("Show all"),()=>{category.Clear();tag.Clear();page=0;Refresh();});
  var actions=new WrapPanel{Margin=new
   Thickness(0,8,0,0)};DockPanel.SetDock(actions,Dock.Bottom);root.Children.Add(actions);Add(actions,"←",()=>{page=Math.Max(0,page-1);Refresh();});Add(actions,"→",()=>{if(list.Items.Count==120){page++;Refresh();}});Add(actions,T("Export original bytes …"),Export);Add(actions,Tc("tag image", "Remove assignment"),Unassign);
  var grid=new Grid();grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});root.Children.Add(grid);
  list.MinWidth=420;list.Margin=new
   Thickness(0,0,12,0);list.Resources[SystemColors.InactiveSelectionHighlightBrushKey]=Theme.Brush("ItemSelected");list.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey]=Theme.Brush("Text");list.Resources[SystemColors.HighlightBrushKey]=Theme.Brush("ItemSelected");
  list.ItemContainerStyle=ArtworkRowStyle();
  MigrationViews.Columns(list,(T("Category"),"Tag.Category",100),("Tag","Tag.Value",100),("Name","Label",160),(T("Bytes"),"ByteLength",90));
  grid.Children.Add(list);var right=new DockPanel();Grid.SetColumn(right,1);grid.Children.Add(right);DockPanel.SetDock(details,Dock.Bottom);right.Children.Add(details);right.Children.Add(preview);
  var frame=new WindowFrame(T("Tag images"),T("Images for tags, for example people or genres. Source files are only read; the original bytes stay in the catalog."),root,"",scroll:false);
  frame.Add(T("Close"),Close,primary:true,cancel:true);Content=frame;
  list.SelectionChanged+=(_,_)=>ReadyTask=LoadPreviewAsync();Refresh();
 }
 internal static Style ArtworkRowStyle()
 {
  var style=new Style(typeof(ListViewItem));style.Setters.Add(new Setter(Control.ForegroundProperty,new DynamicResourceExtension("Text")));
  var template=new ControlTemplate(typeof(ListViewItem));var border=new FrameworkElementFactory(typeof(Border),"row");border.SetValue(Border.PaddingProperty,new Thickness(4));border.SetValue(Border.BackgroundProperty,Brushes.Transparent);
  var presenter=new FrameworkElementFactory(typeof(GridViewRowPresenter));presenter.SetBinding(GridViewRowPresenter.ContentProperty,new
   Binding());presenter.SetBinding(GridViewRowPresenter.ColumnsProperty,new Binding("View.Columns"){RelativeSource=new
   RelativeSource(RelativeSourceMode.FindAncestor,typeof(ListView),1)});border.AppendChild(presenter);template.VisualTree=border;
  var selected=new Trigger{Property=ListViewItem.IsSelectedProperty,Value=true};selected.Setters.Add(new Setter(Border.BackgroundProperty,new
   DynamicResourceExtension("ItemSelected"),"row"));template.Triggers.Add(selected);style.Setters.Add(new
   Setter(Control.TemplateProperty,template));return style;
 }
 private void Add(Panel parent,string label,Action action){var button=new Button{Content=label};button.Click+=(_,_)=>{try{action();}catch(Exception error){Message.Show(this,error.Message,Title,MessageBoxButton.OK,MessageBoxImage.Warning);}};parent.Children.Add(button);}
 private void Refresh(){list.ItemsSource=repository.GetTagAssetSummaries(offset:page*120);if(list.Items.Count>0)list.SelectedIndex=0;else{preview.Source=null;details.Text=T("No tag images yet. Source files are only read.");}}
 private async void Import()
 {
  if(string.IsNullOrWhiteSpace(category.Text)||string.IsNullOrWhiteSpace(tag.Text)){Message.Show(this,T("Enter the category and tag for the assignment."));return;}
  var picker=new OpenFileDialog { AddToRecent = Privacy.AddToRecent, Title=T("Choose tag image (source stays unchanged)"),Filter=T("Image files|*.jpg;*.jpeg;*.png;*.webp;*.gif;*.bmp;*.tif;*.tiff;*.avif;*.heic;*.heif;*.ico|All files|*.*")};if(picker.ShowDialog(this)!=true)return;
  try{await repository.ImportTagAssetFileAsync(picker.FileName,new(category.Text,tag.Text));page=0;Refresh();}catch(Exception error){Message.Show(this,error.Message,Title,MessageBoxButton.OK,MessageBoxImage.Warning);}
 }
 private async void ImportLegacyFolder()
 {
  var picker=new OpenFolderDialog { AddToRecent = Privacy.AddToRecent, Title=T("Choose the old tag image folder with category subfolders") };if(picker.ShowDialog(this)!=true)return;
  try
  {
   var plan=await Task.Run(()=>repository.PreviewLegacyTagImages(picker.FolderName));
   var mapped=plan.Files.Count(value=>value.IsUnambiguous);var ambiguous=plan.Files.Count(value=>value.Tags.Count>1);
   var summary=T("{0} unambiguously assigned images\n{1} ambiguous images\n{2} images without a known assignment\n\n", mapped, ambiguous, plan.Files.Count(value=>value.Tags.Count==0))+
    string.Join('\n',plan.Files.Select(value=>Path.GetFileName(value.Path)+" → "+(value.IsUnambiguous?value.Tags[0].Category+" / "+value.Tags[0].Value:T("no unambiguous match"))))+
    T("\n\nImport unambiguous matches only? The source files are kept.");
   if(Message.Show(this,summary,T("Review tag image import"),MessageBoxButton.OKCancel,MessageBoxImage.Information)!=MessageBoxResult.OK)return;
   await Task.Run(()=>repository.ImportLegacyTagImages(plan));page=0;Refresh();
  }
  catch(Exception error){Message.Show(this,error.Message,Title,MessageBoxButton.OK,MessageBoxImage.Warning);}
 }
 private async Task LoadPreviewAsync()
 {
  var version=++selectionVersion;preview.Source=null;if(list.SelectedItem is not TagAssetSummary summary)return;
  details.Text=T("{0}\n{1:N0} bytes\n{2}\n", summary.Label, summary.ByteLength, summary.Sha256)+(summary.Tag==null?T("Unassigned · original bytes are kept."):summary.Tag.Category+" · "+summary.Tag.Value);
  try{var asset=await Task.Run(()=>repository.GetTagAsset(summary.Id));if(asset==null||asset.IsOpaque)return;var path=await Task.Run(()=>repository.MaterializeTagAsset(asset,cache));var bitmap=await DesktopImaging.LoadBitmapAsync(path,900);if(version==selectionVersion)preview.Source=bitmap;}
  catch(Exception error)when(error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or UnauthorizedAccessException){if(version==selectionVersion)details.Text+=T("\nPreview not available: ")+error.Message;}
 }
 private void Export(){if(list.SelectedItem is not TagAssetSummary summary)return;var picker=new
  SaveFileDialog{Title=T("Export unchanged original bytes"),FileName=summary.Id+".bin",Filter=T("Original bytes|*.*")};if(picker.ShowDialog(this)==true)repository.ExportTagAsset(summary.Id,picker.FileName);}
 private void Unassign(){if(list.SelectedItem is not TagAssetSummary summary)return;var
  asset=repository.GetTagAsset(summary.Id);if(asset==null)return;repository.UpsertTagAsset(asset
  with{Tag=null,PreviousTags=asset.Tag==null?asset.PreviousTags:asset.PreviousTags.Append(asset.Tag).ToList()});Refresh();}
}
