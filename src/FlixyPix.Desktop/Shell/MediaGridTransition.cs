using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace FlixyPix.Desktop;

/// <summary>Identity-based transitions, installed in the same UI turn as the layout change, before it can be painted.</summary>
internal sealed class MediaGridTransition(ListBox list,Func<bool>? animations=null)
{
    private sealed record Place(FrameworkElement Visual,Rect Bounds,ImageSource? Snapshot=null);
    private readonly Dictionary<(Guid Id,int Occurrence),Place> before=[];
    private readonly List<FrameworkElement> moving=[];
    private Ghosts? ghosts;
    private int generation;
    private Action? settled;
    private bool Enabled=>animations?.Invoke()??(Motion.Enabled && !Motion.Steady);
    internal bool HasMotion=>moving.Count>0 || ghosts!=null;
    internal bool IsActive=>before.Count>0 || HasMotion;
    internal void WhenSettled(Action action){if(IsActive)settled+=action;else action();}
    internal double MovingDistance=>moving.Sum(v=>v.RenderTransform is TranslateTransform t ? Math.Abs(t.X)+Math.Abs(t.Y) : 0);
    internal void Capture()
    {
        if(!Enabled){Clear();return;}
        // Bounds include an interrupted animation's current position. Freeze outgoing pictures before containers change.
        var current=Places(true);Clear();foreach(var pair in current)before[pair.Key]=pair.Value;
    }
    private Dictionary<(Guid Id,int Occurrence),Place> Places(bool snapshot=false)
    {
        var result=new Dictionary<(Guid Id,int Occurrence),Place>();var occurrences=new Dictionary<Guid,int>();
        foreach(var card in list.Items.OfType<MediaCard>())
        {
            var occurrence=occurrences.GetValueOrDefault(card.Item.Id);occurrences[card.Item.Id]=occurrence+1;
            if(list.ItemContainerGenerator.ContainerFromItem(card) is ListBoxItem container && container.IsVisible &&
               VisualTreeHelper.GetChildrenCount(container)>0 && VisualTreeHelper.GetChild(container,0) is FrameworkElement visual)
            {
                var point=visual.TranslatePoint(new Point(),list);var bounds=new Rect(point,visual.RenderSize);
                if(!bounds.IntersectsWith(new Rect(list.RenderSize)) || bounds.Width<=0 || bounds.Height<=0)continue;
                ImageSource? picture=null;
                if(snapshot)
                {
                    var dpi=VisualTreeHelper.GetDpi(list);
                    var bitmap=new RenderTargetBitmap(Math.Max(1,(int)Math.Ceiling(bounds.Width*dpi.DpiScaleX)),Math.Max(1,(int)Math.Ceiling(bounds.Height*dpi.DpiScaleY)),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);
                    var drawing=new DrawingVisual();
                    using(var dc=drawing.RenderOpen())dc.DrawRectangle(new VisualBrush(visual),null,new Rect(visual.RenderSize));
                    bitmap.Render(drawing);bitmap.Freeze();picture=bitmap;
                }
                result[(card.Item.Id,occurrence)]=new(visual,bounds,picture);
            }
        }
        return result;
    }
    internal void Apply()
    {
        if(!Enabled || !list.IsVisible){Clear();return;}
        var turn=++generation;
        // Scheduling this after Loaded let the final grid flash up before the first animated frame.
        list.UpdateLayout();if(turn!=generation)return;var after=Places();
        var ease=new CubicEase{EasingMode=EasingMode.EaseOut};
        foreach(var (id,place) in after)
        {
            var old=before.GetValueOrDefault(id);
            var delta=old is null?new Vector(0,12):old.Bounds.TopLeft-place.Bounds.TopLeft;
            if(delta.Length<.1 && old!=null)continue;
            // A row wrap must not sweep a tile diagonally across every neighbouring column.
            var wraps=old!=null && Math.Abs(delta.X)>place.Bounds.Width*1.5;
            if(wraps)delta=new Vector(0,Math.Sign(delta.Y)*12);
            if(delta.Length>Math.Max(list.ActualHeight,list.ActualWidth))delta=new Vector(0,12);
            var translate=new TranslateTransform(delta.X,delta.Y);place.Visual.RenderTransform=translate;moving.Add(place.Visual);
            translate.BeginAnimation(TranslateTransform.XProperty,new DoubleAnimation(delta.X,0,TimeSpan.FromMilliseconds(260)){EasingFunction=ease,FillBehavior=FillBehavior.HoldEnd});
            translate.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(delta.Y,0,TimeSpan.FromMilliseconds(260)){EasingFunction=ease,FillBehavior=FillBehavior.HoldEnd});
            if(old is null || wraps)place.Visual.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(180)){FillBehavior=FillBehavior.Stop});
        }
        var gone=before.Where(p=>!after.ContainsKey(p.Key)).Select(p=>p.Value).ToArray();
        if(gone.Length>0 && AdornerLayer.GetAdornerLayer(list) is {} layer)
        {
            ghosts=new Ghosts(list,gone){IsHitTestVisible=false};layer.Add(ghosts);
            ghosts.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(1,0,TimeSpan.FromMilliseconds(150)){FillBehavior=FillBehavior.HoldEnd});
        }
        before.Clear();
        var timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(280)};
        timer.Tick+=(_,_)=>{timer.Stop();if(turn==generation)Clear();};timer.Start();
    }
    internal void Clear()
    {
        ++generation;
        if(ghosts!=null){AdornerLayer.GetAdornerLayer(list)?.Remove(ghosts);ghosts=null;}
        foreach(var visual in moving){visual.RenderTransform=Transform.Identity;visual.BeginAnimation(UIElement.OpacityProperty,null);}
        moving.Clear();before.Clear();
        var done=settled;settled=null;
        if(done!=null)list.Dispatcher.BeginInvoke(()=>WhenSettled(done),System.Windows.Threading.DispatcherPriority.Loaded);
    }
    private sealed class Ghosts(UIElement owner,IReadOnlyList<Place> places):Adorner(owner)
    {
        protected override void OnRender(DrawingContext dc)
        {
            dc.PushClip(new RectangleGeometry(new Rect(AdornedElement.RenderSize)));
            foreach(var place in places)if(place.Snapshot is {} picture)dc.DrawImage(picture,place.Bounds);
            dc.Pop();
        }
    }
}
