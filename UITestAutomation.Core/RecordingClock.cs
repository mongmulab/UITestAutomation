namespace UITestAutomation.Core;

// Turns hook timestamps (milliseconds, GetTickCount based) into per-event delays.
public sealed class RecordingClock
{
    private readonly int _firstDelayMs;
    private uint? _last;

    public RecordingClock(int firstDelayMs = 1000) => _firstDelayMs = firstDelayMs;

    public int Next(uint hookTimeMs)
    {
        // The first event waits a fixed time so the user can switch to the target window after Play.
        // unchecked subtraction keeps the gap correct when the 32-bit tick counter wraps (about 49.7 days).
        int delay = _last is uint prev ? (int)unchecked(hookTimeMs - prev) : _firstDelayMs;
        _last = hookTimeMs;
        return Math.Max(0, delay);
    }
}
