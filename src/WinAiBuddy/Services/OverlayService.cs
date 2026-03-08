using WinAiBuddy.Models;

namespace WinAiBuddy.Services;

public sealed class OverlayService
{
    private readonly OverlayWindow _window;
    private readonly SettingsService _settingsService;
    private CancellationTokenSource? _hideCts;

    public OverlayService(OverlayWindow window, SettingsService settingsService)
    {
        _window = window;
        _settingsService = settingsService;
        _window.PositionChanged += OverlayWindow_OnPositionChanged;
        ApplySettings(_settingsService.Current);
    }

    public async Task ShowMessageAsync(string text, TimeSpan duration)
    {
        await _window.Dispatcher.InvokeAsync(async () =>
        {
            _window.ApplySettings(_settingsService.Current);
            _window.SetMessage(text);
            if (!_window.IsVisible)
            {
                _window.Opacity = 0.0;
                _window.Show();
                await _window.FadeInAsync(1.0);
            }
        });

        if (_window.IsEditing)
        {
            return;
        }

        _hideCts?.Cancel();
        _hideCts = new CancellationTokenSource();
        _ = HideLaterAsync(duration, _hideCts.Token);
    }

    public void ApplySettings(AppSettings settings)
    {
        if (_window.Dispatcher.CheckAccess())
        {
            _window.ApplySettings(settings);
            return;
        }

        _window.Dispatcher.Invoke(() => _window.ApplySettings(settings));
    }

    public async Task BeginEditingAsync(string sampleText)
    {
        _hideCts?.Cancel();

        await _window.Dispatcher.InvokeAsync(async () =>
        {
            _window.ApplySettings(_settingsService.Current);
            _window.SetMessage(sampleText);
            _window.SetEditMode(true);

            if (!_window.IsVisible)
            {
                _window.Opacity = 0.0;
                _window.Show();
                await _window.FadeInAsync(1.0);
            }
        });
    }

    public async Task EndEditingAsync(bool hideOverlay = true)
    {
        _hideCts?.Cancel();

        await _window.Dispatcher.InvokeAsync(async () =>
        {
            _window.SetEditMode(false);

            if (hideOverlay && _window.IsVisible)
            {
                await _window.FadeOutAsync();
                _window.Hide();
            }
        });
    }

    public async Task HideAsync()
    {
        _hideCts?.Cancel();
        await _window.Dispatcher.InvokeAsync(async () =>
        {
            _window.SetEditMode(false);
            if (_window.IsVisible)
            {
                await _window.FadeOutAsync();
                _window.Hide();
            }
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

    private void OverlayWindow_OnPositionChanged(double left, double top)
    {
        _settingsService.Current.OverlayLeft = left;
        _settingsService.Current.OverlayTop = top;
        _settingsService.Save(_settingsService.Current);
    }
}
