using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AutoFixScout.UI;

/// <summary>
/// The soft colour field behind the glass (in the spirit of iOS materials, where frosting reads as rich because there
/// is colour behind it): three large, very soft colour pools that drift slowly. Only shown with glass; static when
/// animations are off; never hit-testable.
/// </summary>
public static class Ambient
{
    private static readonly (Color Color, double X, double Y, double Size, double DriftX, double DriftY, int Seconds)[] Pools =
    {
        (Color.FromRgb(0x2F, 0x7D, 0xFF), 0.62, -0.18, 0.95, -0.10, 0.08, 23), // blue, top right
        (Color.FromRgb(0x8B, 0x5C, 0xF6), -0.20, 0.45, 0.85, 0.08, -0.07, 29), // violet, left
        (Color.FromRgb(0x14, 0xB8, 0xA6), 0.55, 0.70, 0.75, -0.07, -0.06, 31), // teal, bottom
    };

    public static void Attach(Canvas canvas, bool on)
    {
        canvas.Children.Clear();
        canvas.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) return;
        var pools = new List<(System.Windows.Shapes.Ellipse Shape, TranslateTransform Move, int Index)>();
        for (var i = 0; i < Pools.Length; i++)
        {
            var p = Pools[i];
            var brush = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x5A, p.Color.R, p.Color.G, p.Color.B), 0),
                    new GradientStop(Color.FromArgb(0x22, p.Color.R, p.Color.G, p.Color.B), 0.45),
                    new GradientStop(Color.FromArgb(0x00, p.Color.R, p.Color.G, p.Color.B), 1),
                },
            };
            brush.Freeze();
            var move = new TranslateTransform();
            var shape = new System.Windows.Shapes.Ellipse { Fill = brush, RenderTransform = move, IsHitTestVisible = false };
            canvas.Children.Add(shape);
            pools.Add((shape, move, i));
        }
        void Layout()
        {
            var w = canvas.ActualWidth; var h = canvas.ActualHeight;
            if (w < 1 || h < 1) return;
            foreach (var (shape, move, i) in pools)
            {
                var p = Pools[i];
                var size = Math.Max(w, h) * p.Size;
                shape.Width = shape.Height = size;
                Canvas.SetLeft(shape, w * p.X);
                Canvas.SetTop(shape, h * p.Y);
                if (!Motion.Enabled) continue;
                Drift(move, TranslateTransform.XProperty, w * p.DriftX, p.Seconds);
                Drift(move, TranslateTransform.YProperty, h * p.DriftY, p.Seconds + 7);
            }
        }
        canvas.SizeChanged += (_, _) => Layout();
        Layout();
    }

    private static void Drift(TranslateTransform t, DependencyProperty dp, double by, int seconds) =>
        t.BeginAnimation(dp, new DoubleAnimation(0, by, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
}

