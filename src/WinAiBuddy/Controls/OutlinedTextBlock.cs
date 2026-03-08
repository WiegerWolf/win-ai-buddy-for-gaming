using System.Globalization;
using System.Windows;
using System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfControl = System.Windows.Controls.Control;
using WpfPen = System.Windows.Media.Pen;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace WinAiBuddy.Controls;

public sealed class OutlinedTextBlock : WpfControl
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty =
        DependencyProperty.Register(
            nameof(Stroke),
            typeof(WpfBrush),
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(
                WpfBrushes.Black,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(
            nameof(StrokeThickness),
            typeof(double),
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(
                2.0,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public WpfBrush Stroke
    {
        get => (WpfBrush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override WpfSize MeasureOverride(WpfSize constraint)
    {
        var outlineThickness = Math.Max(0, StrokeThickness);
        var maxTextWidth = GetAvailableTextWidth(constraint.Width, outlineThickness);
        var formattedText = CreateFormattedText(maxTextWidth);
        var measuredWidth = formattedText.WidthIncludingTrailingWhitespace + (outlineThickness * 2);
        var measuredHeight = formattedText.Height + (outlineThickness * 2);

        if (!double.IsInfinity(constraint.Width))
        {
            measuredWidth = Math.Min(measuredWidth, constraint.Width);
        }

        if (!double.IsInfinity(constraint.Height))
        {
            measuredHeight = Math.Min(measuredHeight, constraint.Height);
        }

        return new WpfSize(measuredWidth, measuredHeight);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        if (string.IsNullOrWhiteSpace(Text))
        {
            return;
        }

        var outlineThickness = Math.Max(0, StrokeThickness);
        var maxTextWidth = GetAvailableTextWidth(ActualWidth, outlineThickness);
        var formattedText = CreateFormattedText(maxTextWidth);
        var geometry = formattedText.BuildGeometry(new WpfPoint(outlineThickness, outlineThickness));
        var pen = new WpfPen(Stroke, outlineThickness)
        {
            LineJoin = PenLineJoin.Round
        };

        drawingContext.DrawGeometry(Foreground, pen, geometry);
    }

    private FormattedText CreateFormattedText(double maxTextWidth)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var formattedText = new FormattedText(
            Text ?? string.Empty,
            CultureInfo.CurrentUICulture,
            FlowDirection,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretch),
            FontSize,
            Foreground,
            dpi.PixelsPerDip);

        if (!double.IsInfinity(maxTextWidth) && maxTextWidth > 0)
        {
            formattedText.MaxTextWidth = maxTextWidth;
        }

        return formattedText;
    }

    private static double GetAvailableTextWidth(double totalWidth, double outlineThickness)
    {
        if (double.IsInfinity(totalWidth))
        {
            return double.PositiveInfinity;
        }

        return Math.Max(0, totalWidth - (outlineThickness * 2));
    }
}
