using WinAiBuddy.Models;

namespace WinAiBuddy.Services;

public static class GeminiVoiceCatalog
{
    public static IReadOnlyList<GeminiVoiceOption> All { get; } =
    [
        new("Zephyr", "Bright, Higher pitch"),
        new("Puck", "Upbeat, Middle pitch"),
        new("Charon", "Informative, Lower pitch"),
        new("Kore", "Firm, Middle pitch"),
        new("Fenrir", "Excitable, Lower middle pitch"),
        new("Leda", "Youthful, Higher pitch"),
        new("Orus", "Firm, Lower middle pitch"),
        new("Aoede", "Breezy, Middle pitch"),
        new("Callirrhoe", "Easy-going, Middle pitch"),
        new("Autonoe", "Bright, Middle pitch"),
        new("Enceladus", "Breathy, Lower pitch"),
        new("Iapetus", "Clear, Lower middle pitch"),
        new("Umbriel", "Easy-going, Lower middle pitch"),
        new("Algieba", "Smooth, Lower pitch"),
        new("Despina", "Smooth, Middle pitch"),
        new("Erinome", "Clear, Middle pitch"),
        new("Algenib", "Gravelly, Lower pitch"),
        new("Rasalgethi", "Informative, Middle pitch"),
        new("Laomedeia", "Upbeat, Higher pitch"),
        new("Achernar", "Soft, Higher pitch"),
        new("Alnilam", "Firm, Lower middle pitch"),
        new("Schedar", "Even, Lower middle pitch"),
        new("Gacrux", "Mature, Middle pitch"),
        new("Pulcherrima", "Forward, Middle pitch"),
        new("Achird", "Friendly, Lower middle pitch"),
        new("Zubenelgenubi", "Casual, Lower middle pitch"),
        new("Vindemiatrix", "Gentle, Middle pitch"),
        new("Sadachbia", "Lively, Lower pitch"),
        new("Sadaltager", "Knowledgeable, Middle pitch"),
        new("Sulafat", "Warm, Middle pitch")
    ];
}

