using System.Drawing;

namespace ASPAssistant.Core.Services;

/// <summary>物资页按短边缩放，保留完整画幅和贴边控件；高画面的卡片区域上下居中。</summary>
public static class RosterCoordinates
{
    public static double Scale(int width, int height) => Math.Min(width / 1920.0, height / 1080.0);

    public static Bitmap Normalize(Bitmap image)
    {
        double scale = Scale(image.Width, image.Height);
        var normalized = new Bitmap((int)Math.Round(image.Width / scale), (int)Math.Round(image.Height / scale));
        using var graphics = Graphics.FromImage(normalized);
        graphics.DrawImage(image, new Rectangle(0, 0, normalized.Width, normalized.Height),
            new Rectangle(0, 0, image.Width, image.Height), GraphicsUnit.Pixel);
        return normalized;
    }

    public static Point ToScreen(int x, int y, int width, int height)
    {
        double scale = Scale(width, height);
        return new((int)(x * scale), (int)(y * scale));
    }

    public static (Point Start, Point End) Drag(bool left, int distance, int width, int height)
    {
        double scale = Scale(width, height);
        int y = (int)(450 + (height / scale - 1080) / 2);
        return (ToScreen(left ? 210 + distance : 210, y, width, height),
            ToScreen(left ? 210 : 210 + distance, y, width, height));
    }
}
