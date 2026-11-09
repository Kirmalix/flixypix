using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>What a window shows while its library opens: the outline of the real window with placeholders, a thin progress
/// line and the current step, and for a moment the logo above them. It never makes the start longer. A neutral title means
/// no logo and no name. Nothing here comes from the library, so nothing can be seen before it is unlocked.</summary>
internal sealed class StartupSurface : Grid
{
    private const double IntroMilliseconds = 1320, FoldWidth = 980, RailWidth = 44;
    private readonly bool neutral;
    private readonly double sidebarWidth;
    private readonly ColumnDefinition sidebarColumn = new();
    private readonly StackPanel sidebarRows = new() { Margin = new Thickness(10, 14, 10, 10) };
    private readonly Grid skeleton = new();
    private readonly Border rowsHost = new() { ClipToBounds = true, Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextBlock phase = new() { TextTrimming = TextTrimming.CharacterEllipsis, MinHeight = 20, Margin = new Thickness(0, 6, 0, 0) };
    private readonly ScaleTransform progress = new(0, 1);
    private readonly Border progressLine = new() { Height = 2, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Stretch, RenderTransformOrigin = new Point(0, .5), IsHitTestVisible = false };
    private readonly Border slowNotice = new() { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(12, 8, 8, 8), Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock elapsed = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private readonly Button cancel = new() { Content = T("Cancel"), Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly LinearGradientBrush shimmer;
    private readonly TranslateTransform sweep = new(-1, 0);
    private readonly StackPanel failure = new() { MaxWidth = 480, Margin = new Thickness(32), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly TaskCompletionSource gone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Border? intro;
    private Image? logo;
    private Rectangle? fanLeft, fanRight;
    private TextBlock? word;
    private DispatcherTimer? introEnd;
    private string phaseText = "";
    private bool waiting, failed;
    private double builtCardWidth = -1;

    internal bool IntroStarted { get; private set; }
    internal bool IntroVisible => intro is { Visibility: Visibility.Visible };
    internal bool SlowNoticeVisible => slowNotice.Visibility == Visibility.Visible;
    internal bool IsWaiting => waiting;
    internal bool HasLogo => logo != null;
    /// <summary>Finished with fading out; the window underneath took input from the start of the fade.</summary>
    internal Task Gone => gone.Task;
    internal string PhaseShown => phase.Text;
    internal Button CancelButton => cancel;

    internal StartupSurface(bool neutral, double sidebarWidth, bool sidebarFolded, Action cancelRequested)
    {
        this.neutral = neutral; this.sidebarWidth = sidebarFolded ? RailWidth : sidebarWidth;
        SetResourceReference(BackgroundProperty, "WindowBackground");
        phase.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        shimmer = new LinearGradientBrush { StartPoint = new Point(0, .5), EndPoint = new Point(1, .5), RelativeTransform = sweep };
        var plain = Theme.Color("Skeleton", Theme.IsDark); var bright = Theme.Color("SkeletonHighlight", Theme.IsDark);
        shimmer.GradientStops.Add(new GradientStop(plain, .3)); shimmer.GradientStops.Add(new GradientStop(bright, .5)); shimmer.GradientStops.Add(new GradientStop(plain, .7));
        cancel.Click += (_, _) => cancelRequested();
        Children.Add(BuildSkeleton());
        Children.Add(failure);
        if (!neutral) Children.Add(BuildIntro());
        Shimmer(true);
        SizeChanged += (_, _) => FitSidebar();
    }

    private Border Block(double width, double height, double radius = 4, Thickness? margin = null) => new()
    {
        Width = width, Height = height, CornerRadius = new CornerRadius(radius), Background = shimmer, Margin = margin ?? new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left,
    };

    private Grid BuildSkeleton()
    {
        skeleton.ColumnDefinitions.Add(sidebarColumn); skeleton.ColumnDefinitions.Add(new ColumnDefinition());
        var side = new Border { BorderThickness = new Thickness(0, 0, 1, 0), Child = sidebarRows, ClipToBounds = true };
        side.SetResourceReference(Border.BackgroundProperty, "SidebarBackground"); side.SetResourceReference(Border.BorderBrushProperty, "Divider");
        skeleton.Children.Add(side);

        var header = new Grid { Height = 52 };
        header.SetResourceReference(BackgroundProperty, "ChromeBackground");
        var edge = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom };
        edge.SetResourceReference(Border.BackgroundProperty, "Divider");
        progressLine.SetResourceReference(Border.BackgroundProperty, "Accent");
        progressLine.RenderTransform = progress;
        AutomationProperties.SetName(progressLine, T("Progress"));
        var search = new Border { Height = 32, CornerRadius = new CornerRadius(6), Margin = new Thickness(84, 0, 300, 0), Background = shimmer };
        header.Children.Add(search); header.Children.Add(edge); header.Children.Add(progressLine);

        var heading = Block(150, 28, 5);
        slowNotice.SetResourceReference(Border.BackgroundProperty, "CardBackground"); slowNotice.SetResourceReference(Border.BorderBrushProperty, "Divider");
        var notice = new StackPanel { Orientation = Orientation.Horizontal };
        notice.Children.Add(new TextBlock { Text = T("This is taking longer than usual."), VerticalAlignment = VerticalAlignment.Center });
        elapsed.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        notice.Children.Add(elapsed); notice.Children.Add(cancel);
        slowNotice.Child = notice;
        var top = new StackPanel { Margin = new Thickness(24, 20, 24, 0) };
        top.Children.Add(heading); top.Children.Add(phase); top.Children.Add(slowNotice);
        rowsHost.Margin = new Thickness(24, 0, 24, 0);
        rowsHost.SizeChanged += (_, e) => BuildRows(e.NewSize.Width);

        var page = new DockPanel();
        DockPanel.SetDock(header, Dock.Top); DockPanel.SetDock(top, Dock.Top);
        page.Children.Add(header); page.Children.Add(top); page.Children.Add(rowsHost);
        SetColumn(page, 1); skeleton.Children.Add(page);
        FitSidebar();
        return skeleton;
    }

    /// <summary>Same width and count of cards as the start page, so that nothing jumps when it replaces this.</summary>
    private void BuildRows(double room)
    {
        var width = Math.Max(300, room - 20);
        var visible = Math.Max(2, (int)((width + 14) / (240 + 14)));
        var card = Math.Floor((width + 14) / visible - 14);
        if (card == builtCardWidth) return;
        builtCardWidth = card;
        var rows = new StackPanel();
        for (var row = 0; row < 3; row++)
        {
            rows.Children.Add(Block(170, 20, 4, new Thickness(2, 24, 0, 12)));
            var strip = new StackPanel { Orientation = Orientation.Horizontal };
            for (var i = 0; i < visible; i++)
            {
                var item = new StackPanel { Width = card, Margin = new Thickness(0, 0, 14, 0) };
                item.Children.Add(Block(card, Math.Round(card * .5625), 8));
                item.Children.Add(Block(card * .68, 13, 3, new Thickness(2, 11, 0, 0)));
                item.Children.Add(Block(card * .42, 10, 3, new Thickness(2, 7, 0, 0)));
                strip.Children.Add(item);
            }
            rows.Children.Add(strip);
        }
        rowsHost.Child = rows;
    }

    /// <summary>The bar is folded in a narrow window too, as in the finished window; the limit counts at the current interface size.</summary>
    private void FitSidebar()
    {
        var rail = sidebarWidth <= RailWidth || ActualWidth > 0 && ActualWidth < FoldWidth;
        var wanted = rail ? RailWidth : sidebarWidth;
        if (sidebarColumn.Width.Value == wanted && sidebarRows.Children.Count > 0) return;
        sidebarColumn.Width = new GridLength(wanted);
        sidebarRows.Margin = rail ? new Thickness(13, 14, 13, 10) : new Thickness(14, 14, 14, 10);
        sidebarRows.Children.Clear();
        foreach (var width in new[] { 104d, 66, 84, 0, 62, 66, 60, 0, 88, 66, 82, 94, 118, 0, 72, 0, 110, 84, 92 })
        {
            if (width == 0) { sidebarRows.Children.Add(new Border { Height = 18 }); continue; }
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 9) };
            line.Children.Add(Block(16, 16, 4));
            if (!rail) line.Children.Add(Block(width, 12, 3, new Thickness(12, 2, 0, 0)));
            sidebarRows.Children.Add(line);
        }
    }

    private Border BuildIntro()
    {
        var accent = Theme.Color("Accent", Theme.IsDark); var ground = Theme.Color("WindowBackground", Theme.IsDark);
        var glow = Color.FromRgb((byte)(ground.R + (accent.R - ground.R) * .26), (byte)(ground.G + (accent.G - ground.G) * .26), (byte)(ground.B + (accent.B - ground.B) * .26));
        var mark = new Grid { Width = 104, Height = 104, HorizontalAlignment = HorizontalAlignment.Center };
        fanLeft = FanCard("Brand"); fanRight = FanCard("Accent");
        logo = new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/flixypix-mark-192.png")), Width = 104, Height = 104, RenderTransformOrigin = new Point(.5, .5), RenderTransform = new ScaleTransform(1, 1) };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        AutomationProperties.SetName(logo, ProductIdentity.Name);
        mark.Children.Add(fanLeft); mark.Children.Add(fanRight); mark.Children.Add(logo);
        word = new TextBlock { Text = ProductIdentity.Name, FontSize = 24, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0), RenderTransform = new TranslateTransform() };
        word.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(mark); stack.Children.Add(word);
        intro = new Border
        {
            Width = 420, Height = 262, CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false, Visibility = Visibility.Collapsed, Child = stack, RenderTransformOrigin = new Point(.5, .5), RenderTransform = new ScaleTransform(1, 1),
            Background = new RadialGradientBrush(glow, ground) { Center = new Point(.5, .38), GradientOrigin = new Point(.5, .38), RadiusX = .72, RadiusY = .92 },
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 40, ShadowDepth = 10, Direction = 270, Opacity = Theme.IsDark ? .55 : .25, Color = Colors.Black, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance },
        };
        return intro;
    }

    private static Rectangle FanCard(string colour)
    {
        var card = new Rectangle { Margin = new Thickness(14), RadiusX = 12, RadiusY = 12, Opacity = 0, RenderTransformOrigin = new Point(.5, .5) };
        card.SetResourceReference(Shape.FillProperty, colour);
        var turn = new TransformGroup(); turn.Children.Add(new RotateTransform()); turn.Children.Add(new TranslateTransform());
        card.RenderTransform = turn;
        return card;
    }

    /// <summary>The short moment of the logo over the loading library; once per start, and not at all with a neutral title.</summary>
    internal void PlayIntro()
    {
        if (intro == null || IntroStarted || failed || waiting) return;
        IntroStarted = true;
        intro.Visibility = Visibility.Visible;
        introEnd = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(IntroMilliseconds + 200) };
        introEnd.Tick += (_, _) => HideIntro();
        introEnd.Start();
        if (!Motion.Enabled) { introEnd.Interval = TimeSpan.FromMilliseconds(1000); return; }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(IntroMilliseconds) };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)), ease));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1100))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(IntroMilliseconds))));
        intro.BeginAnimation(OpacityProperty, fade);
        Grow((ScaleTransform)intro.RenderTransform, .97, 260, 0, ease);
        Grow((ScaleTransform)logo!.RenderTransform, .82, 520, 90, ease);
        Appear(logo, 520, 90, ease);
        Appear(word!, 340, 380, ease);
        ((TranslateTransform)word!.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(340)) { BeginTime = TimeSpan.FromMilliseconds(380), EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        Fan(fanLeft!, -13, -18, 0, ease); Fan(fanRight!, 13, 18, 40, ease);
    }

    private static void Grow(ScaleTransform scale, double from, int milliseconds, int delay, IEasingFunction ease)
    {
        // Holds its start value until its turn, so nothing shows at full size first.
        var grow = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(delay + milliseconds) };
        grow.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        grow.KeyFrames.Add(new LinearDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));
        grow.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + milliseconds)), ease));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow); scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private static void Appear(UIElement element, int milliseconds, int delay, IEasingFunction ease)
    {
        var show = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(delay + milliseconds) };
        show.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        show.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));
        show.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + milliseconds)), ease));
        element.BeginAnimation(OpacityProperty, show);
    }

    /// <summary>A card of the logo slides out to its side and back behind it.</summary>
    private static void Fan(Rectangle card, double angle, double shift, int delay, IEasingFunction ease)
    {
        var total = TimeSpan.FromMilliseconds(720); var begin = TimeSpan.FromMilliseconds(delay);
        var turn = (TransformGroup)card.RenderTransform;
        DoubleAnimationUsingKeyFrames Out(double peak)
        {
            var frames = new DoubleAnimationUsingKeyFrames { Duration = total, BeginTime = begin };
            frames.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            frames.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromPercent(.35), ease));
            frames.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), ease));
            return frames;
        }
        turn.Children[0].BeginAnimation(RotateTransform.AngleProperty, Out(angle));
        turn.Children[1].BeginAnimation(TranslateTransform.XProperty, Out(shift));
        card.BeginAnimation(OpacityProperty, Out(.8));
    }

    private void HideIntro()
    {
        introEnd?.Stop();
        if (intro == null) return;
        intro.BeginAnimation(OpacityProperty, null);
        intro.Visibility = Visibility.Collapsed;
    }

    private void Shimmer(bool on)
    {
        if (on && Motion.Enabled) sweep.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-1, 1, TimeSpan.FromSeconds(1.5)) { RepeatBehavior = RepeatBehavior.Forever });
        else { sweep.BeginAnimation(TranslateTransform.XProperty, null); sweep.X = -1; }
    }

    /// <summary>A question is open (the password of an encrypted library, the conversion of an older one): the start waits for
    /// the person, so everything stands still and the logo makes way.</summary>
    internal void SetWaiting(bool value)
    {
        if (waiting == value || failed) return;
        waiting = value;
        phase.Text = value ? "" : phaseText;
        Shimmer(!value);
        if (value) HideIntro();
    }

    internal void SetPhase(string text, double share, double seconds = .4)
    {
        phaseText = text;
        if (!waiting) phase.Text = text;
        if (!Motion.Enabled) { progress.ScaleX = share; return; }
        progress.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(share, TimeSpan.FromSeconds(seconds)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    /// <summary>Time spent working, without any time spent waiting for an answer.</summary>
    internal void ShowTime(TimeSpan worked)
    {
        if (failed || waiting || worked.TotalSeconds < 5) return;
        slowNotice.Visibility = Visibility.Visible;
        if (worked.TotalSeconds >= 10) elapsed.Text = T("Elapsed: {0}", worked.ToString(@"mm\:ss"));
    }

    internal void SetCancelling()
    {
        cancel.IsEnabled = false;
        foreach (var button in failure.Children.OfType<Button>()) button.IsEnabled = false;
        phaseText = T("Cancelling …"); phase.Text = phaseText;
    }

    /// <summary>The library cannot be opened: the placeholders give way to the reason and the two ways on.</summary>
    internal void ShowFailure(string message, string libraryName, Action libraries, Action close)
    {
        failed = true; waiting = false;
        HideIntro(); Shimmer(false);
        skeleton.Visibility = Visibility.Collapsed;
        failure.Visibility = Visibility.Visible;
        failure.Children.Clear();
        if (!neutral)
        {
            var mark = new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/flixypix-mark-192.png")), Width = 96, Height = 96, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 16) };
            RenderOptions.SetBitmapScalingMode(mark, BitmapScalingMode.HighQuality);
            AutomationProperties.SetName(mark, ProductIdentity.Name);
            failure.Children.Add(mark);
            failure.Children.Add(new TextBlock { Text = libraryName, FontSize = 24, TextWrapping = TextWrapping.Wrap });
        }
        failure.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 18) });
        var toLibraries = new Button { Content = T("Libraries"), HorizontalAlignment = HorizontalAlignment.Left };
        toLibraries.Click += (_, _) => libraries();
        var closing = new Button { Content = T("Close"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        closing.Click += (_, _) => close();
        failure.Children.Add(toLibraries); failure.Children.Add(closing);
    }

    /// <summary>The finished window lies ready underneath and already takes input while this fades away.</summary>
    internal void FadeOut()
    {
        IsHitTestVisible = false;
        introEnd?.Stop();
        void Finish()
        {
            if (Visibility == Visibility.Collapsed) return;
            Shimmer(false); HideIntro();
            BeginAnimation(OpacityProperty, null);
            Visibility = Visibility.Collapsed;
            gone.TrySetResult();
        }
        if (!Motion.Enabled) { Finish(); return; }
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) => Finish();
        BeginAnimation(OpacityProperty, fade);
        // An animation does not run in a minimized or covered window; the input must not depend on that.
        var safety = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        safety.Tick += (_, _) => { safety.Stop(); Finish(); };
        safety.Start();
    }
}
