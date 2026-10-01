using System;
using System.Collections.Generic;
using Android.Graphics;
using Android.Views;
using Broiler.Graphics;
using Broiler.Graphics.Color;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.Imaging;
using Broiler.Graphics.RenderList;
using Broiler.Graphics.Rendering;
using Broiler.Graphics.Resources;
using Broiler.Graphics.Text;

namespace Broiler.Hosting.Android;

/// <summary>
/// Replays Broiler display lists through Android's hardware-accelerated Canvas.
/// </summary>
/// <remarks>
/// The portable image renderer remains the resource owner and off-screen fallback. On-screen frames
/// use the platform compositor directly, avoiding a full managed CPU raster and texture upload for
/// every input event.
/// </remarks>
public sealed class AndroidCanvasRenderer : IBroilerRenderer
{
    private readonly BImageRenderer _fallback = new();
    private readonly Dictionary<ulong, BBitmap> _sourceImages = [];
    private readonly Dictionary<ulong, Bitmap> _nativeImages = [];
    private readonly Dictionary<FontKey, Typeface> _typefaces = [];
    private readonly Paint _paint = new(PaintFlags.AntiAlias | PaintFlags.Dither | PaintFlags.SubpixelText);
    private bool _disposed;

    public IBroilerSurface CreateSurface(BSurfaceDescriptor descriptor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _fallback.CreateSurface(descriptor);
    }

    public BImageHandle CreateImage(ReadOnlySpan<byte> encodedImage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using BBitmap decoded = BBitmap.Decode(encodedImage);
        return CreateImage(decoded.ToPixelBuffer());
    }

