using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AutoFixScout.UI;

/// <summary>
/// All movement in the app goes through here so it can follow the Windows "Animation effects" setting
/// (SystemParameters.ClientAreaAnimation): when it's off, every change jumps straight to its end state.
/// Rules: 150-300 ms, decelerating curves for things arriving, nothing that delays a click.
/// </summary>
public static class Motion
{
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    public static readonly IEasingFunction EaseOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction EaseOutStrong = Frozen(new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 });
    public static readonly IEasingFunction Overshoot = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 });

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
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
        { BeginTime = from, EasingFunction = EaseOut, FillBehavior = FillBehavior.HoldEnd });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(distance, 0, TimeSpan.FromMilliseconds(320))
        { BeginTime = from, EasingFunction = EaseOutStrong });
        if (delayMs > 0) element.Opacity = 0; // hidden until its turn (the animation's base value)
    }

    /// <summary>Endless rotation for the busy ring. Pass false to stop it.</summary>
    public static void Spin(RotateTransform rotate, bool on)
    {
        if (!on || !Enabled) { rotate.BeginAnimation(RotateTransform.AngleProperty, null); return; }
        rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(1100))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = Frozen(new SineEase { EasingMode = EasingMode.EaseInOut }),
        });
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

    private static readonly DependencyPropertyDescriptor IsPressedDescriptor =
        DependencyPropertyDescriptor.FromProperty(ButtonBase.IsPressedProperty, typeof(ButtonBase));

    private static void OnPressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase b || e.NewValue is not true) return;
        b.RenderTransform = new ScaleTransform(1, 1);
        b.MouseEnter += (_, _) => Settle(b);
        b.MouseLeave += (_, _) => Settle(b);
        IsPressedDescriptor.AddValueChanged(b, (_, _) => Settle(b));
    }

    private static void Settle(ButtonBase b)
    {
        if (b.RenderTransform is not ScaleTransform s) return;
        var target = b.IsPressed ? 0.975 : b.IsMouseOver ? GetHoverScale(b) : 1.0;
        var ms = b.IsPressed ? 90 : 200;
        To(s, ScaleTransform.ScaleXProperty, target, ms);
        To(s, ScaleTransform.ScaleYProperty, target, ms);
    }
}
