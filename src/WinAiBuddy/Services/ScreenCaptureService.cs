using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Screen = System.Windows.Forms.Screen;
using WinAiBuddy.Models;

namespace WinAiBuddy.Services;

public sealed class ScreenCaptureService
{
    public ScreenshotCapture CapturePrimaryMonitorJpeg(int jpegQuality)
    {
        var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);

        using var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
        }

        using var stream = new MemoryStream();
        var encoder = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(jpegQuality, 40, 100));
        bitmap.Save(stream, encoder, parameters);

        return new ScreenshotCapture(stream.ToArray(), "image/jpeg", bounds.Width, bounds.Height);
    }
}
