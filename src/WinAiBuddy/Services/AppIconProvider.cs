using System.Drawing;
using System.IO;
using System.Windows.Media.Imaging;

namespace WinAiBuddy.Services;

public static class AppIconProvider
{
    private static string IconDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon");

    public static Icon LoadTrayIcon()
    {
        var path = Path.Combine(IconDirectory, "app-icon.ico");
        return File.Exists(path) ? new Icon(path) : SystemIcons.Application;
    }

    public static BitmapFrame? LoadWindowIcon()
    {
        var path = Path.Combine(IconDirectory, "app-icon.png");
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = File.OpenRead(path);
        return BitmapFrame.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
    }
}

