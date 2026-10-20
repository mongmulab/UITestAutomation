namespace UITestAutomation.Core;

public readonly record struct Point(int X, int Y);

// Coordinate rules shared by recording and playback.
// Recording stores client coordinates of the target window (minus the calibration offset).
// Playback must turn them back into screen coordinates through the client origin,
// not the window rectangle: the window rectangle includes the title bar and borders.
public static class CoordinateMapper
{
    public static Point ToStored(Point client, Calibration? calib) =>
        calib is { IsValid: true } ? new Point(client.X - calib.OffsetX, client.Y - calib.OffsetY) : client;

    public static Point ToClient(Point stored, Calibration? calib) =>
        calib is { IsValid: true } ? new Point(stored.X + calib.OffsetX, stored.Y + calib.OffsetY) : stored;

    // clientOrigin is the screen position of the client area's top-left (ClientToScreen of 0,0).
    public static Point ToScreen(Point stored, Calibration? calib, Point clientOrigin)
    {
        var c = ToClient(stored, calib);
        return new Point(clientOrigin.X + c.X, clientOrigin.Y + c.Y);
    }

    // Maps a stored point onto an Android device of devW x devH.
    // The base area is the calibrated content when present, otherwise the whole client area.
    public static Point? ToDevice(Point stored, Calibration? calib, int clientW, int clientH, int devW, int devH)
    {
        int baseW = calib is { IsValid: true } ? calib.ContentW : clientW;
        int baseH = calib is { IsValid: true } ? calib.ContentH : clientH;
        if (baseW <= 0 || baseH <= 0 || devW <= 0 || devH <= 0) return null;
        return new Point(
            (int)Math.Round((double)stored.X / baseW * devW),
            (int)Math.Round((double)stored.Y / baseH * devH));
    }

    // Two clicks in any order become a calibration rectangle.
    public static Calibration FromCorners(Point a, Point b)
    {
        int x1 = Math.Min(a.X, b.X), y1 = Math.Min(a.Y, b.Y);
        int x2 = Math.Max(a.X, b.X), y2 = Math.Max(a.Y, b.Y);
        return new Calibration
        {
            OffsetX = x1,
            OffsetY = y1,
            ContentW = Math.Max(1, x2 - x1),
            ContentH = Math.Max(1, y2 - y1)
        };
    }
}
