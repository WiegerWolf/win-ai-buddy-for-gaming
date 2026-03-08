using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WinAiBuddy.Models;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfCursor = System.Windows.Input.Cursor;
using Screen = System.Windows.Forms.Screen;

namespace WinAiBuddy;

public partial class OverlayWindow : Window
{
    private bool _hasCustomPosition;
    private bool _isEditing;

    public event Action<double, double>? PositionChanged;

    public OverlayWindow()
    {
        InitializeComponent();
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
        OverlayBorder.BorderThickness = bgOpacityByte == 0 ? new Thickness(0) : new Thickness(1);

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
}
