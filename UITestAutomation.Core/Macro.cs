using System.Text.Json.Serialization;

namespace UITestAutomation.Core;

public enum EventType { MouseClick, KeyDown, KeyUp }

public class InputEvent
{
    public EventType Type { get; set; } = EventType.MouseClick;
    // Mouse: client coordinates of the target window, or content coordinates when calibrated,
    // or absolute screen coordinates when no target window was found.
    public int X { get; set; }
    public int Y { get; set; }
    public int Button { get; set; } = 0; // 0=Left
    // Keyboard
    public int VkCode { get; set; }
    public int ScanCode { get; set; }
    // Arrow keys, Insert/Delete, Home/End, PageUp/PageDown, right Ctrl/Alt.
    // Without this flag a scan-code replay turns them into numeric keypad keys.
    public bool Extended { get; set; }
    // Wait before this event. Recorded from the real gap between inputs.
    public int DelayMs { get; set; } = 1000;
}

public class Calibration
{
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public int ContentW { get; set; }
    public int ContentH { get; set; }

    [JsonIgnore]
    public bool IsValid => ContentW > 0 && ContentH > 0;
}

// JSON root (calibration + events). Older files that are a bare event array still load.
public class MacroFile
{
    // Part of the target window title. Coordinates are relative to this window's client area,
    // so playback finds it again by title. Empty means absolute screen coordinates.
    public string? TargetTitle { get; set; }
    public Calibration? Calibration { get; set; }
    public List<InputEvent> Events { get; set; } = new();
}
