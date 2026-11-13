using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AutoFixScout.UI;

/// <summary>
/// The click ripple: a soft circle that grows from the exact point that was clicked and fades out, clipped to the
/// button's rounded shape. Drawn in the adorner layer so no button template has to change. Skipped when animations are off.
/// </summary>
internal sealed class RippleAdorner : Adorner
{
    private readonly Point _origin;
    private readonly double _maxRadius;
    private readonly Color _color;
    private readonly double _cornerRadius;

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(RippleAdorner), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    private RippleAdorner(UIElement element, Point origin, Color color, double cornerRadius) : base(element)
    {
        IsHitTestVisible = false;
        _origin = origin;
        _color = color;
        _cornerRadius = cornerRadius;
        var size = element.RenderSize;
        // Far enough to reach the furthest corner from the click point.
        var dx = Math.Max(origin.X, size.Width - origin.X);
        var dy = Math.Max(origin.Y, size.Height - origin.Y);
        _maxRadius = Math.Sqrt(dx * dx + dy * dy);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = AdornedElement.RenderSize;
        var p = Progress;
        var alpha = (byte)(Math.Clamp(1 - p, 0, 1) * _color.A);
        var brush = new RadialGradientBrush(
            Color.FromArgb(alpha, _color.R, _color.G, _color.B),
            Color.FromArgb((byte)(alpha * 0.35), _color.R, _color.G, _color.B));
        dc.PushClip(new RectangleGeometry(new Rect(size), _cornerRadius, _cornerRadius));
        dc.DrawEllipse(brush, null, _origin, _maxRadius * (0.15 + 0.85 * p), _maxRadius * (0.15 + 0.85 * p));
        dc.Pop();
    }

    /// <summary>Starts a ripple on <paramref name="element"/> at <paramref name="at"/> (element coordinates).</summary>
    public static void Play(FrameworkElement element, Point at, Color color, double cornerRadius)
    {
        if (!Motion.Enabled || element.ActualWidth < 1) return;
        var layer = AdornerLayer.GetAdornerLayer(element);
        if (layer is null) return;
        var ripple = new RippleAdorner(element, at, color, cornerRadius);
        layer.Add(ripple);
        var grow = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(560)) { EasingFunction = Motion.EaseOutStrong };
        grow.Completed += (_, _) => layer.Remove(ripple);
        ripple.BeginAnimation(ProgressProperty, grow);
    }
}
