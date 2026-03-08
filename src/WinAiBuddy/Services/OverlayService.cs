namespace WinAiBuddy.Services;

public sealed class OverlayService
{
    private readonly OverlayWindow _window;
    private CancellationTokenSource? _hideCts;

    public OverlayService(OverlayWindow window)
    {
        _window = window;
    }

    public async Task ShowMessageAsync(string text, TimeSpan duration)
    {
        await _window.Dispatcher.InvokeAsync(() =>
        {
            _window.SetMessage(text);
            if (!_window.IsVisible)
            {
                _window.Show();
            }
        });

        _hideCts?.Cancel();
        _hideCts = new CancellationTokenSource();
        _ = HideLaterAsync(duration, _hideCts.Token);
    }

    public async Task HideAsync()
    {
        _hideCts?.Cancel();
        await _window.Dispatcher.InvokeAsync(() =>
        {
            _window.Hide();
        });
    }

    private async Task HideLaterAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            await HideAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }
}
