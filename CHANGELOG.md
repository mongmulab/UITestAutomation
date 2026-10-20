# Changelog

## 1.1.0

### Fixed

- **Playback clicks landed below and to the right of the recorded point.**
  Recording stored client coordinates, but playback added them to the window rectangle,
  which includes the title bar and borders. Measured on a Windows 11 PC at 125% scale,
  that gap was 9 px across and 38 px down. Playback now converts from the client origin.
  Calibration was often used to hide this offset; it is no longer needed for PC playback.
- **Arrow keys, Delete, Home/End and similar keys replayed as numeric keypad keys.**
  The extended-key flag is now recorded and sent.
- **The bundled `example_macro.json` and the README example could not be loaded.**
  They used `"Type": "MouseClick"` while 1.0.0 only read numbers. Both forms now load,
  and new files are written with names.
- **A loaded macro played at absolute screen positions.** Only Record set the target window,
  so a file loaded later had no window to be relative to. The target window title is now
  saved in the file and looked up again on Play.
- After calibration the mouse hook stayed on, so later clicks kept being added as events.
- The ESC key that stops recording was saved as an event and sent on playback.
- Clicks on UITestAutomation's own window (for example **Stop Record**) were recorded.
- Every event waited a fixed 1000 ms. Real gaps between inputs are now recorded.
- Long recordings could lose the hook because the whole list was redrawn inside the hook callback.
- Each playback added another hotkey message handler.
- Editing the JSON into an invalid state was silently ignored and the old events were played.
  Playback now stops and says the JSON is invalid.
- ADB failures (missing `adb`, non-zero exit) were hidden. They now stop playback with the reason.
- The status line and stop-hotkey hint were cut off at the bottom of the window.

### Changed

- .NET 7 (end of support May 2024) to .NET 8.
- DPI awareness is declared in `app.manifest`. Setting it from code after WPF starts had no effect.
- Coordinate, file-format and timing logic moved to `UITestAutomation.Core` with unit tests.
- More ADB key mappings: Space, arrow keys, Delete.
- The app opens a macro file passed on the command line.
- The single-file release is compressed.

## 1.0.0

First public release.
