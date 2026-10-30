using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FlixyPix.Core;
namespace FlixyPix.Desktop;
internal sealed record UiAction(string Label,string Gesture,Action Run)
{
 internal MenuSpec Menu=>MenuSpec.Command(Label,Run,shortcut:Gesture);
 internal bool Matches(KeyEventArgs e)=>PlayerHotkeys.TryParse(Gesture,out var modifiers,out var key)&&key==(e.Key==Key.System?e.SystemKey:e.Key)&&modifiers==Keyboard.Modifiers;
}
public partial class MainWindow
{
 private IReadOnlyList<UiAction> AppActions()=>[
  new(T("Help"),"F1",()=>ShowHelp(false)),new(T("Keyboard shortcuts"),"Ctrl+F1",()=>ShowHelp(true)),
  new(T("Menu"),"F10",()=>ApplicationMenuClicked(ApplicationMenuButton,new())),
  new(T("Search items"),"Ctrl+F",()=>{SearchBox.Focus();SearchBox.SelectAll();}),
  new(T("Import media"),"Ctrl+O",()=>ImportFiles(this,new())),new(T("Save"),"Ctrl+S",()=>SaveDetails(this,new())),
  new(T("Show or hide the sidebar"),"Ctrl+B",ToggleSidebar),new(T("Show or hide details"),"Ctrl+D",ToggleDetails),
  new(T("Libraries"),"Ctrl+Shift+B",ManageLibraries),new(T("Home"),"Ctrl+Home",GoHome),
  new(T("Tags"),"Ctrl+T",FocusTagInput),new(T("Page"),"Ctrl+G",FocusPageInput),
  new(T("Invert selection"),"Ctrl+I",()=>_=InvertSelectionAsync())];
 private ContextMenu? _applicationMenu;
 private void ApplicationMenuClicked(object sender,RoutedEventArgs e)
 {
  var actions=AppActions();
  _applicationMenu=Menus.Open(ApplicationMenuButton,[
   MenuSpec.Command(T("Libraries …"),ManageLibraries),MenuSpec.Command(T("Media folders …"),()=>ShowLibraryFolders()),
   MenuSpec.Command(T("Settings"),()=>Settings(this,new())),MenuSpec.Command(T("Media jobs"),OpenQueue),MenuSpec.Separator,
   actions[0].Menu,actions[1].Menu,MenuSpec.Command(T("Release notes"),()=>OfflineDocument("CHANGELOG.md",T("Release notes"))),
   MenuSpec.Command(T("About FlixyPix"),ShowAbout),MenuSpec.Command(T("Licenses"),()=>OfflineDocument("THIRD-PARTY-NOTICES.md",T("Licenses"))),
   MenuSpec.Submenu(T("Diagnostics"),[
    MenuSpec.Command(T("Open log folder"),ErrorLog.OpenFolder),MenuSpec.Command(T("Technical information"),()=>new HelpWindow(this,T("Technical information"),[(T("Technical information"),TechnicalInformation())]).ShowDialog())]),
   MenuSpec.Separator,MenuSpec.Command(T("Project website"),()=>OpenProject(false)),MenuSpec.Command(T("Report a problem"),()=>OpenProject(true)),
   MenuSpec.Separator,MenuSpec.Command(T("Exit"),Close)],MenuFailed);
  _applicationMenu.MoveFocus(new(FocusNavigationDirection.First));
 }
 internal static string AppVersion=>typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion??"";
 internal static string TechnicalInformation()=>string.Join(Environment.NewLine,"FlixyPix "+AppVersion,RuntimeInformation.OSDescription,RuntimeInformation.OSArchitecture.ToString(),RuntimeInformation.ProcessArchitecture.ToString(),RuntimeInformation.FrameworkDescription);
 private void OfflineDocument(string file,string title)
 {
  var path=Path.Combine(AppContext.BaseDirectory,"help",file);var text=File.Exists(path)?File.ReadAllText(path):T("Not available");
  if(file=="THIRD-PARTY-NOTICES.md") {var license=Path.Combine(AppContext.BaseDirectory,"help","LICENSE");if(File.Exists(license))text+="\n\n"+File.ReadAllText(license);}
  new HelpWindow(this,title,[(title,text)]).ShowDialog();
 }
 private static void OpenProject(bool issue)=>Process.Start(new ProcessStartInfo(ProductIdentity.ProjectUrl+(issue?"/issues/new":"")){UseShellExecute=true});
 private void ShowHelp(bool shortcuts,Window? owner=null)
 {
  var sections=HelpWindow.Guide().ToList();if(shortcuts)sections.Clear();
  sections.Add((T("Keyboard shortcuts"),string.Join("\n",AppActions().Select(a=>a.Gesture+"   "+a.Label))));
  sections.Add((T("Navigation"),"F6 / Shift+F6 · Tab / Shift+Tab · ↑ ↓ ← → · Page↑ / Page↓ · Home / End · Shift+F10"));
  sections.Add((T("Selection"),"Shift+↑/↓/←/→ · Ctrl+↑/↓/←/→ · Ctrl+Space · Ctrl+A"));
  sections.Add((T("Media"),string.Join("\n", "Enter   "+T("Open"),"Space   "+T("Large view"),"F2   "+T("Rename"),"Delete   "+T("Remove from catalog"),"0–5   "+T("Rating"),"F   "+T("Favorite"),"B   "+T("Target collection for B"))));
  sections.Add((T("Tags"),string.Join("\n",QuickTags.Select((tag,i)=>"Ctrl+"+(i+1)+"   "+tag))));
  var playback=_repository.GetPreferences().Playback;
  sections.Add((T("Shortcuts in the player")+(playback.KeyboardShortcuts?"":" · "+T("Inactive")),string.Join("\n",playback.AllShortcuts().Select(p=>p.Key+"   "+(PlayerActions.All.FirstOrDefault(a=>a.Action==p.Owner).Label??p.Owner)))));
  sections.Add((T("Images"),"← / → · + / − · F · R · Space · S · I · T · Page↑ / Page↓ · Esc"));
  new HelpWindow(owner??this,shortcuts?T("Keyboard shortcuts"):T("Help"),sections).ShowDialog();
 }
 private void ThumbnailFitChanged(object sender,SelectionChangedEventArgs e)
 {
  if(_initializingBrowsing||_repository==null||ThumbnailFit.SelectedIndex<0)return;
  var current=_repository.GetPreferences();if(current.General.CropThumbnails==(ThumbnailFit.SelectedIndex==1))return;
  var anchor=_visibleFirst;_repository.SavePreferences(current with {General=current.General with {CropThumbnails=ThumbnailFit.SelectedIndex==1}});_anchor=Math.Max(0,anchor);Refresh();
 }
}
