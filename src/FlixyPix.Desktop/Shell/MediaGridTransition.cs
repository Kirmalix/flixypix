using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FlixyPix.Desktop;

/// <summary>Identity-based layout transitions for the realized viewport only. No data operation waits for a clock.</summary>
internal sealed class MediaGridTransition(ListBox list,Func<bool>? animations=null)
{
    private sealed record Place(FrameworkElement Visual,Rect Bounds);
    private readonly Dictionary<Guid,Place> before=[];
    private readonly List<FrameworkElement> moving=[];
    private Ghosts? ghosts;
    private int generation;
    internal bool HasMotion=>moving.Count>0 || ghosts!=null;
    internal double MovingDistance=>moving.Sum(v=>v.RenderTransform is TranslateTransform t ? Math.Abs(t.X)+Math.Abs(t.Y) : 0);
    internal void Capture()
    {
        var current=Places();Clear();foreach(var pair in current)before[pair.Key]=pair.Value;
    }
    private Dictionary<Guid,Place> Places()
    {
        var result=new Dictionary<Guid,Place>();
        foreach(var card in list.Items.OfType<MediaCard>())
            if(list.ItemContainerGenerator.ContainerFromItem(card) is ListBoxItem container && container.IsVisible &&
               VisualTreeHelper.GetChildrenCount(container)>0 && VisualTreeHelper.GetChild(container,0) is FrameworkElement visual)
            {
                var point=visual.TranslatePoint(new Point(),list);var bounds=new Rect(point,visual.RenderSize);
                if(bounds.IntersectsWith(new Rect(list.RenderSize)))result[card.Item.Id]=new(visual,bounds);
            }
        return result;
    }
    internal void Apply()
    {
        if(!(animations?.Invoke()??Motion.Smooth)){Clear();return;}
        var turn=++generation;
        list.Dispatcher.BeginInvoke(()=>{
            if(turn!=generation)return;
            if(!list.IsVisible){Clear();return;}
            list.UpdateLayout();var after=Places();
            var ease=new CubicEase{EasingMode=EasingMode.EaseOut};
            foreach(var (id,place) in after)
            {
                var old=before.GetValueOrDefault(id);
                var delta=old is null?new Vector(0,8):old.Bounds.TopLeft-place.Bounds.TopLeft;
                if(delta.Length>Math.Max(list.ActualHeight,list.ActualWidth))delta=new Vector(0,8);
                var translate=new TranslateTransform();place.Visual.RenderTransform=translate;moving.Add(place.Visual);
                translate.BeginAnimation(TranslateTransform.XProperty,new DoubleAnimation(delta.X,0,TimeSpan.FromMilliseconds(220)){EasingFunction=ease,FillBehavior=FillBehavior.Stop});
                translate.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(delta.Y,0,TimeSpan.FromMilliseconds(220)){EasingFunction=ease,FillBehavior=FillBehavior.Stop});
                if(old is null)place.Visual.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(170)){FillBehavior=FillBehavior.Stop});
            }
            var gone=before.Where(p=>!after.ContainsKey(p.Key)).Select(p=>p.Value).ToArray();
            if(gone.Length>0 && AdornerLayer.GetAdornerLayer(list) is {} layer)
            {
                ghosts=new Ghosts(list,gone){IsHitTestVisible=false};layer.Add(ghosts);
                ghosts.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(1,0,TimeSpan.FromMilliseconds(120)){FillBehavior=FillBehavior.HoldEnd});
            }
            before.Clear();
            var timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(240)};
            timer.Tick+=(_,_)=>{timer.Stop();if(turn==generation)Clear();};timer.Start();
        },System.Windows.Threading.DispatcherPriority.Loaded);
    }
    internal void Clear()
    {
        ++generation;
        if(ghosts!=null){AdornerLayer.GetAdornerLayer(list)?.Remove(ghosts);ghosts=null;}
        foreach(var visual in moving){visual.RenderTransform=Transform.Identity;visual.BeginAnimation(UIElement.OpacityProperty,null);}
        moving.Clear();before.Clear();
    }
    private sealed class Ghosts(UIElement owner,IReadOnlyList<Place> places):Adorner(owner)
    {
        protected override void OnRender(DrawingContext dc)
        {
            IsHitTestVisible=false;dc.PushClip(new RectangleGeometry(new Rect(AdornedElement.RenderSize)));
            foreach(var place in places)dc.DrawRectangle(new VisualBrush(place.Visual),null,place.Bounds);
            dc.Pop();
        }
    }
}
