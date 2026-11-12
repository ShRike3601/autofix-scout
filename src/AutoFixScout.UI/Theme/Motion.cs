using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AutoFixScout.UI;

/// <summary>
/// All movement in the app goes through here so it can follow the Windows "Animation effects" setting
/// (SystemParameters.ClientAreaAnimation): when it's off, every change jumps straight to its end state and nothing loops.
/// Rules: 150-350 ms with decelerating curves for anything that responds or arrives, nothing that delays a click.
/// Only the header glows and the busy visuals loop, slowly.
/// </summary>
public static class Motion
{
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    private static readonly Color[] ConfettiColors =
        { Color.FromRgb(0x60, 0xCD, 0xFF), Color.FromRgb(0xA7, 0x8B, 0xFA), Color.FromRgb(0x6C, 0xCB, 0x5F),
          Color.FromRgb(0xFF, 0xB9, 0x00), Color.FromRgb(0xFF, 0x7A, 0x8A), Color.FromRgb(0xFF, 0xFF, 0xFF) };

    /// <summary>
    /// A short burst of confetti over the window when a problem is fixed: ~1.8 s, then the pieces are removed.
    /// Nothing at all when Windows animation effects are off.
    /// </summary>
    public static void Confetti(System.Windows.Controls.Canvas layer, int pieces = 70)
    {
        if (!Enabled || layer.ActualWidth < 10) return;
        layer.Children.Clear();
        var rnd = new Random();
        var w = layer.ActualWidth;
        var h = layer.ActualHeight;
        for (var i = 0; i < pieces; i++)
        {
            var rotate = new RotateTransform(rnd.Next(360));
            var piece = new System.Windows.Shapes.Rectangle
            {
                Width = 6 + rnd.Next(6), Height = 10 + rnd.Next(8), RadiusX = 2, RadiusY = 2,
                Fill = new SolidColorBrush(ConfettiColors[rnd.Next(ConfettiColors.Length)]),
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = rotate,
            };
            var x = w * (0.15 + 0.7 * rnd.NextDouble());
            System.Windows.Controls.Canvas.SetLeft(piece, x);
            System.Windows.Controls.Canvas.SetTop(piece, -30);
            layer.Children.Add(piece);

            var ms = 1300 + rnd.Next(700);
            var delay = rnd.Next(250);
            FromTo(piece, System.Windows.Controls.Canvas.TopProperty, -30, h * (0.55 + 0.45 * rnd.NextDouble()), ms, EaseOut, delay);
            FromTo(piece, System.Windows.Controls.Canvas.LeftProperty, x, x + rnd.Next(-160, 160), ms, EaseOut, delay);
            FromTo(rotate, RotateTransform.AngleProperty, rotate.Angle, rotate.Angle + rnd.Next(-540, 540), ms, null, delay);
            FromTo(piece, UIElement.OpacityProperty, 1, 0, 450, null, delay + ms - 450);
        }
        var cleanup = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2400) };
        cleanup.Tick += (_, _) => { cleanup.Stop(); layer.Children.Clear(); };
        cleanup.Start();
    }

    public static readonly IEasingFunction EaseOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction EaseOutStrong = Frozen(new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 });
    public static readonly IEasingFunction Overshoot = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 });
    private static readonly IEasingFunction Gentle = Frozen(new SineEase { EasingMode = EasingMode.EaseInOut });

    private static IEasingFunction Frozen(EasingFunctionBase e) { e.Freeze(); return e; }

    /// <summary>Animates a double property to <paramref name="to"/>, or sets it at once when animations are off.</summary>
    public static void To(IAnimatable target, DependencyProperty dp, double to, int ms, IEasingFunction? ease = null, int delayMs = 0)
    {
        if (!Enabled)
        {
            target.BeginAnimation(dp, null);
            ((DependencyObject)target).SetValue(dp, to);
            return;
        }
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = ease ?? EaseOut,
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
        };
        target.BeginAnimation(dp, a, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Like <see cref="To"/> but always starts from <paramref name="from"/> (for one-off reveals).</summary>
    public static void FromTo(IAnimatable target, DependencyProperty dp, double from, double to, int ms, IEasingFunction? ease = null, int delayMs = 0)
    {
        target.BeginAnimation(dp, null);
        ((DependencyObject)target).SetValue(dp, Enabled ? from : to);
        if (Enabled) To(target, dp, to, ms, ease, delayMs);
    }

    /// <summary>Page entrance: fade in while sliding up a little.</summary>
    public static void Enter(FrameworkElement element, int delayMs = 0, double distance = 14)
    {
        var shift = element.RenderTransform as TranslateTransform;
        if (shift is null || shift.IsFrozen) element.RenderTransform = shift = new TranslateTransform();
        if (!Enabled)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            shift.BeginAnimation(TranslateTransform.YProperty, null);
            element.Opacity = 1;
            shift.Y = 0;
            return;
        }
        var from = TimeSpan.FromMilliseconds(delayMs);
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240))
        { BeginTime = from, EasingFunction = EaseOut, FillBehavior = FillBehavior.HoldEnd });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(distance, 0, TimeSpan.FromMilliseconds(340))
        { BeginTime = from, EasingFunction = EaseOutStrong });
        if (delayMs > 0) element.Opacity = 0; // hidden until its turn (the animation's base value)
    }

    /// <summary>Simple cross-fade in (no movement), for swapping header content.</summary>
    public static void FadeIn(UIElement element, int ms = 220)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;
        if (Enabled)
            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut });
    }

    /// <summary>Endless rotation (busy ring, scanner sweep). Pass false to stop it.</summary>
    public static void Spin(RotateTransform rotate, bool on, int periodMs = 1100, bool linear = false)
    {
        if (!on || !Enabled) { rotate.BeginAnimation(RotateTransform.AngleProperty, null); return; }
        rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(periodMs))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = linear ? null : Gentle,
        });
    }

    /// <summary>Header glows: a very slow, gentle back-and-forth. Off (and reset) when animation effects are off.</summary>
    public static void Drift(TranslateTransform shift, double dx, double dy, double seconds)
    {
        shift.BeginAnimation(TranslateTransform.XProperty, null);
        shift.BeginAnimation(TranslateTransform.YProperty, null);
        shift.X = shift.Y = 0;
        if (!Enabled) return;
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, dx, TimeSpan.FromSeconds(seconds))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Gentle });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, dy, TimeSpan.FromSeconds(seconds * 1.3))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Gentle });
    }

    /// <summary>A ring that grows and fades out, repeating (the scanner's sonar rings). Without animation: a faint still ring.</summary>
    public static void Pulse(UIElement ring, ScaleTransform scale, int delayMs, bool on)
    {
        ring.BeginAnimation(UIElement.OpacityProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = scale.ScaleY = 1;
        ring.Opacity = on ? 0.3 : 0;
        if (!on || !Enabled) return;
        var period = TimeSpan.FromMilliseconds(2400);
        var begin = TimeSpan.FromMilliseconds(delayMs);
        var grow = new DoubleAnimation(0.5, 1.0, period) { EasingFunction = EaseOut };
        var fade = new DoubleAnimation(0.75, 0, period);
        // Wrapped in a storyboard-like clock so the delay happens once, then it repeats without gaps.
        ring.Opacity = 0;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Looping(grow, begin));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Looping(grow, begin));
        ring.BeginAnimation(UIElement.OpacityProperty, Looping(fade, begin));
    }

    private static AnimationTimeline Looping(DoubleAnimation inner, TimeSpan delay)
    {
        inner.RepeatBehavior = RepeatBehavior.Forever;
        inner.BeginTime = delay;
        return inner;
    }

    // ---- hover / press feedback for buttons (attached properties set from Styles.xaml) ----

    public static readonly DependencyProperty PressProperty = DependencyProperty.RegisterAttached(
        "Press", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnPressChanged));
    public static bool GetPress(DependencyObject d) => (bool)d.GetValue(PressProperty);
    public static void SetPress(DependencyObject d, bool value) => d.SetValue(PressProperty, value);

    public static readonly DependencyProperty HoverScaleProperty = DependencyProperty.RegisterAttached(
        "HoverScale", typeof(double), typeof(Motion), new PropertyMetadata(1.0));
    public static double GetHoverScale(DependencyObject d) => (double)d.GetValue(HoverScaleProperty);
    public static void SetHoverScale(DependencyObject d, double value) => d.SetValue(HoverScaleProperty, value);

    /// <summary>How far a button rises on hover, in pixels (home cards).</summary>
    public static readonly DependencyProperty HoverLiftProperty = DependencyProperty.RegisterAttached(
        "HoverLift", typeof(double), typeof(Motion), new PropertyMetadata(0.0));
    public static double GetHoverLift(DependencyObject d) => (double)d.GetValue(HoverLiftProperty);
    public static void SetHoverLift(DependencyObject d, double value) => d.SetValue(HoverLiftProperty, value);

    private static readonly DependencyPropertyDescriptor IsPressedDescriptor =
        DependencyPropertyDescriptor.FromProperty(ButtonBase.IsPressedProperty, typeof(ButtonBase));

    private static void OnPressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase b || e.NewValue is not true) return;
        b.RenderTransform = new TransformGroup { Children = { new ScaleTransform(1, 1), new TranslateTransform() } };
        b.MouseEnter += (_, _) => Settle(b);
        b.MouseLeave += (_, _) => Settle(b);
        IsPressedDescriptor.AddValueChanged(b, (_, _) => Settle(b));
    }

    private static void Settle(ButtonBase b)
    {
        if (b.RenderTransform is not TransformGroup { Children: [ScaleTransform s, TranslateTransform t] }) return;
        var target = b.IsPressed ? 0.975 : b.IsMouseOver ? GetHoverScale(b) : 1.0;
        var lift = b.IsPressed || !b.IsMouseOver ? 0 : -GetHoverLift(b);
        var ms = b.IsPressed ? 100 : 220;
        To(s, ScaleTransform.ScaleXProperty, target, ms);
        To(s, ScaleTransform.ScaleYProperty, target, ms);
        To(t, TranslateTransform.YProperty, lift, ms);
    }
}
