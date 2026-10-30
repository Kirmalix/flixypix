using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
namespace FlixyPix.Desktop;
internal sealed class HelpWindow : Window
{
 internal static IEnumerable<(string Title,string Body)> Guide()=>[
 (T("Libraries"),T("Add one or more media folders to a library. Import keeps your original files in place. Back up the catalog and media files separately.")),
 (T("Folders"),T("Use the folder tree to narrow the view. The subfolder switch affects browsing only. Wheel scrolls vertically; Shift+wheel scrolls horizontally.")),
 (T("Filter"),T("Combine search, tags, rating and thumbnail filters. Click a filter chip to remove it. Save the view as a smart collection to reuse it.")),
 (T("Tags"),T("Select media before assigning tags. Ctrl+1–9 uses your fixed quick tags. Enter filters by the focused tag; Ctrl+Enter excludes it.")),
 (T("Collections"),T("The circle on a media card adds it to the quick collection. B uses your chosen target collection. Neither action moves files.")),
 (T("Thumbnails"),T("Fit whole image preserves the complete thumbnail. Fill and crop fills the frame. High-resolution home artwork is generated only for the visible featured video.")),
 (T("Video player"),T("Use Tab to reach playback controls and arrow keys to adjust focused sliders. Escape closes menus first, then leaves full screen or closes the player. Player shortcuts can be configured in Settings."))];
 internal HelpWindow(Window owner,string title,IEnumerable<(string Title,string Body)> sections)
 {
  Owner=owner;Title=title;Width=820;Height=650;ShowInTaskbar=false;WindowStartupLocation=WindowStartupLocation.CenterOwner;
  var search=new TextBox {Margin=new Thickness(0,4,0,10),ToolTip=T("Search")};System.Windows.Automation.AutomationProperties.SetName(search,T("Search"));
  var body=new StackPanel();var scroll=new ScrollViewer {Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};var entries=sections.ToArray();
  void Render() {body.Children.Clear();foreach(var entry in entries.Where(x=>(x.Title+" "+x.Body).Contains(search.Text,StringComparison.CurrentCultureIgnoreCase))) {body.Children.Add(new TextBlock {Text=entry.Title,FontSize=18,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,12,0,6)});body.Children.Add(new TextBox {Text=entry.Body,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,BorderThickness=new Thickness(0),Background=System.Windows.Media.Brushes.Transparent,AcceptsReturn=true});}}
  search.TextChanged+=(_,_)=>Render();Render();var panel=new DockPanel();var searchLabel=new TextBlock {Text=T("Search"),Foreground=SettingsUi.Muted};DockPanel.SetDock(searchLabel,Dock.Top);panel.Children.Add(searchLabel);DockPanel.SetDock(search,Dock.Top);panel.Children.Add(search);panel.Children.Add(scroll);
  var frame=new WindowFrame(title,null,panel,scroll:false);frame.Add(T("Copy"),()=>Clipboard.SetText(string.Join("\n\n",entries.Select(x=>x.Title+"\n"+x.Body))));frame.Add(T("Close"),Close,cancel:true);Content=frame;
  Loaded+=(_,_)=>search.Focus();PreviewKeyDown+=(_,e)=>{if(e.Key==Key.F&&Keyboard.Modifiers==ModifierKeys.Control){search.Focus();search.SelectAll();e.Handled=true;}};
 }
}
