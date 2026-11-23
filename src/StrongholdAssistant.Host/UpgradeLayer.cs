using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace StrongholdAssistant.Host;

public sealed class UpgradeLayer : Canvas
{
    private (System.Drawing.RectangleF? Button, int? Level, double Width, double Height) _last;

    public void Update(System.Drawing.RectangleF? button, int? level, double width, double height, bool animate = true)
    {
        var state = (button, level, width, height);
        if (state == _last) return;
        _last = state;
        Children.Clear();
        if (button is not { } box || level is null || width <= 0 || height <= 0) return;
        double k = height / 1080;
        var gold = new SolidColorBrush(Color.FromRgb(0xF3, 0xB3, 0x22));
        var frame = new Rectangle { Width = box.Width * width, Height = box.Height * height,
            Stroke = gold, StrokeThickness = 2 * k };
        SetLeft(frame, box.X * width);
        SetTop(frame, box.Y * height);
        if (animate) frame.BeginAnimation(OpacityProperty, new DoubleAnimation(1, .35, TimeSpan.FromSeconds(.8))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        Children.Add(frame);
        var tag = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x15)) { Opacity = .82 },
            Padding = new Thickness(10 * k, 4 * k, 12 * k, 5 * k),
            CornerRadius = new CornerRadius(4 * k),
            Child = new TextBlock { Text = $"升本 → {level} 级", Foreground = gold,
                FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 15 * k, FontWeight = FontWeights.Bold }
        };
        tag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        SetLeft(tag, (box.X + box.Width / 2) * width - tag.DesiredSize.Width / 2);
        SetTop(tag, box.Y * height - tag.DesiredSize.Height - 4 * k);
        Children.Add(tag);
    }
}
