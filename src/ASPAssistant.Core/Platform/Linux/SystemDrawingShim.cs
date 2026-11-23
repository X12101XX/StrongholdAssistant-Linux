#if LINUX_PORT
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace System.Drawing.Imaging
{
    public enum PixelFormat
    {
        Format24bppRgb = 137224,
        Format32bppRgb = 139273,
        Format32bppArgb = 2498570,
        Format32bppPArgb = 925707,
        DontCare = 0
    }

    public enum ImageLockMode
    {
        ReadOnly = 1,
        WriteOnly = 2,
        ReadWrite = 3,
        UserInputBuffer = 4
    }

    public sealed class ImageFormat
    {
        public static ImageFormat Png { get; } = new("png");
        public static ImageFormat Bmp { get; } = new("bmp");
        public static ImageFormat Jpeg { get; } = new("jpeg");
        public string Name { get; }
        private ImageFormat(string name) => Name = name;
    }

    public sealed class BitmapData
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int Stride { get; set; }
        public PixelFormat PixelFormat { get; set; }
        public IntPtr Scan0 { get; set; }
        internal Rectangle LockedRect { get; set; }
        internal ImageLockMode LockMode { get; set; }
        internal bool OwnsBuffer { get; set; }
    }

    public sealed class ColorMatrix
    {
        public float[][] Matrix { get; }
        public ColorMatrix(float[][] newColorMatrix) => Matrix = newColorMatrix;
    }

    public sealed class ImageAttributes : IDisposable
    {
        internal ColorMatrix? ColorMatrix { get; private set; }
        internal WrapMode WrapMode { get; private set; } = WrapMode.Clamp;
        public void SetColorMatrix(ColorMatrix newColorMatrix) => ColorMatrix = newColorMatrix;
        public void SetWrapMode(WrapMode mode) => WrapMode = mode;
        public void Dispose() { }
    }
}

namespace System.Drawing.Drawing2D
{
    public enum WrapMode
    {
        Tile = 0,
        TileFlipX = 1,
        TileFlipY = 2,
        TileFlipXY = 3,
        Clamp = 4
    }
    public enum InterpolationMode
    {
        Default = 0,
        Low = 1,
        High = 2,
        Bilinear = 3,
        Bicubic = 4,
        NearestNeighbor = 5,
        HighQualityBilinear = 6,
        HighQualityBicubic = 7
    }

    public enum SmoothingMode
    {
        Default = 0,
        HighSpeed = 1,
        HighQuality = 2,
        None = 3,
        AntiAlias = 4
    }

    public enum PixelOffsetMode
    {
        Default = 0,
        HighSpeed = 1,
        HighQuality = 2,
        None = 3,
        Half = 4
    }

    public enum CompositingQuality
    {
        Default = 0,
        HighSpeed = 1,
        HighQuality = 2
    }

    public sealed class AdjustableArrowCap : IDisposable
    {
        public float Width { get; }
        public float Height { get; }
        public bool Filled { get; }
        public AdjustableArrowCap(float width, float height, bool isFilled = true)
        {
            Width = width;
            Height = height;
            Filled = isFilled;
        }
        public void Dispose() { }
    }
}

namespace System.Drawing
{
    public enum GraphicsUnit
    {
        World = 0,
        Display = 1,
        Pixel = 2,
        Point = 3,
        Inch = 4,
        Document = 5,
        Millimeter = 6
    }

    [Flags]
    public enum FontStyle
    {
        Regular = 0,
        Bold = 1,
        Italic = 2,
        Underline = 4,
        Strikeout = 8
    }

    public sealed class Font : IDisposable
    {
        public string FamilyName { get; }
        public float Size { get; }
        public FontStyle Style { get; }
        public GraphicsUnit Unit { get; }
        internal SKTypeface Typeface { get; }
        internal float PixelSize => Unit == GraphicsUnit.Pixel ? Size : Size * (96f / 72f);

