using System.Windows;
using Screen = System.Windows.Forms.Screen;

namespace WinAiBuddy;

public partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();
    }

    public void SetMessage(string message)
    {
        MessageTextBlock.Text = message;
        PositionBottomCenter();
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
}
