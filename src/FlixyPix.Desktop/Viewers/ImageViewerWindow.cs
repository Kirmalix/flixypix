using System.Windows;
using System.Windows.Input;

namespace FlixyPix.Desktop;

/// <summary>The image viewer in a window of its own: for image sets, slideshows of a collection and images that are not
/// among the tiles on show. Images among the tiles open in the loupe of the main window instead.</summary>
internal sealed class ImageViewerWindow : Window
{
 internal ImageViewer View { get; }

 internal ImageViewerWindow(Window owner, ImageViewer view)
 {
  View=view; Owner=owner; Content=view;
  FontSize=14; Width=1150; Height=800; MinWidth=600; MinHeight=400; WindowStartupLocation=WindowStartupLocation.CenterOwner;
  view.TitleChanged+=(_,_)=>Title=view.Title;
  view.Closed+=(_,_)=>{ if(!_closing) Close(); };
  Closing+=(_,_)=>_closing=true;
  Closed+=(_,_)=>view.Close();
  PreviewKeyDown+=(_,e)=>{ if(view.HandleKey(e)) e.Handled=true; };
 }
 private bool _closing;
}
