namespace UITestAutomation.Core;

// Windows virtual-key code to Android KEYCODE. Keys without a mapping are skipped in ADB playback.
public static class AdbKeyMap
{
    public static int? FromVk(int vk) => vk switch
    {
        0x0D => 66,   // Enter     -> KEYCODE_ENTER
        0x1B => 111,  // Esc       -> KEYCODE_ESCAPE
        0x08 => 67,   // Backspace -> KEYCODE_DEL
        0x09 => 61,   // Tab       -> KEYCODE_TAB
        0x20 => 62,   // Space     -> KEYCODE_SPACE
        0x25 => 21,   // Left      -> KEYCODE_DPAD_LEFT
        0x26 => 19,   // Up        -> KEYCODE_DPAD_UP
        0x27 => 22,   // Right     -> KEYCODE_DPAD_RIGHT
        0x28 => 20,   // Down      -> KEYCODE_DPAD_DOWN
        0x2E => 112,  // Delete    -> KEYCODE_FORWARD_DEL
        _ => null
    };
}
