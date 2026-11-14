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
/// Only the busy visuals loop.
/// </summary>
public static class Motion
{
    public static bool Enabled => SystemParameters.ClientAreaAnimation && !Reduced;

    /// <summary>Set from the "Reduce motion" setting: no movement even when Windows animations are on.</summary>
    public static bool Reduced { get; set; }

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
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300))
        { BeginTime = from, EasingFunction = EaseOut, FillBehavior = FillBehavior.HoldEnd });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(distance, 0, TimeSpan.FromMilliseconds(520))
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

    /// <summary>Soft glow on hover, in this colour (accent buttons glow in the accent). Transparent = no glow.</summary>
    public static readonly DependencyProperty GlowProperty = DependencyProperty.RegisterAttached(
        "Glow", typeof(Color), typeof(Motion), new PropertyMetadata(Colors.Transparent));
    public static Color GetGlow(DependencyObject d) => (Color)d.GetValue(GlowProperty);
    public static void SetGlow(DependencyObject d, Color value) => d.SetValue(GlowProperty, value);

    /// <summary>Colour of the click ripple (alpha included). Transparent = no ripple.</summary>
    public static readonly DependencyProperty RippleProperty = DependencyProperty.RegisterAttached(
        "Ripple", typeof(Color), typeof(Motion), new PropertyMetadata(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)));
    public static Color GetRipple(DependencyObject d) => (Color)d.GetValue(RippleProperty);
    public static void SetRipple(DependencyObject d, Color value) => d.SetValue(RippleProperty, value);

    /// <summary>Corner radius the ripple is clipped to.</summary>
    public static readonly DependencyProperty RippleRadiusProperty = DependencyProperty.RegisterAttached(
        "RippleRadius", typeof(double), typeof(Motion), new PropertyMetadata(7.0));
    public static double GetRippleRadius(DependencyObject d) => (double)d.GetValue(RippleRadiusProperty);
    public static void SetRippleRadius(DependencyObject d, double value) => d.SetValue(RippleRadiusProperty, value);

    private static readonly IEasingFunction Spring = Frozen(new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 7 });

    private static void OnPressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase b || e.NewValue is not true) return;
        b.RenderTransform = new TransformGroup { Children = { new ScaleTransform(1, 1), new TranslateTransform() } };
        b.MouseEnter += (_, _) => Settle(b);
        b.MouseLeave += (_, _) => Settle(b);
        b.GotKeyboardFocus += (_, _) => Settle(b);
        b.LostKeyboardFocus += (_, _) => Settle(b);
        // Impact: a ripple from the exact point that was pressed (keyboard presses ripple from the centre).
        b.PreviewMouseLeftButtonDown += (_, args) =>
        {
            if (GetRipple(b).A > 0) RippleAdorner.Play(b, args.GetPosition(b), GetRipple(b), GetRippleRadius(b));
        };
        b.PreviewKeyDown += (_, args) =>
        {
            if (args.Key is System.Windows.Input.Key.Space or System.Windows.Input.Key.Enter && GetRipple(b).A > 0)
                RippleAdorner.Play(b, new Point(b.ActualWidth / 2, b.ActualHeight / 2), GetRipple(b), GetRippleRadius(b));
        };
        IsPressedDescriptor.AddValueChanged(b, (_, _) => Settle(b, released: !b.IsPressed));
    }

    private static void Settle(ButtonBase b, bool released = false)
    {
        if (b.RenderTransform is not TransformGroup { Children: [ScaleTransform s, TranslateTransform t] }) return;
        var hover = b.IsMouseOver || b.IsKeyboardFocused;
        var target = b.IsPressed ? 0.955 : hover ? GetHoverScale(b) : 1.0;
        var lift = b.IsPressed || !hover ? 0 : -GetHoverLift(b);
        // Press goes in fast; release springs back with a small overshoot; hover eases.
        var (ms, ease) = b.IsPressed ? (90, EaseOut) : released ? (520, Spring) : (280, EaseOutStrong);
        To(s, ScaleTransform.ScaleXProperty, target, ms, ease);
        To(s, ScaleTransform.ScaleYProperty, target, ms, ease);
        To(t, TranslateTransform.YProperty, lift, ms, EaseOutStrong);
        Glow(b, hover && !b.IsPressed);
        // Cards: the hover glow layer fades rather than switching (template part "Hover").
        if (b is System.Windows.Controls.Control c && c.Template?.FindName("Hover", c) is UIElement glow)
            To(glow, UIElement.OpacityProperty, b.IsMouseOver || b.IsKeyboardFocused ? 1 : 0, b.IsMouseOver ? 180 : 320);
    }

    /// <summary>Soft coloured light around the button while it is hovered: fades in (blur and strength), fades out.</summary>
    private static void Glow(ButtonBase b, bool on)
    {
        var color = GetGlow(b);
        if (color.A == 0 || !Enabled || SystemParameters.HighContrast) { b.Effect = null; return; }
        if (b.Effect is not System.Windows.Media.Effects.DropShadowEffect fx)
        {
            if (!on) return;
            b.Effect = fx = new System.Windows.Media.Effects.DropShadowEffect
            { Color = color, ShadowDepth = 0, BlurRadius = 0, Opacity = 0, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance };
        }
        To(fx, System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, on ? 22 : 0, on ? 260 : 360, EaseOutStrong);
        To(fx, System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, on ? color.A / 255.0 : 0, on ? 220 : 360);
    }
}
