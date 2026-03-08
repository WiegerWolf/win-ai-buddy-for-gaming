namespace WinAiBuddy.Models;

public sealed record AudioInputDeviceOption(
    int DeviceNumber,
    string DeviceName,
    string DisplayName);