        public Font(string familyName, float emSize, FontStyle style = FontStyle.Regular, GraphicsUnit unit = GraphicsUnit.Point)
        {
            FamilyName = familyName;
            Size = emSize;
            Style = style;
            Unit = unit;
            var skStyle = new SKFontStyle(
                style.HasFlag(FontStyle.Bold) ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
                SKFontStyleWidth.Normal,
                style.HasFlag(FontStyle.Italic) ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
            Typeface = ResolveTypeface(familyName, skStyle);
        }

        private static SKTypeface ResolveTypeface(string requested, SKFontStyle style)
        {
            // Map common Windows CJK fonts to Linux equivalents when not installed.
            string[] candidates = requested.Contains("YaHei", StringComparison.OrdinalIgnoreCase)
                ? [requested, "Noto Sans CJK SC", "Source Han Sans SC", "WenQuanYi Micro Hei", "Droid Sans Fallback", "DejaVu Sans"]
                : [requested, "Noto Sans CJK SC", "DejaVu Sans"];
            foreach (var name in candidates)
            {
                var tf = SKTypeface.FromFamilyName(name, style);
                if (tf is not null && !string.Equals(tf.FamilyName, "DejaVu Sans", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "DejaVu Sans", StringComparison.OrdinalIgnoreCase))
                {
                    if (tf is not null) return tf;
                }
                tf?.Dispose();
            }
            return SKTypeface.Default;
        }

        public void Dispose()
        {
            if (!ReferenceEquals(Typeface, SKTypeface.Default))
                Typeface.Dispose();
        }
    }

    public abstract class Brush : IDisposable
    {
        internal abstract Color ColorValue { get; }
        public virtual void Dispose() { }
    }

    public sealed class SolidBrush : Brush
    {
        public Color Color { get; set; }
        internal override Color ColorValue => Color;
        public SolidBrush(Color color) => Color = color;
    }

    public static class Brushes
    {
        public static Brush White { get; } = new SolidBrush(Color.White);
        public static Brush Black { get; } = new SolidBrush(Color.Black);
        public static Brush Red { get; } = new SolidBrush(Color.Red);
        public static Brush Yellow { get; } = new SolidBrush(Color.Yellow);
        public static Brush Green { get; } = new SolidBrush(Color.Green);
        public static Brush Transparent { get; } = new SolidBrush(Color.Transparent);
    }

    public sealed class Pen : IDisposable, ICloneable
    {
        public Color Color { get; set; }
        public float Width { get; set; }
        public AdjustableArrowCap? CustomEndCap { get; set; }

        public Pen(Color color, float width = 1f)
        {
            Color = color;
            Width = width;
        }

        public object Clone() => new Pen(Color, Width) { CustomEndCap = CustomEndCap };
        public void Dispose() { }
    }

    public abstract class Image : IDisposable
    {
        internal SKBitmap SkBitmap { get; set; } = null!;
        public int Width => SkBitmap.Width;
        public int Height => SkBitmap.Height;
        public Size Size => new(Width, Height);
        public PixelFormat PixelFormat { get; protected set; } = PixelFormat.Format32bppArgb;

        public static Image FromStream(Stream stream) => new Bitmap(stream);
        public static Image FromFile(string filename) => new Bitmap(filename);

        public void Save(Stream stream, ImageFormat format)
        {
            if (ReferenceEquals(format, ImageFormat.Bmp))
            {
                WriteBmp(stream, SkBitmap, PixelFormat == PixelFormat.Format24bppRgb ? 24 : 32);
                return;
            }
            var skFormat = ReferenceEquals(format, ImageFormat.Jpeg)
                ? SKEncodedImageFormat.Jpeg
                : SKEncodedImageFormat.Png;
            using var img = SKImage.FromBitmap(SkBitmap);
            using var data = img.Encode(skFormat, 95);
            data.SaveTo(stream);
        }

        public void Save(string filename, ImageFormat format)
        {
            using var fs = File.Create(filename);
            Save(fs, format);
        }

        public void Save(string filename)
        {
            var ext = Path.GetExtension(filename).ToLowerInvariant();
            var fmt = ext is ".jpg" or ".jpeg" ? ImageFormat.Jpeg
                : ext == ".bmp" ? ImageFormat.Bmp
                : ImageFormat.Png;
            Save(filename, fmt);
        }

