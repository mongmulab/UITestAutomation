using System.Text.Json;
using UITestAutomation.Core;
using Xunit;

namespace UITestAutomation.Tests;

public class CoordinateMapperTests
{
    // A typical Windows 11 window at 100% scale: the client area starts 8 px right of and
    // 31 px below the window rectangle (border + title bar).
    private static readonly Point WindowRectTopLeft = new(100, 200);
    private static readonly Point ClientOrigin = new(108, 231);

    [Fact]
    public void Playback_lands_on_the_recorded_client_point()
    {
        var recordedClient = new Point(300, 150);
        var stored = CoordinateMapper.ToStored(recordedClient, calib: null);

        var screen = CoordinateMapper.ToScreen(stored, calib: null, ClientOrigin);

        Assert.Equal(new Point(408, 381), screen);
    }

    [Fact]
    public void Adding_to_the_window_rectangle_misses_by_the_title_bar_and_border()
    {
        // The 1.0.0 bug: client coordinates were added to GetWindowRect's top-left.
        var stored = new Point(300, 150);
        var buggy = new Point(WindowRectTopLeft.X + stored.X, WindowRectTopLeft.Y + stored.Y);
        var fixedPoint = CoordinateMapper.ToScreen(stored, calib: null, ClientOrigin);

        Assert.Equal(8, fixedPoint.X - buggy.X);
        Assert.Equal(31, fixedPoint.Y - buggy.Y);
    }

    [Fact]
    public void Calibrated_round_trip_returns_the_same_screen_point()
    {
        var calib = CoordinateMapper.FromCorners(new Point(80, 120), new Point(1080, 720));
        var client = new Point(500, 400);

        var stored = CoordinateMapper.ToStored(client, calib);
        Assert.Equal(new Point(420, 280), stored);

        var screen = CoordinateMapper.ToScreen(stored, calib, ClientOrigin);
        Assert.Equal(new Point(ClientOrigin.X + client.X, ClientOrigin.Y + client.Y), screen);
    }

    [Fact]
    public void Corners_can_be_clicked_in_any_order()
    {
        var a = CoordinateMapper.FromCorners(new Point(10, 20), new Point(110, 220));
        var b = CoordinateMapper.FromCorners(new Point(110, 220), new Point(10, 20));

        Assert.Equal((10, 20, 100, 200), (a.OffsetX, a.OffsetY, a.ContentW, a.ContentH));
        Assert.Equal((a.OffsetX, a.OffsetY, a.ContentW, a.ContentH), (b.OffsetX, b.OffsetY, b.ContentW, b.ContentH));
    }

    [Fact]
    public void Same_corner_twice_still_gives_a_usable_area()
    {
        var c = CoordinateMapper.FromCorners(new Point(50, 50), new Point(50, 50));
        Assert.True(c.IsValid);
        Assert.Equal(1, c.ContentW);
    }

    [Fact]
    public void Device_mapping_scales_content_to_device_size()
    {
        var calib = new Calibration { OffsetX = 0, OffsetY = 0, ContentW = 540, ContentH = 1200 };
        var dev = CoordinateMapper.ToDevice(new Point(270, 600), calib, 0, 0, 1080, 2400);
        Assert.Equal(new Point(540, 1200), dev);
    }

    [Fact]
    public void Device_mapping_uses_client_area_without_calibration()
    {
        var dev = CoordinateMapper.ToDevice(new Point(100, 50), null, clientW: 400, clientH: 200, devW: 1080, devH: 2400);
        Assert.Equal(new Point(270, 600), dev);
    }

    [Theory]
    [InlineData(0, 200, 1080, 2400)]   // no client width
    [InlineData(400, 200, 0, 2400)]    // device width missing
    [InlineData(400, 200, 1080, -1)]   // negative device height
    public void Device_mapping_refuses_unknown_sizes(int clientW, int clientH, int devW, int devH)
    {
        Assert.Null(CoordinateMapper.ToDevice(new Point(1, 1), null, clientW, clientH, devW, devH));
    }
}

