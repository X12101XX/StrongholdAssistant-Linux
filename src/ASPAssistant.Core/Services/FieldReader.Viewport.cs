using System.Drawing;
using ASPAssistant.Core.Models;
using OpenCvSharp;

namespace ASPAssistant.Core.Services;

public sealed partial class FieldReader
{
    internal static (byte[] Frame, RectangleF Viewport) NormalizeBoardViewport(byte[] frame, int width, int height)
    {
        if (width * 9 == height * 16) return (frame, new(0, 0, 1, 1));
        using var raw = Cv2.ImDecode(frame, ImreadModes.Color);
        using var scaled = new Mat();
        double scale = Math.Max(1920d / width, 1080d / height);
        int scaledWidth = (int)Math.Round(width * scale);
        int scaledHeight = (int)Math.Round(height * scale);
        Cv2.Resize(raw, scaled, new OpenCvSharp.Size(scaledWidth, scaledHeight));
        int left = (scaledWidth - 1920) / 2;
        int top = (scaledHeight - 1080) / 2;
        using var crop = new Mat(scaled, new Rect(left, top, 1920, 1080));
        return (crop.ImEncode(".png"), new((float)left / scaledWidth, (float)top / scaledHeight,
            1920f / scaledWidth, 1080f / scaledHeight));
    }

    internal static FieldOverlayGeometry RestoreBoardViewport(FieldOverlayGeometry geometry, RectangleF viewport)
    {
        (double X, double Y) Restore((double X, double Y) p)
            => (viewport.X + p.X * viewport.Width, viewport.Y + p.Y * viewport.Height);
        var corners = new[] { (0d, 0d), (7d, 0d), (7d, 5d), (0d, 5d) };
        return new(new BoardProjection(corners.Select(p => (p, Restore(geometry.Projection.Project(p.Item1, p.Item2)))).ToArray()),
            geometry.Anchors.ToDictionary(p => p.Key, p => Restore(p.Value)),
            geometry.Badges.ToDictionary(p => p.Key, p => p.Value with
            {
                X = viewport.X + p.Value.X * viewport.Width,
                Y = viewport.Y + p.Value.Y * viewport.Height,
                Size = p.Value.Size * viewport.Height
            }));
    }
}