        internal static unsafe void WriteBmp(Stream stream, SKBitmap bitmap, int bpp)
        {
            int w = bitmap.Width;
            int h = bitmap.Height;
            int bytesPerPixel = bpp / 8;
            int rowStride = (w * bytesPerPixel + 3) & ~3;
            int pixelBytes = rowStride * h;
            int fileSize = 54 + pixelBytes;

            Span<byte> header = stackalloc byte[54];
            header.Clear();
            header[0] = (byte)'B';
            header[1] = (byte)'M';
            BitConverter.TryWriteBytes(header[2..], fileSize);
            BitConverter.TryWriteBytes(header[10..], 54);
            BitConverter.TryWriteBytes(header[14..], 40);
            BitConverter.TryWriteBytes(header[18..], w);
            BitConverter.TryWriteBytes(header[22..], h); // standard bottom-up DIB (matches Windows GDI+)
            BitConverter.TryWriteBytes(header[26..], (short)1);
            BitConverter.TryWriteBytes(header[28..], (short)bpp);
            BitConverter.TryWriteBytes(header[34..], pixelBytes);
            stream.Write(header);

            byte* srcBase = (byte*)bitmap.GetPixels().ToPointer();
            int srcRowBytes = bitmap.RowBytes;
            byte[] rowBuf = new byte[rowStride];
            for (int y = h - 1; y >= 0; y--)
            {
                byte* srcRow = srcBase + y * srcRowBytes;
                if (bytesPerPixel == 4)
                {
                    Marshal.Copy((IntPtr)srcRow, rowBuf, 0, w * 4);
                }
                else
                {
                    for (int x = 0; x < w; x++)
                    {
                        rowBuf[x * 3 + 0] = srcRow[x * 4 + 0];
                        rowBuf[x * 3 + 1] = srcRow[x * 4 + 1];
                        rowBuf[x * 3 + 2] = srcRow[x * 4 + 2];
                    }
                }
                stream.Write(rowBuf, 0, rowStride);
            }
        }