    public BImageHandle CreateImage(BPixelBuffer pixels)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(pixels);
        BImageHandle image = _fallback.CreateImage(pixels);
        _sourceImages[image.Handle.Id] = new BBitmap(
            pixels.Width,
            pixels.Height,
            (byte[])pixels.Rgba.Clone(),
            takeOwnership: true);
        return image;
    }

    public void ReleaseImage(BImageHandle image)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_nativeImages.Remove(image.Handle.Id, out Bitmap? native))
            native.Dispose();
        if (_sourceImages.Remove(image.Handle.Id, out BBitmap? source))
            source.Dispose();
        _fallback.ReleaseImage(image);
    }

    public void Render(IBroilerSurface surface, BRenderList renderList, BFrameContext frameContext)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _fallback.Render(surface, renderList, frameContext);
    }

    public BBitmap RenderToImage(BRenderList renderList, BSurfaceDescriptor descriptor, BFrameContext frameContext)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _fallback.RenderToImage(renderList, descriptor, frameContext);
    }

    public bool Present(ISurfaceHolder holder, BRenderList renderList, double density, BColor clearColor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(renderList);
        renderList.Validate();

        Canvas? canvas = holder.LockHardwareCanvas();
        if (canvas is null)
            return false;

        try
        {
            canvas.DrawColor(ToAndroidColor(clearColor), PorterDuff.Mode.Src);
            int rootSave = canvas.Save();
            canvas.Scale((float)density, (float)density);
            try
            {
                foreach (BRenderCommand command in renderList.Commands)
                    Replay(canvas, command);
            }
            finally
            {
                canvas.RestoreToCount(rootSave);
            }
        }
        finally
        {
            holder.UnlockCanvasAndPost(canvas);
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        foreach (Bitmap bitmap in _nativeImages.Values)
            bitmap.Dispose();
        foreach (BBitmap bitmap in _sourceImages.Values)
            bitmap.Dispose();
        foreach (Typeface typeface in _typefaces.Values)
            typeface.Dispose();
        _nativeImages.Clear();
        _sourceImages.Clear();
        _typefaces.Clear();
        _paint.Dispose();
        _fallback.Dispose();
    }

    private void Replay(Canvas canvas, BRenderCommand command)
    {
        switch (command)
        {
            case BRenderCommand.FillRect fill:
                ConfigureFill(fill.Color);
                canvas.DrawRect(
                    (float)fill.Rect.Left,
                    (float)fill.Rect.Top,
                    (float)fill.Rect.Right,
                    (float)fill.Rect.Bottom,
                    _paint);
                break;
            case BRenderCommand.StrokeRect stroke:
                ConfigureStroke(stroke.Color, stroke.Thickness);
                canvas.DrawRect(
                    (float)stroke.Rect.Left,
                    (float)stroke.Rect.Top,
                    (float)stroke.Rect.Right,
                    (float)stroke.Rect.Bottom,
                    _paint);
                break;
            case BRenderCommand.FillRoundedRect fillRounded:
                ConfigureFill(fillRounded.Color);
                using (var rect = ToRectF(fillRounded.Rect))
                    canvas.DrawRoundRect(rect, (float)fillRounded.RadiusX, (float)fillRounded.RadiusY, _paint);
                break;
            case BRenderCommand.StrokeRoundedRect strokeRounded:
                ConfigureStroke(strokeRounded.Color, strokeRounded.Thickness);
                using (var rect = ToRectF(strokeRounded.Rect))
                    canvas.DrawRoundRect(rect, (float)strokeRounded.RadiusX, (float)strokeRounded.RadiusY, _paint);
                break;
            case BRenderCommand.FillTriangle triangle:
                FillTriangle(canvas, triangle);
                break;
            case BRenderCommand.DrawText text:
                DrawText(canvas, text);
                break;
            case BRenderCommand.DrawImage image:
                DrawImage(canvas, image);
                break;
            case BRenderCommand.PushClip clip:
                canvas.Save();
                canvas.ClipRect(
                    (float)clip.Rect.Left,
                    (float)clip.Rect.Top,
                    (float)clip.Rect.Right,
                    (float)clip.Rect.Bottom);
                break;
            case BRenderCommand.PopClip:
                canvas.Restore();
                break;
            case BRenderCommand.PushTransform transform:
                canvas.Save();
                using (var matrix = ToAndroidMatrix(transform.Transform))
                    canvas.Concat(matrix);
                break;
            case BRenderCommand.PopTransform:
                canvas.Restore();
                break;

            default:
                throw new NotSupportedException($"Unknown render command: {command.GetType().Name}");
        }
    }

    private void FillTriangle(Canvas canvas, BRenderCommand.FillTriangle command)
    {
        ConfigureFill(command.Color);
        using var path = new global::Android.Graphics.Path();
        path.MoveTo((float)command.A.X, (float)command.A.Y);
        path.LineTo((float)command.B.X, (float)command.B.Y);
        path.LineTo((float)command.C.X, (float)command.C.Y);
        path.Close();
        canvas.DrawPath(path, _paint);
    }

    private void DrawText(Canvas canvas, BRenderCommand.DrawText command)
    {
        BTextRun run = command.Text;
        if (string.IsNullOrEmpty(run.Text) || run.Color.A == 0 || run.Font.Size <= 0)
            return;

        ConfigureFill(run.Color);
        _paint.TextSize = (float)run.Font.Size;
        _paint.SetTypeface(ResolveTypeface(run.Font));
        _paint.TextSkewX = run.Font.Slant is BFontSlant.Italic or BFontSlant.Oblique ? -0.25f : 0f;
        float baseline = (float)(command.Origin.Y + (run.Font.Size * 0.8));
        canvas.DrawText(run.Text, (float)command.Origin.X, baseline, _paint);
    }

    private void DrawImage(Canvas canvas, BRenderCommand.DrawImage command)
    {
        if (!command.Image.IsValid || command.Opacity <= 0)
            return;

        Bitmap? bitmap = ResolveImage(command.Image);
        if (bitmap is null)
            return;

        BRect source = command.Source.IsEmpty
            ? new BRect(0, 0, bitmap.Width, bitmap.Height)
            : command.Source;
        using var sourceRect = new Rect(
            (int)Math.Floor(source.Left),
            (int)Math.Floor(source.Top),
            (int)Math.Ceiling(source.Right),
            (int)Math.Ceiling(source.Bottom));
        ConfigureFill(BColor.White);
        _paint.Alpha = (int)Math.Round(Math.Clamp(command.Opacity, 0, 1) * 255);
        using (var destinationRect = ToRectF(command.Destination))
            canvas.DrawBitmap(bitmap, sourceRect, destinationRect, _paint);
        _paint.Alpha = 255;
    }

    private Bitmap? ResolveImage(BImageHandle image)
    {
        if (_nativeImages.TryGetValue(image.Handle.Id, out Bitmap? native))
            return native;
        if (!_sourceImages.TryGetValue(image.Handle.Id, out BBitmap? sourceImage))
            return null;

        byte[] rgba = sourceImage.CopyRgba();
        var argb = new int[sourceImage.Width * sourceImage.Height];
        for (int source = 0, target = 0; source < rgba.Length; source += 4, target++)
        {
            argb[target] = unchecked((int)(
                ((uint)rgba[source + 3] << 24) |
                ((uint)rgba[source] << 16) |
                ((uint)rgba[source + 1] << 8) |
                rgba[source + 2]));
        }

        native = Bitmap.CreateBitmap(sourceImage.Width, sourceImage.Height, Bitmap.Config.Argb8888);
        native.SetPixels(argb, 0, sourceImage.Width, 0, 0, sourceImage.Width, sourceImage.Height);
        _nativeImages[image.Handle.Id] = native;
        return native;
    }

    private Typeface ResolveTypeface(BFontStyle font)
    {
        var key = new FontKey(font.FamilyName, font.Weight, font.Slant);
        if (_typefaces.TryGetValue(key, out Typeface? typeface))
            return typeface;

        bool bold = font.Weight >= BFontWeight.Bold;
        bool italic = font.Slant is BFontSlant.Italic or BFontSlant.Oblique;
        TypefaceStyle style = (bold, italic) switch
        {
            (true, true) => TypefaceStyle.BoldItalic,
            (true, false) => TypefaceStyle.Bold,
            (false, true) => TypefaceStyle.Italic,
            _ => TypefaceStyle.Normal,
        };
        typeface = Typeface.Create(font.FamilyName, style) ?? Typeface.Default;
        _typefaces[key] = typeface;
        return typeface;
    }

    private void ConfigureFill(BColor color)
    {
        _paint.SetStyle(Paint.Style.Fill);
        _paint.Color = ToAndroidColor(color);
        _paint.Alpha = color.A;
        _paint.StrokeWidth = 1f;
    }

    private void ConfigureStroke(BColor color, double width)
    {
        _paint.SetStyle(Paint.Style.Stroke);
        _paint.Color = ToAndroidColor(color);
        _paint.Alpha = color.A;
        _paint.StrokeWidth = (float)Math.Max(0, width);
    }

    private static RectF ToRectF(BRect rect) =>
        new((float)rect.Left, (float)rect.Top, (float)rect.Right, (float)rect.Bottom);

    private static Color ToAndroidColor(BColor color) =>
        Color.Argb(color.A, color.R, color.G, color.B);

    private static Matrix ToAndroidMatrix(BMatrix3x2 matrix)
    {
        var native = new Matrix();
        native.SetValues([
            (float)matrix.M11, (float)matrix.M21, (float)matrix.M31,
            (float)matrix.M12, (float)matrix.M22, (float)matrix.M32,
            0f, 0f, 1f,
        ]);
        return native;
    }

    private readonly record struct FontKey(string Family, BFontWeight Weight, BFontSlant Slant);
}
