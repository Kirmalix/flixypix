using System.Windows;
using System.Windows.Controls;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

internal sealed class ImageSetProposalsWindow : Window
{
 internal sealed record Choice(ImageSetProposal Proposal,CheckBox Include,TextBox Name);
 internal IReadOnlyList<Choice> Choices=>choices;
 private readonly List<Choice> choices=[];
 private readonly Func<IReadOnlyList<ImageSetProposalChoice>,Task> apply;
 private readonly WindowFrame frame;
 internal Button ApplyButton {get;}
 internal Task ApplyTask {get;private set;}=Task.CompletedTask;
 internal string FailureText=>frame.Status.Text;
 private bool applying;
 internal ImageSetProposalsWindow(Window owner,IReadOnlyList<ImageSetProposal> proposals,
  Func<Guid,MediaItem?> media,Func<IReadOnlyList<ImageSetProposalChoice>,Task> apply,Func<Guid,ImageSet?>? existingSet=null)
 {
  this.apply=apply;Owner=owner;Title=T("Suggest image sets");Width=900;Height=680;MinWidth=600;MinHeight=420;
  ShowInTaskbar=false;WindowStartupLocation=WindowStartupLocation.CenterOwner;
  var panel=new StackPanel();
  foreach(var proposal in proposals)
  {
   var include=new CheckBox{Content=new TextBlock{Text=proposal.SourcePath,TextWrapping=TextWrapping.Wrap},ToolTip=proposal.SourcePath,IsChecked=proposal.ExistingSetIds.Count==0,Margin=new Thickness(0,12,0,4)};
   var name=new TextBox{Text=proposal.Name,MaxLength=256,Margin=new Thickness(24,0,0,4)};
   panel.Children.Add(include);panel.Children.Add(name);choices.Add(new(proposal,include,name));
   string Names(IEnumerable<Guid> ids)=>string.Join(", ",ids.Select(id=>existingSet?.Invoke(id)?.Name??id.ToString()));
   if(proposal.ExistingSetIds.Count>0)panel.Children.Add(new TextBlock{Text=T("An image set with these members already exists.")+"\n"+Names(proposal.ExistingSetIds),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(24,0,0,4)});
   if(proposal.OverlappingSetIds.Count>0)panel.Children.Add(new TextBlock{Text=T("Images also belong to: {0}",Names(proposal.OverlappingSetIds)),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(24,0,0,4)});
   var members=new StackPanel();
   foreach(var id in proposal.MediaIds)
   {
    var item=media(id);members.Children.Add(new TextBlock{Text=item==null?id.ToString():item.Title+"\n"+(proposal.IsArchive?item.Extra.GetValueOrDefault("entryName")??item.Path:item.Path),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,4)});
   }
   panel.Children.Add(new Expander{Header=T("Images")+": "+proposal.MediaIds.Count,Foreground=SettingsUi.Muted,Content=members,Margin=new Thickness(24,0,0,8)});
  }
  frame=new WindowFrame(T("Suggest image sets"),T("Only checked proposals are created. Existing sets and files are kept."),panel);
  frame.Add(T("Cancel"),()=>DialogResult=false,cancel:true);
  ApplyButton=frame.Add(T("Apply"),()=>ApplyTask=ApplyAsync(),primary:true);
  ApplyButton.IsEnabled=proposals.Count>0;Closing+=(_,e)=>{if(applying)e.Cancel=true;};Content=frame;
 }
 private async Task ApplyAsync()
 {
  if(applying)return;
  var selected=choices.Where(c=>c.Include.IsChecked==true).Select(c=>new ImageSetProposalChoice(c.Proposal,c.Name.Text)).ToArray();
  if(selected.Length==0)return;
  applying=true;ApplyButton.IsEnabled=false;
  try{await apply(selected);applying=false;DialogResult=true;}
  catch(Exception error)when(error is ArgumentException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or OperationCanceledException)
  {frame.Status.Text=error.Message;}
  finally{applying=false;ApplyButton.IsEnabled=true;}
 }
}