        public void Dispose()
        {
            SkBitmap?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    public sealed class Bitmap : Image
    {
        public Bitmap(int width, int height, PixelFormat format = PixelFormat.Format32bppArgb)
        {
            PixelFormat = format;
            SkBitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            SkBitmap.Erase(SKColors.Transparent);
        }

        public Bitmap(string filename)
        {
            using var fs = File.OpenRead(filename);
            SkBitmap = DecodeToBgraUnpremul(fs);
            PixelFormat = PixelFormat.Format32bppArgb;
        }

        public Bitmap(Stream stream)
        {
            SkBitmap = DecodeToBgraUnpremul(stream);
            PixelFormat = PixelFormat.Format32bppArgb;
        }

        public Bitmap(Image original)
        {
            PixelFormat = original.PixelFormat;
            SkBitmap = new SKBitmap(new SKImageInfo(original.Width, original.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            using var canvas = new SKCanvas(SkBitmap);
            canvas.DrawBitmap(original.SkBitmap, 0, 0);
        }

        public Bitmap(Image original, int width, int height)
        {
            PixelFormat = original.PixelFormat;
            SkBitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            using var canvas = new SKCanvas(SkBitmap);
            using var srcImg = SKImage.FromBitmap(original.SkBitmap);
            canvas.DrawImage(srcImg, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        }

        public Bitmap(Image original, Size newSize) : this(original, newSize.Width, newSize.Height) { }

        internal Bitmap(SKBitmap skBitmap, PixelFormat format = PixelFormat.Format32bppArgb)
        {
            SkBitmap = skBitmap;
            PixelFormat = format;
        }

        private static SKBitmap DecodeToBgraUnpremul(Stream stream)
        {
            if (stream.CanSeek) stream.Position = 0;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();
            if (bytes.Length >= 54 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
            {
                var bmp = TryDecodeBmp(bytes);
                if (bmp is not null) return bmp;
            }
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data) ?? throw new ArgumentException("Unable to decode image stream.");
            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            var bitmap = new SKBitmap(info);
            var result = codec.GetPixels(info, bitmap.GetPixels());
            if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
            {
                bitmap.Dispose();
                throw new ArgumentException($"Image decode failed: {result}");
            }
            return bitmap;
        }

        private static unsafe SKBitmap? TryDecodeBmp(byte[] bytes)
        {
            int dataOffset = BitConverter.ToInt32(bytes, 10);
            int dibSize = BitConverter.ToInt32(bytes, 14);
            if (dibSize < 40 || dataOffset < 54 || dataOffset >= bytes.Length) return null;
            int width = BitConverter.ToInt32(bytes, 18);
            int rawHeight = BitConverter.ToInt32(bytes, 22);
            short bpp = BitConverter.ToInt16(bytes, 28);
            int compression = BitConverter.ToInt32(bytes, 30);
            if (width <= 0 || rawHeight == 0 || (bpp != 24 && bpp != 32) || (compression != 0 && compression != 3))
                return null;

            bool topDown = rawHeight < 0;
            int height = Math.Abs(rawHeight);
            int bytesPerPixel = bpp / 8;
            int srcStride = (width * bytesPerPixel + 3) & ~3;
            if ((long)dataOffset + (long)srcStride * height > bytes.Length) return null;

            var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            byte* dstBase = (byte*)bitmap.GetPixels().ToPointer();
            int dstStride = bitmap.RowBytes;
            fixed (byte* srcFile = bytes)
            {
                byte* srcPixels = srcFile + dataOffset;
                for (int y = 0; y < height; y++)
                {
                    int srcY = topDown ? y : (height - 1 - y);
                    byte* srcRow = srcPixels + srcY * srcStride;
                    byte* dstRow = dstBase + y * dstStride;
                    if (bytesPerPixel == 4)
                    {
                        Buffer.MemoryCopy(srcRow, dstRow, dstStride, width * 4);
                    }
                    else
                    {
                        for (int x = 0; x < width; x++)
                        {
                            dstRow[x * 4 + 0] = srcRow[x * 3 + 0];
                            dstRow[x * 4 + 1] = srcRow[x * 3 + 1];
                            dstRow[x * 4 + 2] = srcRow[x * 3 + 2];
                            dstRow[x * 4 + 3] = 255;
                        }
                    }
                }
            }
            return bitmap;
        }

        public unsafe Color GetPixel(int x, int y)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
                throw new ArgumentOutOfRangeException();
            byte* ptr = (byte*)SkBitmap.GetPixels().ToPointer() + y * SkBitmap.RowBytes + x * 4;
            return Color.FromArgb(ptr[3], ptr[2], ptr[1], ptr[0]);
        }

        public unsafe void SetPixel(int x, int y, Color color)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
                throw new ArgumentOutOfRangeException();
            byte* ptr = (byte*)SkBitmap.GetPixels().ToPointer() + y * SkBitmap.RowBytes + x * 4;
            ptr[0] = color.B;
            ptr[1] = color.G;
            ptr[2] = color.R;
            ptr[3] = color.A;
        }

        public object Clone() => new Bitmap(this);

        public Bitmap Clone(Rectangle rect, PixelFormat format)
        {
            var dst = new Bitmap(rect.Width, rect.Height, format);
            using var g = Graphics.FromImage(dst);
            g.DrawImage(this, new Rectangle(0, 0, rect.Width, rect.Height), rect, GraphicsUnit.Pixel);
            return dst;
        }

        public unsafe BitmapData LockBits(Rectangle rect, ImageLockMode flags, PixelFormat format)
        {
            if (rect.X < 0 || rect.Y < 0 || rect.Right > Width || rect.Bottom > Height || rect.Width <= 0 || rect.Height <= 0)
                throw new ArgumentOutOfRangeException(nameof(rect));

            if (format == PixelFormat.Format32bppArgb || format == PixelFormat.Format32bppRgb || format == PixelFormat.DontCare)
            {
                IntPtr basePtr = SkBitmap.GetPixels();
                IntPtr scan0 = basePtr + rect.Y * SkBitmap.RowBytes + rect.X * 4;
                return new BitmapData
                {
                    Width = rect.Width,
                    Height = rect.Height,
                    Stride = SkBitmap.RowBytes,
                    PixelFormat = format == PixelFormat.DontCare ? PixelFormat.Format32bppArgb : format,
                    Scan0 = scan0,
                    LockedRect = rect,
                    LockMode = flags,
                    OwnsBuffer = false
                };
            }

            if (format == PixelFormat.Format24bppRgb)
            {
                int stride = (rect.Width * 3 + 3) & ~3;
                IntPtr buffer = Marshal.AllocHGlobal(stride * rect.Height);
                if (flags is ImageLockMode.ReadOnly or ImageLockMode.ReadWrite)
                {
                    byte* srcBase = (byte*)SkBitmap.GetPixels().ToPointer();
                    int srcStride = SkBitmap.RowBytes;
                    byte* dstBase = (byte*)buffer.ToPointer();
                    for (int y = 0; y < rect.Height; y++)
                    {
                        byte* srcRow = srcBase + (rect.Y + y) * srcStride + rect.X * 4;
                        byte* dstRow = dstBase + y * stride;
                        for (int x = 0; x < rect.Width; x++)
                        {
                            dstRow[x * 3 + 0] = srcRow[x * 4 + 0];
                            dstRow[x * 3 + 1] = srcRow[x * 4 + 1];
                            dstRow[x * 3 + 2] = srcRow[x * 4 + 2];
                        }
                    }
                }
                return new BitmapData
                {
                    Width = rect.Width,
                    Height = rect.Height,
                    Stride = stride,
                    PixelFormat = PixelFormat.Format24bppRgb,
                    Scan0 = buffer,
                    LockedRect = rect,
                    LockMode = flags,
                    OwnsBuffer = true
                };
            }

            throw new NotSupportedException($"Unsupported PixelFormat: {format}");
        }

        public unsafe void UnlockBits(BitmapData bitmapdata)
        {
            if (!bitmapdata.OwnsBuffer) return;
            try
            {
                if (bitmapdata.LockMode is ImageLockMode.WriteOnly or ImageLockMode.ReadWrite
                    && bitmapdata.PixelFormat == PixelFormat.Format24bppRgb)
                {
                    var rect = bitmapdata.LockedRect;
                    byte* dstBase = (byte*)SkBitmap.GetPixels().ToPointer();
                    int dstStride = SkBitmap.RowBytes;
                    byte* srcBase = (byte*)bitmapdata.Scan0.ToPointer();
                    int srcStride = bitmapdata.Stride;
                    for (int y = 0; y < rect.Height; y++)
                    {
                        byte* srcRow = srcBase + y * srcStride;
                        byte* dstRow = dstBase + (rect.Y + y) * dstStride + rect.X * 4;
                        for (int x = 0; x < rect.Width; x++)
                        {
                            dstRow[x * 4 + 0] = srcRow[x * 3 + 0];
                            dstRow[x * 4 + 1] = srcRow[x * 3 + 1];
                            dstRow[x * 4 + 2] = srcRow[x * 3 + 2];
                            dstRow[x * 4 + 3] = 255;
                        }
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(bitmapdata.Scan0);
                bitmapdata.Scan0 = IntPtr.Zero;
                bitmapdata.OwnsBuffer = false;
            }
        }
    }

    public sealed class Graphics : IDisposable
    {
        private readonly SKCanvas _canvas;
        private readonly Bitmap _target;

        public InterpolationMode InterpolationMode { get; set; } = InterpolationMode.Bilinear;
        public SmoothingMode SmoothingMode { get; set; } = SmoothingMode.Default;
        public PixelOffsetMode PixelOffsetMode { get; set; } = PixelOffsetMode.Default;
        public CompositingQuality CompositingQuality { get; set; } = CompositingQuality.Default;

        private Graphics(Bitmap target)
        {
            _target = target;
            _canvas = new SKCanvas(target.SkBitmap);
        }

        public static Graphics FromImage(Image image)
        {
            if (image is not Bitmap bmp) throw new ArgumentException("Image must be a Bitmap.", nameof(image));
            return new Graphics(bmp);
        }

        private SKSamplingOptions Sampling => InterpolationMode switch
        {
            InterpolationMode.NearestNeighbor => new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None),
            InterpolationMode.Bicubic or InterpolationMode.HighQualityBicubic => new SKSamplingOptions(SKCubicResampler.CatmullRom),
            _ => new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)
        };

        private bool IsAntiAlias => SmoothingMode is SmoothingMode.AntiAlias or SmoothingMode.HighQuality;

        private static SKColor ToSk(Color c) => new(c.R, c.G, c.B, c.A);

        public void Clear(Color color) => _canvas.Clear(ToSk(color));

        public void TranslateTransform(float dx, float dy) => _canvas.Translate(dx, dy);
        public void ScaleTransform(float sx, float sy) => _canvas.Scale(sx, sy);
        public void RotateTransform(float angle) => _canvas.RotateDegrees(angle);
        public void SetClip(Rectangle rect) => _canvas.ClipRect(new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom));
        public void SetClip(RectangleF rect) => _canvas.ClipRect(new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom));

        private static SKPaint? BuildImagePaint(ImageAttributes? attrs)
        {
            if (attrs?.ColorMatrix is not { Matrix: var m } || m.Length < 5) return null;
            // Transpose GDI+ row-vector 5x5 matrix into Skia column-vector 4x5 matrix (normalized 0..1 translation)
            float[] sk = new float[20];
            for (int dstRow = 0; dstRow < 4; dstRow++)
            {
                sk[dstRow * 5 + 0] = m[0][dstRow];
                sk[dstRow * 5 + 1] = m[1][dstRow];
                sk[dstRow * 5 + 2] = m[2][dstRow];
                sk[dstRow * 5 + 3] = m[3][dstRow];
                sk[dstRow * 5 + 4] = m[4][dstRow];
            }
            return new SKPaint { ColorFilter = SKColorFilter.CreateColorMatrix(sk) };
        }

        public void DrawImage(Image image, RectangleF destRect, RectangleF srcRect, GraphicsUnit srcUnit)
        {
            using var skImage = SKImage.FromBitmap(image.SkBitmap);
            var s = new SKRect(srcRect.Left, srcRect.Top, srcRect.Right, srcRect.Bottom);
            var d = new SKRect(destRect.Left, destRect.Top, destRect.Right, destRect.Bottom);
            _canvas.DrawImage(skImage, s, d, Sampling);
        }

        public void DrawImage(Image image, Rectangle destRect, Rectangle srcRect, GraphicsUnit srcUnit)
            => DrawImage(image, (RectangleF)destRect, (RectangleF)srcRect, srcUnit);

        public void DrawImage(Image image, Rectangle destRect, RectangleF srcRect, GraphicsUnit srcUnit)
            => DrawImage(image, (RectangleF)destRect, srcRect, srcUnit);

        public void DrawImage(Image image, Rectangle destRect, float srcX, float srcY, float srcWidth, float srcHeight, GraphicsUnit srcUnit, ImageAttributes? imageAttr = null)
        {
            using var skImage = SKImage.FromBitmap(image.SkBitmap);
            var s = new SKRect(srcX, srcY, srcX + srcWidth, srcY + srcHeight);
            var d = new SKRect(destRect.Left, destRect.Top, destRect.Right, destRect.Bottom);
            using var paint = BuildImagePaint(imageAttr);
            _canvas.DrawImage(skImage, s, d, Sampling, paint);
        }

        public void DrawImage(Image image, Rectangle destRect, int srcX, int srcY, int srcWidth, int srcHeight, GraphicsUnit srcUnit, ImageAttributes? imageAttr = null)
            => DrawImage(image, destRect, (float)srcX, (float)srcY, (float)srcWidth, (float)srcHeight, srcUnit, imageAttr);

        public void DrawImage(Image image, Rectangle destRect)
            => DrawImage(image, destRect, new Rectangle(0, 0, image.Width, image.Height), GraphicsUnit.Pixel);

        public void DrawImage(Image image, int x, int y, int width, int height)
            => DrawImage(image, new Rectangle(x, y, width, height), new Rectangle(0, 0, image.Width, image.Height), GraphicsUnit.Pixel);

        public void DrawImage(Image image, int x, int y)
            => DrawImage(image, new Rectangle(x, y, image.Width, image.Height), new Rectangle(0, 0, image.Width, image.Height), GraphicsUnit.Pixel);

        public SizeF MeasureString(string text, Font font)
        {
            using var skFont = new SKFont(font.Typeface, font.PixelSize);
            float width = skFont.MeasureText(text);
            var metrics = skFont.Metrics;
            float height = Math.Max(font.PixelSize, metrics.Descent - metrics.Ascent);
            return new SizeF(width + 4f, height + 2f);
        }

        public void DrawString(string s, Font font, Brush brush, float x, float y)
        {
            using var skFont = new SKFont(font.Typeface, font.PixelSize) { Subpixel = true };
            using var paint = new SKPaint { Color = ToSk(brush.ColorValue), IsAntialias = true };
            var metrics = skFont.Metrics;
            float baselineY = y - metrics.Ascent;
            _canvas.DrawText(s, x, baselineY, skFont, paint);
        }

        public void DrawString(string s, Font font, Brush brush, PointF point)
            => DrawString(s, font, brush, point.X, point.Y);

        public void FillRectangle(Brush brush, float x, float y, float width, float height)
        {
            using var paint = new SKPaint { Color = ToSk(brush.ColorValue), Style = SKPaintStyle.Fill, IsAntialias = IsAntiAlias };
            _canvas.DrawRect(x, y, width, height, paint);
        }

        public void FillRectangle(Brush brush, Rectangle rect)
            => FillRectangle(brush, rect.X, rect.Y, rect.Width, rect.Height);

        public void DrawRectangle(Pen pen, float x, float y, float width, float height)
        {
            using var paint = new SKPaint { Color = ToSk(pen.Color), StrokeWidth = pen.Width, Style = SKPaintStyle.Stroke, IsAntialias = IsAntiAlias };
            _canvas.DrawRect(x, y, width, height, paint);
        }

        public void DrawRectangle(Pen pen, Rectangle rect)
            => DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);

