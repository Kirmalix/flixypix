using System.IO;
using System.Windows;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 private SearchExpression? _advancedExpression;
 private void AdvancedSearch(){var editor=CreateAdvancedSearch();if(editor.ShowDialog()==true){_advancedExpression=editor.Result;_anchor=0;Refresh();}}
 /// <summary>The search builder with live hit counts and suggestions from the library's tags and categories.</summary>
 internal AdvancedSearchWindow CreateAdvancedSearch()
 {
  AdvancedSearchWindow.Tags=_tagEntries.Where(t=>t.Count>0).OrderByDescending(t=>t.Count).Select(t=>t.Tag).ToArray();
  AdvancedSearchWindow.Categories=_repository.GetFacets().Categories;
  return new AdvancedSearchWindow(this,_advancedExpression,(expression,token)=>Task.Run(()=>_repository.QueryPage(new MediaQuery{Expression=expression,Limit=1},token).TotalCount,token));
 }
 private void ClearAdvancedSearch(){_advancedExpression=null;_anchor=0;Refresh();}
 private (string Label, Action Action)[] OrganizationEntries()=>[(T("Tag images"),ShowTagArtwork),(T("Advanced search …"),AdvancedSearch),(T("Clear advanced search"),ClearAdvancedSearch),(T("Extend tag vocabulary …"),AddVocabularyTag),(T("Toggle tag favorite …"),ToggleVocabularyFavorite),(T("Open tag favorites …"),OpenFavoriteTag),(T("Toggle folder favorite"),ToggleCurrentFolderFavorite),(T("Open folder favorites …"),OpenFavoriteFolder),(T("Rename / merge tag everywhere …"),RenameGlobalTag),(T("Remove tag everywhere …"),DeleteGlobalTag),(T("Change tag category …"),MoveGlobalTagCategory),(T("Create file name parser …"),NewFilenameParser),(T("Apply file name parser …"),ApplyParser)];
 private void AddVocabularyTag(){var input=Dialogs.Fields(this,T("Tag vocabulary"),(T("Tag"),CurrentTag??""),(T("Category"),_tagGroup??T("People")));if(input==null)return;_repository.UpsertVocabularyTag(new(input[0],input[1]));Refresh();}
 private void ToggleVocabularyFavorite(){var
  input=Dialogs.Fields(this,T("Tag favorite"),(T("Tag"),CurrentTag??""),(T("Category"),_tagGroup??T("People")));if(input==null)return;var
  old=_repository.GetPreferences().TagVocabulary.FirstOrDefault(t=>t.Tag.Equals(input[0],StringComparison.OrdinalIgnoreCase)&&t.Category.Equals(input[1],StringComparison.OrdinalIgnoreCase));_repository.UpsertVocabularyTag(new(input[0],input[1],old?.IsFavorite!=true,old?.LegacyId));Refresh();}
 private void OpenFavoriteTag(){var
  tags=_repository.GetPreferences().TagVocabulary.Where(t=>t.IsFavorite).ToArray();if(tags.Length==0){Message.Show(this,T("No tag favorites yet."));return;}Menu(tags.Select(t=>(t.Category+" · "+t.Tag,(Action)(()=>{_tag=t.Tag;_tagGroup=t.Category;_view="all";_playlist=null;_anchor=0;Refresh();}))).ToArray());}
 private void ToggleCurrentFolderFavorite(){var folder=_folder??(Selected is {} item?Path.GetDirectoryName(item.Path):null);if(folder==null)return;_repository.ToggleFolderFavorite(new(folder));Status.Text=T("Folder favorite toggled");}
 private void OpenFavoriteFolder(){var
  folders=_repository.GetPreferences().FolderFavorites;if(folders.Count==0){Message.Show(this,T("No folder favorites yet."));return;}Menu(folders.Select(f=>(f.Path,(Action)(()=>{_folder=f.Path;_category=f.Category;_view=f.Kind?.ToString()??"all";_tag=null;_tagGroup=null;_playlist=null;_anchor=0;Refresh();}))).ToArray());}
 private void RenameGlobalTag()=>RenameGlobalTag(CurrentTag);
 private async void RenameGlobalTag(string? initial){var
  input=Dialogs.Fields(this,T("Rename / merge tag everywhere"),(T("Previous tag"),initial??""),(T("New tag"),""));if(input==null)return;await
  RunOperation(T("Merge tags"),async token=>{var result=await
  Task.Run(()=>_repository.ChangeTag(input[0],input[1],token),token);Status.Text=Tn(result.MediaChanged, "1 item changed",
  "{0} items changed");});}
 private void DeleteGlobalTag()=>DeleteGlobalTag(CurrentTag);
 private async void DeleteGlobalTag(string? initial){var
  input=Dialogs.Fields(this,T("Remove tag everywhere"),(T("Tag"),initial??""));if(input==null||Message.Show(this,T("Remove the tag from all items? Affected tag rules are deactivated; historical raw data is kept."),T("Remove tag"),MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)return;await
  RunOperation(T("Remove tag"),async token=>await Task.Run(()=>_repository.ChangeTag(input[0],null,token),token));}
 private async void MoveGlobalTagCategory(){var
  input=Dialogs.Fields(this,T("Change tag category"),(T("Tag"),CurrentTag??""),(T("Previous category"),_tagGroup??""),(T("New category"),""));if(input==null)return;await
  RunOperation(T("Change tag category"),async token=>await Task.Run(()=>_repository.MoveTagCategory(input[0],input[1],input[2],token),token));}
 private void NewFilenameParser()
 {
  var input=Dialogs.Fields(this,T("File name parser"),(T("Name"),T("New parser")),(T("Separator"),"_-.,;"),(T("Included parent folders (0–32)"),"0"),(T("Replacements: old=new; more=replacement"),""),(T("Excluded whole tags (separated by semicolons)"),""),(T("Tag category (optional)"),""));if(input==null)return;
  try{if(!int.TryParse(input[2],out var depth))throw new ArgumentException(T("Enter the folder depth as a whole number."));var
   replacements=input[3].Split(';',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Select(s=>{var pair=s.Split('=',2);return
   pair.Length==2?new TextReplacement(pair[0],pair[1]):throw new
   ArgumentException(T("A replacement needs old=new."));}).ToList();_repository.UpsertFilenameParser(new(){Name=input[0],Separators=input[1],ParentFolderDepth=depth,Replacements=replacements,Exclusions=input[4].Split(';',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).ToList(),Category=string.IsNullOrWhiteSpace(input[5])?null:input[5]});Status.Text=T("File name parser saved");}
  catch(Exception error){Message.Show(this,error.Message,T("File name parser"),MessageBoxButton.OK,MessageBoxImage.Warning);}
 }
 private void ApplyParser(){var
  parsers=_repository.GetPreferences().FilenameParsers;if(parsers.Count==0){Message.Show(this,T("No file name parsers yet."));return;}Menu(parsers.Select(p=>(p.Name,(Action)(async()=>await
  RunOperation(T("Apply file name parser"),async token=>await Task.Run(()=>_repository.ApplyFilenameParser(p.Id,null,token),token))))).ToArray());}
}
