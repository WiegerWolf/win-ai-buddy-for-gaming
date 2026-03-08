namespace WinAiBuddy.Models;

public sealed record ScreenshotCapture(byte[] Bytes, string MimeType, int Width, int Height);

public sealed record AudioChunk(byte[] Bytes, string MimeType);