        public void DrawEllipse(Pen pen, float x, float y, float width, float height)
        {
            using var paint = new SKPaint { Color = ToSk(pen.Color), StrokeWidth = pen.Width, Style = SKPaintStyle.Stroke, IsAntialias = IsAntiAlias };
            _canvas.DrawOval(new SKRect(x, y, x + width, y + height), paint);
        }

        public void DrawLine(Pen pen, PointF pt1, PointF pt2)
        {
            using var paint = new SKPaint { Color = ToSk(pen.Color), StrokeWidth = pen.Width, Style = SKPaintStyle.Stroke, IsAntialias = IsAntiAlias };
            _canvas.DrawLine(pt1.X, pt1.Y, pt2.X, pt2.Y, paint);
            if (pen.CustomEndCap is { } cap)
            {
                float dx = pt2.X - pt1.X;
                float dy = pt2.Y - pt1.Y;
                float len = MathF.Sqrt(dx * dx + dy * dy);
                if (len > 1e-3f)
                {
                    float ux = dx / len, uy = dy / len;
                    float headLen = cap.Height * pen.Width * 0.7f;
                    float headHalfW = cap.Width * pen.Width * 0.45f;
                    var tip = new SKPoint(pt2.X, pt2.Y);
                    var left = new SKPoint(pt2.X - ux * headLen - uy * headHalfW, pt2.Y - uy * headLen + ux * headHalfW);
                    var right = new SKPoint(pt2.X - ux * headLen + uy * headHalfW, pt2.Y - uy * headLen - ux * headHalfW);
                    using var path = new SKPath();
                    path.MoveTo(tip);
                    path.LineTo(left);
                    path.LineTo(right);
                    path.Close();
                    using var fill = new SKPaint { Color = ToSk(pen.Color), Style = SKPaintStyle.Fill, IsAntialias = IsAntiAlias };
                    _canvas.DrawPath(path, fill);
                }
            }
        }

        public void CopyFromScreen(int sourceX, int sourceY, int destinationX, int destinationY, Size blockRegionSize)
        {
            // Fallback hook used only if legacy BitBlt path is ever invoked on Linux.
            var captured = ASPAssistant.Core.Interop.LinuxPlatform.CaptureScreenRegion(sourceX, sourceY, blockRegionSize.Width, blockRegionSize.Height);
            if (captured is null) throw new InvalidOperationException("Wayland screen capture failed.");
            using (captured)
                DrawImage(captured, new Rectangle(destinationX, destinationY, blockRegionSize.Width, blockRegionSize.Height),
                    new Rectangle(0, 0, captured.Width, captured.Height), GraphicsUnit.Pixel);
        }

        public void Dispose() => _canvas.Dispose();
    }
}
#endif
