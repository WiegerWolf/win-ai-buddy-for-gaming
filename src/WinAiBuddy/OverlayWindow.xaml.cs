using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using WinAiBuddy.Models;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfCursor = System.Windows.Input.Cursor;
using Screen = System.Windows.Forms.Screen;

namespace WinAiBuddy;

public partial class OverlayWindow : Window
{
    private static readonly IntPtr HwndTopmost = new(-1);
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    private bool _hasCustomPosition;
    private bool _isEditing;

    public event Action<double, double>? PositionChanged;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OverlayWindow_OnSourceInitialized;
    }

    public bool IsEditing => _isEditing;

    public void SetMessage(string message)
    {
        MessageTextBlock.Text = message;
        if (!_hasCustomPosition)
        {
            PositionBottomCenter();
        }
    }

    public void ApplySettings(AppSettings settings)
    {
        RootGrid.Opacity = Math.Clamp(settings.OverlayOpacity, 0.0, 1.0);
        
        var baseBgColor = ParseColor(settings.OverlayBackgroundColor, WpfColor.FromArgb(255, 17, 17, 17));
        var bgOpacityByte = (byte)Math.Clamp(settings.OverlayBackgroundOpacity * 255.0, 0, 255);
        
        var bgBrush = new SolidColorBrush(WpfColor.FromArgb(bgOpacityByte, baseBgColor.R, baseBgColor.G, baseBgColor.B));
        bgBrush.Freeze();
        OverlayBorder.Background = bgBrush;

        MessageTextBlock.Foreground = ParseBrush(settings.OverlayTextColor, WpfBrushes.White);
        MessageTextBlock.FontSize = settings.OverlayFontSize;
        MessageTextBlock.Stroke = ParseBrush(settings.OverlayOutlineColor, WpfBrushes.Black);
        MessageTextBlock.StrokeThickness = Math.Clamp(settings.OverlayOutlineThickness, 0, 8);

        if (settings.OverlayLeft.HasValue && settings.OverlayTop.HasValue)
        {
            _hasCustomPosition = true;
            Left = settings.OverlayLeft.Value;
            Top = settings.OverlayTop.Value;
            return;
        }

        _hasCustomPosition = false;
        PositionBottomCenter();
    }

    public void SetEditMode(bool isEditing)
    {
        _isEditing = isEditing;
        RootGrid.IsHitTestVisible = isEditing;
        EditHintBorder.Visibility = isEditing ? Visibility.Visible : Visibility.Collapsed;
        OverlayBorder.Cursor = isEditing ? System.Windows.Input.Cursors.SizeAll : System.Windows.Input.Cursors.Arrow;
        UpdateWindowInteractionStyle();
        EnsureTopmost();
    }

    public void EnsureTopmost()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(
            handle,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    public Task FadeInAsync(double targetOpacity)
    {
        var tcs = new TaskCompletionSource();
        var animation = new DoubleAnimation
        {
            From = Opacity,
            To = targetOpacity,
            Duration = TimeSpan.FromMilliseconds(150),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        animation.Completed += (s, e) =>
        {
            Opacity = targetOpacity;
            BeginAnimation(OpacityProperty, null);
            tcs.TrySetResult();
        };

        BeginAnimation(OpacityProperty, animation);
        return tcs.Task;
    }

    public Task FadeOutAsync()
    {
        var tcs = new TaskCompletionSource();
        var animation = new DoubleAnimation
        {
            From = Opacity,
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(400),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };

        animation.Completed += (s, e) =>
        {
            Opacity = 0.0;
            BeginAnimation(OpacityProperty, null);
            tcs.TrySetResult();
        };

        BeginAnimation(OpacityProperty, animation);
        return tcs.Task;
    }

    private void PositionBottomCenter()
    {
        var area = Screen.PrimaryScreen?.WorkingArea;
        if (area is null)
        {
            return;
        }

        Left = area.Value.Left + Math.Max(0, (area.Value.Width - Width) / 2);
        Top = area.Value.Bottom - Height - 56;
    }

    private void OverlayBorder_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isEditing)
        {
            return;
        }

        try
        {
            DragMove();
            _hasCustomPosition = true;
            PositionChanged?.Invoke(Left, Top);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static WpfBrush ParseBrush(string? value, WpfBrush fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        try
        {
            var converter = new BrushConverter();
            var converted = converter.ConvertFromString(value);
            if (converted is WpfBrush brush)
            {
                if (brush.CanFreeze)
                {
                    brush.Freeze();
                }

                return brush;
            }
        }
        catch (FormatException)
        {
        }

        return fallback;
    }

    private static WpfColor ParseColor(string? value, WpfColor fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        try
        {
            var converter = new BrushConverter();
            var converted = converter.ConvertFromString(value);
            if (converted is SolidColorBrush solidBrush)
            {
                return solidBrush.Color;
            }
        }
        catch (FormatException)
        {
        }

        return fallback;
    }

    private void OverlayWindow_OnSourceInitialized(object? sender, EventArgs e)
    {
        UpdateWindowInteractionStyle();
        EnsureTopmost();
    }

    private void UpdateWindowInteractionStyle()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var exStyle = GetWindowLong(handle, GwlExStyle);
        exStyle |= WsExToolWindow | WsExNoActivate;

        if (!_isEditing)
        {
            exStyle |= WsExTransparent;
        }
        else
        {
            exStyle &= ~WsExTransparent;
        }

        SetWindowLong(handle, GwlExStyle, exStyle);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);
}
