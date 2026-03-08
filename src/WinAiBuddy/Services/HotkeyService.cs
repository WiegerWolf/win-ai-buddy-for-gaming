using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using WinAiBuddy.Infrastructure;

namespace WinAiBuddy.Services;

public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x4781;
    private readonly Window _window;
    private HwndSource? _source;
    private string _hotkeyText = "Ctrl+Shift+Space";
    private bool _isRegistered;

    public HotkeyService(Window window)
    {
        _window = window;
        _window.SourceInitialized += WindowOnSourceInitialized;
        _window.Closed += WindowOnClosed;
    }

    public event EventHandler? HotkeyPressed;

    public void UpdateHotkey(string hotkeyText)
    {
        _hotkeyText = hotkeyText;
        RegisterHotkeyIfPossible();
    }

    public void Dispose()
    {
        Unregister();

        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
        }

        _window.SourceInitialized -= WindowOnSourceInitialized;
        _window.Closed -= WindowOnClosed;
    }

    private void WindowOnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(_window).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);
        RegisterHotkeyIfPossible();
    }

    private void WindowOnClosed(object? sender, EventArgs e)
    {
        Unregister();
    }

    private void RegisterHotkeyIfPossible()
    {
        if (_source is null)
        {
            return;
        }

        Unregister();

        if (!TryParseHotkey(_hotkeyText, out var modifiers, out var virtualKey))
        {
            return;
        }

        _isRegistered = NativeMethods.RegisterHotKey(_source.Handle, HotkeyId, modifiers, virtualKey);
    }

    private void Unregister()
    {
        if (_source is null || !_isRegistered)
        {
            return;
        }

        NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
        _isRegistered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmHotKey && wParam.ToInt32() == HotkeyId)
        {
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static bool TryParseHotkey(string? text, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        for (var index = 0; index < parts.Length - 1; index++)
        {
            switch (parts[index].ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= NativeMethods.ModControl;
                    break;
                case "shift":
                    modifiers |= NativeMethods.ModShift;
                    break;
                case "alt":
                    modifiers |= NativeMethods.ModAlt;
                    break;
                case "win":
                case "windows":
                    modifiers |= NativeMethods.ModWin;
                    break;
            }
        }

        var converter = new KeyConverter();
        var converted = converter.ConvertFromString(parts[^1]);
        if (converted is not Key key)
        {
            return false;
        }

        virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        return virtualKey != 0;
    }
}