public class MacroSerializerTests
{
    [Fact]
    public void Legacy_array_loads_and_keeps_current_calibration()
    {
        var (events, calib, title) = MacroSerializer.Parse("""[{"Type":0,"X":3,"Y":4,"DelayMs":800}]""");

        Assert.Single(events);
        Assert.Equal(EventType.MouseClick, events[0].Type);
        Assert.Equal(800, events[0].DelayMs);
        Assert.Null(calib);
        Assert.Null(title);
    }

    [Fact]
    public void Old_files_without_Extended_load_as_not_extended()
    {
        var (events, _, _) = MacroSerializer.Parse("""{"Events":[{"Type":1,"VkCode":13,"ScanCode":28,"DelayMs":200}]}""");
        Assert.False(events[0].Extended);
    }

    [Fact]
    public void Object_form_round_trips_with_calibration_and_extended_keys()
    {
        var calib = new Calibration { OffsetX = 80, OffsetY = 120, ContentW = 1000, ContentH = 600 };
        var events = new List<InputEvent>
        {
            new() { Type = EventType.KeyDown, VkCode = 0x25, ScanCode = 0x4B, Extended = true, DelayMs = 0 },
            new() { Type = EventType.MouseClick, X = 10, Y = 20, DelayMs = 35 },
        };

        var json = MacroSerializer.Serialize(events, calib);
        var (back, backCalib, _) = MacroSerializer.Parse(json);

        Assert.StartsWith("{", json.TrimStart());
        Assert.NotNull(backCalib);
        Assert.Equal(1000, backCalib!.ContentW);
        Assert.True(back[0].Extended);
        Assert.Equal(0, back[0].DelayMs);
        Assert.Equal(35, back[1].DelayMs);
    }

    [Fact]
    public void Target_title_is_kept_so_a_loaded_macro_finds_its_window()
    {
        var json = MacroSerializer.Serialize(new List<InputEvent> { new() { X = 5, Y = 6 } }, new Calibration(), "Notepad");
        var (events, calib, title) = MacroSerializer.Parse(json);

        Assert.Equal("Notepad", title);
        Assert.False(calib!.IsValid);
        Assert.DoesNotContain("Calibration", json);
        Assert.Equal(5, events[0].X);
    }

    [Fact]
    public void Without_calibration_the_array_form_is_written()
    {
        var json = MacroSerializer.Serialize(new List<InputEvent> { new() }, new Calibration());
        Assert.StartsWith("[", json.TrimStart());
    }

    [Fact]
    public void Event_types_are_written_as_names()
    {
        var json = MacroSerializer.Serialize(new List<InputEvent> { new() { Type = EventType.KeyUp } }, null);
        Assert.Contains("\"KeyUp\"", json);
    }

    // 1.0.0 wrote enum numbers but its own example and README used names, which it could not read.
    [Fact]
    public void Shipped_example_macro_still_loads()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "example_macro.json");
        var (events, _, title) = MacroSerializer.Parse(File.ReadAllText(path));
        Assert.Equal("Notepad", title);
        Assert.NotEmpty(events);
        Assert.Contains(events, e => e.Extended);
    }

    [Fact]
    public void Text_that_is_not_json_is_rejected()
    {
        Assert.ThrowsAny<JsonException>(() => MacroSerializer.Parse("hello"));
    }
}

public class RecordingClockTests
{
    [Fact]
    public void Delays_follow_the_real_gaps_between_inputs()
    {
        var clock = new RecordingClock(firstDelayMs: 1000);
        Assert.Equal(1000, clock.Next(5_000));
        Assert.Equal(120, clock.Next(5_120));
        Assert.Equal(0, clock.Next(5_120));
    }

    [Fact]
    public void Tick_counter_wrap_does_not_produce_a_huge_or_negative_delay()
    {
        var clock = new RecordingClock();
        clock.Next(uint.MaxValue - 49);
        Assert.Equal(100, clock.Next(50));
    }
}

public class AdbKeyMapTests
{
    [Theory]
    [InlineData(0x0D, 66)]
    [InlineData(0x08, 67)]
    [InlineData(0x26, 19)]
    public void Known_keys_map_to_android_keycodes(int vk, int android) =>
        Assert.Equal(android, AdbKeyMap.FromVk(vk));

    [Fact]
    public void Unknown_keys_are_skipped() => Assert.Null(AdbKeyMap.FromVk(0x41));
}
