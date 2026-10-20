using System.Text.Json;
using System.Text.Json.Serialization;

namespace UITestAutomation.Core;

public static class MacroSerializer
{
    // Event types are written as names ("MouseClick") so hand-edited files stay readable.
    // Numbers (0, 1, 2) written by 1.0.0 are still accepted when reading.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    // Writes the object form when there is a target window or a valid calibration,
    // otherwise the legacy array form that 1.0.0 can also read.
    public static string Serialize(IReadOnlyList<InputEvent> events, Calibration? calib, string? targetTitle = null)
    {
        bool hasTarget = !string.IsNullOrWhiteSpace(targetTitle);
        bool hasCalib = calib is { IsValid: true };
        if (hasTarget || hasCalib)
            return JsonSerializer.Serialize(new MacroFile
            {
                TargetTitle = hasTarget ? targetTitle : null,
                Calibration = hasCalib ? calib : null,
                Events = events.ToList()
            }, Options);
        return JsonSerializer.Serialize(events, Options);
    }

    // Calibration and TargetTitle are null when the text is the legacy array form,
    // so the caller can keep what it already has.
    public static (List<InputEvent> Events, Calibration? Calibration, string? TargetTitle) Parse(string text)
    {
        text = text.Trim();
        if (text.StartsWith('{'))
        {
            var mf = JsonSerializer.Deserialize<MacroFile>(text, Options) ?? new MacroFile();
            return (mf.Events ?? new(), mf.Calibration ?? new Calibration(), mf.TargetTitle ?? "");
        }
        if (text.StartsWith('['))
            return (JsonSerializer.Deserialize<List<InputEvent>>(text, Options) ?? new(), null, null);
        throw new JsonException("Macro JSON must start with '{' or '['.");
    }
}
