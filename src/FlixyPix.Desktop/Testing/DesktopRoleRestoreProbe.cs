using System.IO;
using System.Windows;
using System.Windows.Threading;
using FlixyPix.Core;
namespace FlixyPix.Desktop;
public partial class MainWindow
{
 internal static async Task RunRoleRestoreProbeAsync(string root, CatalogKey? key, ColorScheme scheme = ColorScheme.Dark)
 {
  Theme.Apply(scheme);
  Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
  var library = Path.Combine(root, "catalog");
  using (var db = new CatalogRepository(Path.Combine(library, "catalog.db"), key))
  {
   for (var i = 0; i < 128; i++) db.UpsertVocabularyTag(new("Person " + i.ToString("D3"), "Cast"));
   db.SetTagRole("Cast", TagRole.Performer);
   var imagePath = Path.Combine(root, "synthetic-portrait.png");
   for (var i = 0; i < 128; i++)
   {
    using var image = new ImageMagick.MagickImage(new ImageMagick.MagickColor("#" + i.ToString("X2") + "8899"), 64, 64);
    image.Write(imagePath, ImageMagick.MagickFormat.Png);
    db.ImportTagAssetFile(imagePath, new TagKey("Cast", "Person " + i.ToString("D3")));
   }
   var preferences = db.GetPreferences(); db.SavePreferences(preferences with { General = preferences.General with { RestoreLastView = true, RestoreScope = RestoreScope.All }, Home = preferences.Home with { StartOnHome = false } });
   db.SaveViewState(new ViewState { View = "performers", SearchText = "Person", Directory = new(new("Cast", "Person 100"), 40, 140) });
  }
  for (var round = 0; round < 2; round++)
  {
   var window = new MainWindow(new DesktopSettings { DataDirectory = library, AutoGeneratePreviews = false }, persistPreferences: false, unlockedKey: key);
   var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); window.Closed += (_, _) => closed.TrySetResult();
   try
   {
    window.Show(); await window.Startup; await window._lastRefresh;
    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    var state = window.CaptureViewState();
    if (state.View != "performers" || state.SearchText != "Person" || state.Directory?.Selected != new TagKey("Cast", "Person 100") || state.Directory.ListOffset < 35 || state.Directory.ProfileOffset < 130)
     throw new InvalidOperationException("Fresh role window did not restore profile/search/scroll: " + System.Text.Json.JsonSerializer.Serialize(state.Directory));
    Capture(window, Path.Combine(root, "role-" + scheme + "-reopen-" + round + ".png"));
    if (round == 0)
    {
     foreach (var width in new[] { 1000d, 1600d })
     {
      window.Width = width; await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
      window.UpdateLayout();
      if (window._roleBrowser!.CompactLayout != (width == 1000)) throw new InvalidOperationException("Role view did not adapt in fresh theme process.");
      Capture(window, Path.Combine(root, "role-" + scheme + "-" + width + ".png"));
     }
     window._roleBrowser!.RestoreDirectoryState(state.Directory);
     await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
    if (round == 1)
    {
     var portraits = new List<WeakReference>(); var memory = new List<object>();
     for (var i = 0; i < 100; i++)
     {
      window._roleBrowser!.RestoreDirectoryState(new(new("Cast", "Person " + i.ToString("D3"))));
      await window._roleBrowser.ProfileReady;
      if (!window._roleBrowser.HasPortrait) throw new InvalidOperationException("Profile picture did not load during memory probe.");
      if (window._roleBrowser.PortraitReference is { } portrait) portraits.Add(portrait);
      if (i % 20 == 0) memory.Add(new { Profile = i, ManagedBytes = GC.GetTotalMemory(true) });
      if (window._roleBrowser.RetainedRowPictures > 128) throw new InvalidOperationException("Row picture retention exceeded its budget.");
     }
     GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
     var retained = portraits.Count(p => p.IsAlive);
     if (retained > 3) throw new InvalidOperationException("Old profile portraits retained: " + retained);
     window.ApplyViewState(new ViewState { View = "all" }); await window._lastRefresh;
     if (window._roleBrowser!.RetainedRowPictures != 0 || window._roleBrowser.HasPortrait) throw new InvalidOperationException("Hidden role browser retained pictures.");
     File.WriteAllText(Path.Combine(root, "role-artwork-memory.json"), System.Text.Json.JsonSerializer.Serialize(new { Profiles = 100, RetainedOldPortraits = retained, Samples = memory }));
    }
    window.SaveViewNow();
   }
   finally { window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
  }
  File.WriteAllText(Path.Combine(root, "role-result.txt"), "PASS: two actual main windows restored profile, search, list and profile scroll.");
  await RunImageSetRestoreProbeAsync(root, library, key);
  await RunPublicationGroupRestoreProbeAsync(root,library,key);
  await RunTagHierarchyHostProbeAsync(root,key,scheme);
 }
 private static async Task RunImageSetRestoreProbeAsync(string root,string library,CatalogKey? key)
 {
  Guid setId;Guid imageId;
  using(var db=new CatalogRepository(Path.Combine(library,"catalog.db"),key))
  {
   imageId=db.Upsert(new(){Path=Path.Combine(root,"synthetic-portrait.png"),Kind=MediaKind.Image,Title="Restored set image"}).Id;
   setId=db.UpsertImageSet(new(){Name="Restored image set",MediaIds=[imageId],CoverId=imageId}).Id;
   db.SaveViewState(new(){View="image-sets",ImageSetId=setId});
  }
  for(var round=0;round<3;round++)
  {
   var window=new MainWindow(new DesktopSettings{DataDirectory=library,AutoGeneratePreviews=false},persistPreferences:false,unlockedKey:key);
   var closed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);window.Closed+=(_,_)=>closed.TrySetResult();
   try
   {
    window.Show();await window.Startup;await window._lastRefresh;await window._setCoverTask;
    await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
    if(!window.IsImageSetView||window._setPage?.IsVisible!=true)throw new InvalidOperationException("Fresh window did not restore image-set route.");
    if(round<2)
    {
     if(window.SelectedImageSet?.Id!=setId||window._setCover?.Source==null||window._setMembers?.Items.Count!=1)
      throw new InvalidOperationException("Fresh window lost selected set, members or cover.");
     if(round==0)await window.RunImageSetCrudSmokeAsync(root);
    }
    else if(window.SelectedImageSet?.Id==setId||window.CaptureViewState().ImageSetId==setId)
     throw new InvalidOperationException("Deleted set selection survived fresh-window sanitization.");
    window.SaveViewNow();
   }
   finally{window.Close();await closed.Task.WaitAsync(TimeSpan.FromSeconds(20));}
   if(round==1)using(var db=new CatalogRepository(Path.Combine(library,"catalog.db"),key))db.DeleteImageSet(setId);
  }
  File.WriteAllText(Path.Combine(root,"image-set-restore-result.txt"),"PASS: selected set and cover restored in two fresh windows; deleted set sanitized in third.");
 }
 private static async Task RunPublicationGroupRestoreProbeAsync(string root,string library,CatalogKey? key)
 {
  Guid groupId;
  using(var db=new CatalogRepository(Path.Combine(library,"catalog.db"),key))
  {
   var image=db.Upsert(new(){Path=Path.Combine(root,"synthetic-portrait.png"),Kind=MediaKind.Image,Title="Group restore image"});
   var video=db.Upsert(new(){Path=Path.Combine(root,"group-restore.mp4"),Kind=MediaKind.Video,Title="Group restore video"});
   var set=db.UpsertImageSet(new(){Name="Group restore set",MediaIds=[image.Id]});
   groupId=db.UpsertPublicationGroup(new(){Name="Restored publication",Members=[new(PublicationGroupMemberKind.ImageSet,set.Id),new(PublicationGroupMemberKind.Video,video.Id)]}).Id;
   db.SaveViewState(new(){View="publication-groups",PublicationGroupId=groupId});
  }
  for(var round=0;round<3;round++)
  {
   var window=new MainWindow(new DesktopSettings{DataDirectory=library,AutoGeneratePreviews=false},persistPreferences:false,unlockedKey:key);
   var closed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);window.Closed+=(_,_)=>closed.TrySetResult();
   try
   {
    window.Show();await window.Startup;await window._lastRefresh;await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
    if(!window.IsPublicationGroupView||window._publicationPage?.IsVisible!=true)throw new InvalidOperationException("Fresh window did not restore publication route.");
    if(round<2)
    {
     if(window.SelectedPublicationGroup?.Id!=groupId||window._publicationMembers?.Items.Count!=2)throw new InvalidOperationException("Fresh window lost group or typed members.");
     if(round==0){await window.RunPublicationGroupsSmokeAsync(root);await window.RunPublicationCrudSmokeAsync(root);}
    }
    else if(window.SelectedPublicationGroup?.Id==groupId||window.CaptureViewState().PublicationGroupId==groupId)throw new InvalidOperationException("Deleted group selection survived sanitization.");
    window.SaveViewNow();
   }
   finally{window.Close();await closed.Task.WaitAsync(TimeSpan.FromSeconds(20));}
   if(round==1)using(var db=new CatalogRepository(Path.Combine(library,"catalog.db"),key))db.DeletePublicationGroup(groupId);
  }
  File.WriteAllText(Path.Combine(root,"publication-restore-result.txt"),"PASS: selected group and typed order restored in two fresh windows; deleted group sanitized in third.");
 }
}
