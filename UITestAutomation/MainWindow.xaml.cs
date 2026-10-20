using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using UITestAutomation.Core;
using CorePoint = UITestAutomation.Core.Point;

namespace UITestAutomation
{
    public partial class MainWindow : Window
    {
        private List<InputEvent> _events = new();
        private Calibration _calib = new(); // 기본 0 / invalid

        // ===== Recording state ===============================================
        private IntPtr _mouseHookId = IntPtr.Zero;
        private IntPtr _kbdHookId = IntPtr.Zero;
        private readonly LowLevelMouseProc _mouseProc;
        private readonly LowLevelKeyboardProc _kbdProc;
        private IntPtr _targetWindow = IntPtr.Zero;
        private string _targetTitle = "";
        private IntPtr _ownWindow = IntPtr.Zero;
        private bool _recording = false;
        private RecordingClock _clock = new();

        // 훅 콜백에서 목록을 매번 다시 그리면 콜백이 늦어져 Windows 가 훅을 끊을 수 있다.
        // 콜백은 표시만 해 두고, 화면 갱신은 타이머가 몰아서 한다.
        private bool _listDirty = false;
        private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };

        // ===== Calibration state ==============================================
        private bool _calibActive = false;
        private CorePoint? _calibPtTL = null; // client coords
        private CorePoint? _calibPtBR = null; // client coords

        // ===== Playback state ================================================
        private CancellationTokenSource? _playbackCts;
        private const int HOTKEY_ID = 0x1000;
        private bool _hotkeyRegistered = false;

        public MainWindow()
        {
            InitializeComponent();
            _mouseProc = MouseHookCallback;
            _kbdProc = KeyboardHookCallback;
            _refreshTimer.Tick += (s, e) =>
            {
                if (!_listDirty) return;
                _listDirty = false;
                RefreshListAndJson();
            };
            UpdateUiState();
            UpdateCalibLabels();

            // UITestAutomation.exe macro.json 로 실행하면 그 파일을 열어 둔다.
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && File.Exists(args[1])) LoadMacroFile(args[1]);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _ownWindow = new WindowInteropHelper(this).Handle;
            // 핫키 메시지 훅은 한 번만 건다. 재생할 때마다 걸면 같은 처리기가 겹겹이 쌓인다.
            HwndSource.FromHwnd(_ownWindow)?.AddHook(HwndHook);
        }

        protected override void OnClosed(EventArgs e)
        {
            _playbackCts?.Cancel();
            UnhookMouse();
            UnhookKeyboard();
            UnregisterStopHotkey();
            base.OnClosed(e);
        }

        // ===== UI helpers =====================================================
        private void UI(Action a) => Dispatcher.Invoke(a);

        private void SetStatus(string text) => UI(() => TxtStatus.Text = text);

        private void UpdateUiState()
        {
            BtnStopPlay.IsEnabled = false;
            BtnStopRecord.IsEnabled = false;
            BtnRecord.IsEnabled = true;
            BtnPlay.IsEnabled = _events.Count > 0;
            BtnSave.IsEnabled = _events.Count > 0;
        }

        private void UpdateCalibLabels()
        {
            if (_calib.IsValid)
            {
                LblOffX.Text = _calib.OffsetX.ToString();
                LblOffY.Text = _calib.OffsetY.ToString();
                LblCW.Text = _calib.ContentW.ToString();
                LblCH.Text = _calib.ContentH.ToString();
                LblCalibHint.Text = "Calibration is active.";
            }
            else
            {
                LblOffX.Text = LblOffY.Text = LblCW.Text = LblCH.Text = "-";
                LblCalibHint.Text = "Click Start, then the top-left and bottom-right corners of the phone screen.";
            }
        }

        private sealed record EventRow(int No, string Kind, string Detail, string Delay);

        private static string KeyName(int vk)
        {
            var key = KeyInterop.KeyFromVirtualKey(vk);
            return key == Key.None ? "?" : key.ToString();
        }

        private void RefreshListAndJson()
        {
            ListEvents.Items.Clear();
            for (int i = 0; i < _events.Count; i++)
            {
                var ev = _events[i];
                string ext = ev.Extended ? " (ext)" : "";
                var row = ev.Type switch
                {
                    EventType.MouseClick => new EventRow(i + 1, "CLICK", $"({ev.X}, {ev.Y})", $"{ev.DelayMs} ms"),
                    EventType.KeyDown => new EventRow(i + 1, "KEY DOWN", $"{KeyName(ev.VkCode)}{ext}   VK {ev.VkCode}", $"{ev.DelayMs} ms"),
                    EventType.KeyUp => new EventRow(i + 1, "KEY UP", $"{KeyName(ev.VkCode)}{ext}   VK {ev.VkCode}", $"{ev.DelayMs} ms"),
                    _ => new EventRow(i + 1, "?", "", "")
                };
                ListEvents.Items.Add(row);
            }
            TxtCount.Text = _events.Count.ToString();
            TxtTarget.Text = _targetTitle.Length > 0 ? $"Target: {_targetTitle}" : "Target: screen";

            // JSON 미리보기: 캘리브 유효하면 객체형식, 아니면 레거시 배열
            TxtJson.Text = MacroSerializer.Serialize(_events, _calib, _targetTitle);
        }

        // 재생 직전에 편집한 JSON 을 반영한다. 고친 JSON 이 깨져 있으면 재생하지 않는다.
        private bool TryLoadEventsFromJsonText()
        {
            var text = TxtJson.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text)) return true;
            try
            {
                var (events, calib, title) = MacroSerializer.Parse(text);
                _events = events;
                if (calib != null) _calib = calib; // 배열 형식이면 캘리브는 유지
                if (title != null) SetTarget(title);
                UpdateCalibLabels();
                RefreshListAndJson();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("The JSON in the editor is not valid, so playback did not start.\n\n" + ex.Message,
                    "JSON error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        // ===== Buttons ========================================================
        private void BtnRecord_Click(object sender, RoutedEventArgs e)
        {
            var title = Microsoft.VisualBasic.Interaction.InputBox(
                "Title of the window to record (partial match).\n ex: \"Chrome\" ",
                "Window Title", "");
            title = (title ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(title)) return;

            _targetWindow = FindWindowByTitleContains(title, _ownWindow);
            _targetTitle = title;
            if (_targetWindow == IntPtr.Zero)
            {
                if (MessageBox.Show("Could not find the window. Would you like to record in absolute screen coordinates?",
                    "Window not found", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.No)
                    return;
                _targetTitle = "";
            }
            StartRecording();
        }

        // 파일에서 읽은 매크로는 대상 창 핸들이 없다. 제목을 기억해 두고 재생할 때 다시 찾는다.
        private void SetTarget(string title)
        {
            _targetTitle = title;
            _targetWindow = string.IsNullOrWhiteSpace(title) ? IntPtr.Zero : FindWindowByTitleContains(title, _ownWindow);
        }

        private void BtnStopRecord_Click(object sender, RoutedEventArgs e) => StopRecording();

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "JSON files (*.json)|*.json", DefaultExt = ".json" };
            if (dlg.ShowDialog() != true) return;

            foreach (var ev in _events) if (ev.DelayMs < 0) ev.DelayMs = 0;
            try
            {
                await File.WriteAllTextAsync(dlg.FileName, MacroSerializer.Serialize(_events, _calib, _targetTitle));
                SetStatus($"Saved: {dlg.FileName}");
            }
            catch (Exception ex) { MessageBox.Show("JSON Save Fail: " + ex.Message); }
        }

        private void BtnLoad_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "JSON files (*.json)|*.json", DefaultExt = ".json" };
            if (dlg.ShowDialog() == true) LoadMacroFile(dlg.FileName);
        }

        private void LoadMacroFile(string path)
        {
            try
            {
                var (events, calib, title) = MacroSerializer.Parse(File.ReadAllText(path));
                _events = events;
                if (calib != null) _calib = calib; // 배열 형식이면 캘리브는 유지
                if (title != null) SetTarget(title);
                UpdateCalibLabels();
                RefreshListAndJson();
                UpdateUiState();
                SetStatus($"Loaded: {Path.GetFileName(path)}");
            }
            catch (Exception ex) { MessageBox.Show("JSON Load Fail: " + ex.Message); }
        }

        private void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (_events.Count == 0) { MessageBox.Show("Please record first or load JSON."); return; }
            StartPlayback();
        }

        private void BtnStopPlay_Click(object sender, RoutedEventArgs e) => StopPlayback();

        private void ListEvents_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ListEvents.SelectedIndex >= 0 &&
                MessageBox.Show("Delete selected event?", "Delete", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                _events.RemoveAt(ListEvents.SelectedIndex);
                RefreshListAndJson();
                UpdateUiState();
            }
        }

        private void ChkUseAdb_Checked(object s, RoutedEventArgs e) { TxtDevW.IsEnabled = true; TxtDevH.IsEnabled = true; }
        private void ChkUseAdb_Unchecked(object s, RoutedEventArgs e) { TxtDevW.IsEnabled = false; TxtDevH.IsEnabled = false; }

        // ===== Calibration UI =================================================
        private void BtnCalibStart_Click(object sender, RoutedEventArgs e)
        {
            if (_targetWindow == IntPtr.Zero)
            {
                MessageBox.Show("Select the window you want to record (Record button).");
                return;
            }
            _calibActive = true;
            _calibPtTL = null;
            _calibPtBR = null;
            LblCalibHint.Text = "Now click the top-left corner, then the bottom-right corner.";
            SetStatus("Calibration waiting for 2 clicks...");
            // 녹화 전후 어느 때나 보정할 수 있게 마우스 훅만 잠시 건다. 두 번 누르면 다시 푼다.
            if (_mouseHookId == IntPtr.Zero) _mouseHookId = NativeMethods.SetMouseHook(_mouseProc);
        }

        private void BtnCalibClear_Click(object sender, RoutedEventArgs e)
        {
            _calib = new Calibration();
            UpdateCalibLabels();
            SetStatus("Calibration cleared.");
        }

        private void FinalizeCalibration(CorePoint tl, CorePoint br)
        {
            _calib = CoordinateMapper.FromCorners(tl, br);
            _calibActive = false;
            // 녹화 중이 아니면 보정용으로 건 훅을 푼다. 남겨 두면 이후의 모든 클릭이 이벤트로 쌓인다.
            if (!_recording) UnhookMouse();
            UpdateCalibLabels();
            SetStatus("Calibration saved.");
        }

        // ===== Recording (mouse + keyboard) ==================================
        private void StartRecording()
        {
            _events.Clear();
            _clock = new RecordingClock();
            _recording = true;

            if (_mouseHookId == IntPtr.Zero) _mouseHookId = NativeMethods.SetMouseHook(_mouseProc);
            if (_kbdHookId == IntPtr.Zero) _kbdHookId = NativeMethods.SetKeyboardHook(_kbdProc);
            _refreshTimer.Start();

            BtnRecord.IsEnabled = false;
            BtnStopRecord.IsEnabled = true;
            BtnSave.IsEnabled = false;
            BtnLoad.IsEnabled = false;
            BtnPlay.IsEnabled = false;
            SetStatus("Recording... (ESC: stop)");
        }

        private void StopRecording()
        {
            if (!_recording) return;
            _recording = false;
            _refreshTimer.Stop();
            if (!_calibActive) UnhookMouse();
            UnhookKeyboard();

            _listDirty = false;
            RefreshListAndJson();
            BtnRecord.IsEnabled = true;
            BtnStopRecord.IsEnabled = false;
            BtnSave.IsEnabled = _events.Count > 0;
            BtnLoad.IsEnabled = true;
            BtnPlay.IsEnabled = _events.Count > 0;
            SetStatus($"Recording stopped. {_events.Count} events.");
        }

        private void UnhookMouse()
        {
            if (_mouseHookId != IntPtr.Zero) { NativeMethods.UnhookWindowsHookEx(_mouseHookId); _mouseHookId = IntPtr.Zero; }
        }

        private void UnhookKeyboard()
        {
            if (_kbdHookId != IntPtr.Zero) { NativeMethods.UnhookWindowsHookEx(_kbdHookId); _kbdHookId = IntPtr.Zero; }
        }

        private bool IsOwnWindowAt(int sx, int sy)
        {
            var hit = NativeMethods.WindowFromPoint(new POINT { x = sx, y = sy });
            return hit != IntPtr.Zero && NativeMethods.GetAncestor(hit, NativeMethods.GA_ROOT) == _ownWindow;
        }

        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            const int WM_LBUTTONDOWN = 0x0201;
            if (nCode >= 0 && wParam.ToInt32() == WM_LBUTTONDOWN)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                int sx = info.pt.x, sy = info.pt.y; // screen coords

                // 이 도구 창(Stop Record 버튼 등)을 누른 것은 녹화하지 않는다. 재생 때 그 자리를 누르게 된다.
                if (!IsOwnWindowAt(sx, sy))
                {
                    // 대상 창 클라이언트로 변환
                    var client = new CorePoint(sx, sy);
                    if (_targetWindow != IntPtr.Zero)
                    {
                        var pt = new POINT { x = sx, y = sy };
                        if (NativeMethods.ScreenToClient(_targetWindow, ref pt)) client = new CorePoint(pt.x, pt.y);
                    }

                    if (_calibActive)
                    {
                        // 캘리브레이션 수집 모드일 때: 두 점만 수집
                        if (_calibPtTL is null) { _calibPtTL = client; SetStatus("Calibration: click Bottom-Right."); }
                        else if (_calibPtBR is null)
                        {
                            _calibPtBR = client;
                            var tl = _calibPtTL.Value; var br = _calibPtBR.Value;
                            Dispatcher.BeginInvoke(() => FinalizeCalibration(tl, br));
                        }
                    }
                    else if (_recording)
                    {
                        // 일반 녹화: 캘리브 유효 시 컨텐츠 좌표로 정규화
                        var stored = CoordinateMapper.ToStored(client, _calib);
                        _events.Add(new InputEvent
                        {
                            Type = EventType.MouseClick, X = stored.X, Y = stored.Y, Button = 0,
                            DelayMs = _clock.Next(info.time)
                        });
                        _listDirty = true;
                    }
                }
            }
            return NativeMethods.CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
        }

        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
            const uint LLKHF_EXTENDED = 0x01;
            const int VK_ESCAPE = 0x1B;
            int msg = wParam.ToInt32();
            if (nCode >= 0 && _recording && (msg == WM_KEYDOWN || msg == WM_KEYUP || msg == WM_SYSKEYDOWN || msg == WM_SYSKEYUP))
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int vk = (int)info.vkCode;
                bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;

                // ESC 는 녹화를 끝내는 키다. 이벤트로 남기면 재생할 때 대상 창에 ESC 가 들어간다.
                if (vk == VK_ESCAPE)
                {
                    if (down) Dispatcher.BeginInvoke(StopRecording);
                    return NativeMethods.CallNextHookEx(_kbdHookId, nCode, wParam, lParam);
                }

                // 정지 핫키(Ctrl+Alt+X)는 녹화 제외
                bool isCtrl = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
                bool isAlt = Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt);
                bool isX = vk == KeyInterop.VirtualKeyFromKey(Key.X);
                if (!(isCtrl && isAlt && isX))
                {
                    _events.Add(new InputEvent
                    {
                        Type = down ? EventType.KeyDown : EventType.KeyUp,
                        VkCode = vk,
                        ScanCode = (int)info.scanCode,
                        Extended = (info.flags & LLKHF_EXTENDED) != 0,
                        DelayMs = _clock.Next(info.time)
                    });
                    _listDirty = true;
                }
            }
            return NativeMethods.CallNextHookEx(_kbdHookId, nCode, wParam, lParam);
        }

        // ===== Playback =======================================================
        private void StartPlayback()
        {
            // JSON 편집 반영
            if (!TryLoadEventsFromJsonText()) return;

            // 재생 중 캘리브 수집 금지
            _calibActive = false;

            // UI 상태
            BtnPlay.IsEnabled = false;
            BtnStopPlay.IsEnabled = true;
            BtnRecord.IsEnabled = false;
            BtnLoad.IsEnabled = false;
            BtnSave.IsEnabled = false;
            TxtStatus.Text = "Playing...";

            // 재생 중 편집 잠금 (열거 수정 방지)
            ListEvents.IsEnabled = false;
            TxtJson.IsReadOnly = true;

            _playbackCts = new CancellationTokenSource();
            var token = _playbackCts.Token;

            // 이벤트·설정 스냅샷. 재생 스레드는 이 복사본만 본다.
            var snapshot = _events.ToArray();
            var calib = _calib;
            // 창을 닫았다 다시 열었으면 핸들이 바뀐다. 제목으로 다시 찾는다.
            if (_targetTitle.Length > 0 && !NativeMethods.IsWindow(_targetWindow))
                _targetWindow = FindWindowByTitleContains(_targetTitle, _ownWindow);
            if (_targetTitle.Length > 0 && _targetWindow == IntPtr.Zero)
            {
                MessageBox.Show($"No window whose title contains \"{_targetTitle}\" is open. Open it and press Play again.");
                StopPlaybackUiCleanup();
                TxtStatus.Text = "Idle";
                return;
            }
            var target = _targetWindow;
            bool useAdb = ChkUseAdb.IsChecked == true;
            int devW = 0, devH = 0;
            if (useAdb && (!int.TryParse(TxtDevW.Text, out devW) || !int.TryParse(TxtDevH.Text, out devH) || devW <= 0 || devH <= 0))
            {
                MessageBox.Show("Device W and H must be positive numbers for ADB playback.");
                StopPlaybackUiCleanup();
                TxtStatus.Text = "Idle";
                return;
            }

            RegisterStopHotkey();

            _ = Task.Run(async () =>
            {
                string end = "Playback finished.";
                try
                {
                    await PlaybackLoop(snapshot, calib, target, token, useAdb, devW, devH);
                }
                catch (OperationCanceledException) { end = "Playback stopped by user."; }
                catch (Exception ex) { end = "Playback error: " + ex.Message; UI(() => MessageBox.Show(end)); }
                finally
                {
                    UI(() =>
                    {
                        UnregisterStopHotkey();
                        StopPlaybackUiCleanup();
                        TxtStatus.Text = end;
                    });
                }
            });
        }

        private void StopPlayback() => _playbackCts?.Cancel();

        private void StopPlaybackUiCleanup()
        {
            BtnPlay.IsEnabled = _events.Count > 0;
            BtnStopPlay.IsEnabled = false;
            BtnRecord.IsEnabled = true;
            BtnLoad.IsEnabled = true;
            BtnSave.IsEnabled = _events.Count > 0;

            // 재생 종료 후 편집 가능
            ListEvents.IsEnabled = true;
            TxtJson.IsReadOnly = false;
        }

        private async Task PlaybackLoop(IReadOnlyList<InputEvent> events, Calibration calib, IntPtr target,
            CancellationToken token, bool useAdb, int devW, int devH)
        {
            foreach (var ev in events)
            {
                token.ThrowIfCancellationRequested();
                if (ev.DelayMs > 0) await Task.Delay(ev.DelayMs, token);

                switch (ev.Type)
                {
                    case EventType.MouseClick:
                        var stored = new CorePoint(ev.X, ev.Y);
                        if (useAdb)
                        {
                            // ADB: 캘리브가 있으면 컨텐츠 기준, 없으면 클라이언트 기준
                            int clientW = 0, clientH = 0;
                            if (target != IntPtr.Zero && NativeMethods.GetClientRect(target, out RECT rc))
                            { clientW = rc.Right - rc.Left; clientH = rc.Bottom - rc.Top; }
                            var dev = CoordinateMapper.ToDevice(stored, calib, clientW, clientH, devW, devH)
                                ?? throw new InvalidOperationException("Invalid window/calibration size for ADB mapping.");
                            RunAdb($"shell input tap {dev.X} {dev.Y}");
                        }
                        else
                        {
                            // 클라이언트 원점(ClientToScreen)으로 화면 좌표를 만든다. 창 사각형(GetWindowRect)은
                            // 제목 표시줄과 테두리를 포함해서, 그 기준으로 더하면 클릭이 아래·오른쪽으로 밀린다.
                            // 매 이벤트마다 다시 읽으니 재생 중에 창을 옮겨도 따라간다.
                            var screen = stored;
                            if (target != IntPtr.Zero)
                            {
                                if (!NativeMethods.IsWindow(target))
                                    throw new InvalidOperationException("The target window was closed.");
                                var o = new POINT { x = 0, y = 0 };
                                NativeMethods.ClientToScreen(target, ref o);
                                screen = CoordinateMapper.ToScreen(stored, calib, new CorePoint(o.x, o.y));
                            }
                            ClickAbsVirtual(screen.X, screen.Y);
                        }
                        break;

                    case EventType.KeyDown:
                    case EventType.KeyUp:
                        if (useAdb)
                        {
                            // ADB 는 누름·뗌을 따로 보내지 않는다. 누를 때 한 번만 보낸다.
                            if (ev.Type == EventType.KeyDown && AdbKeyMap.FromVk(ev.VkCode) is int androidKey)
                                RunAdb($"shell input keyevent {androidKey}");
                        }
                        else
                        {
                            SendKey(ev.Type == EventType.KeyDown, ev.VkCode, ev.ScanCode, ev.Extended);
                        }
                        break;
                }
            }
        }

        // ===== Low-level: Mouse/Keyboard send =================================
        private static void ClickAbsVirtual(int screenX, int screenY)
        {
            const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
            int vx = NativeMethods.GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vy = NativeMethods.GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vw = NativeMethods.GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int vh = NativeMethods.GetSystemMetrics(SM_CYVIRTUALSCREEN);

            int nx = (int)Math.Round((screenX - vx) * 65535.0 / Math.Max(1, vw - 1));
            int ny = (int)Math.Round((screenY - vy) * 65535.0 / Math.Max(1, vh - 1));

            var inputs = new INPUT[]
            {
                new INPUT{ type = INPUT_MOUSE, U = new InputUnion{ mi = new MOUSEINPUT{ dx = nx, dy = ny, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK } } },
                new INPUT{ type = INPUT_MOUSE, U = new InputUnion{ mi = new MOUSEINPUT{ dwFlags = MOUSEEVENTF_LEFTDOWN } } },
                new INPUT{ type = INPUT_MOUSE, U = new InputUnion{ mi = new MOUSEINPUT{ dwFlags = MOUSEEVENTF_LEFTUP } } },
            };
            NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        private static void SendKey(bool down, int vk, int scan, bool extended)
        {
            const uint INPUT_KEYBOARD = 1;
            const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_SCANCODE = 0x0008;

            uint flags = 0;
            if (!down) flags |= KEYEVENTF_KEYUP;
            if (scan > 0) flags |= KEYEVENTF_SCANCODE;
            if (extended) flags |= KEYEVENTF_EXTENDEDKEY;

            var inp = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = (ushort)vk,
                        wScan = (ushort)scan,
                        dwFlags = flags
                    }
                }
            };
            NativeMethods.SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
        }

        private static void RunAdb(string args)
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo("adb", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            try { p.Start(); }
            catch (Exception ex) { throw new InvalidOperationException("adb could not start. Is adb on PATH? " + ex.Message, ex); }
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(3000)) throw new InvalidOperationException("adb did not finish in 3 seconds: " + args);
            if (p.ExitCode != 0) throw new InvalidOperationException($"adb {args} failed: {stderr.Result.Trim()}");
            _ = stdout.Result;
        }

        // ===== Hotkey (Ctrl+Alt+X) ===========================================
        private void RegisterStopHotkey()
        {
            if (_hotkeyRegistered) return;
            const uint MOD_CONTROL = 0x0002, MOD_ALT = 0x0001;
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(Key.X);

            if (!NativeMethods.RegisterHotKey(_ownWindow, HOTKEY_ID, MOD_CONTROL | MOD_ALT, vk))
                MessageBox.Show("Hotkey registration failed (Ctrl+Alt+X). Another app may be using it; use the Stop button.");
            else _hotkeyRegistered = true;
        }

        private void UnregisterStopHotkey()
        {
            if (!_hotkeyRegistered) return;
            NativeMethods.UnregisterHotKey(_ownWindow, HOTKEY_ID);
            _hotkeyRegistered = false;
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID) { StopPlayback(); handled = true; }
            return IntPtr.Zero;
        }

        // ===== Window search by partial title =================================
        private static IntPtr FindWindowByTitleContains(string text, IntPtr exclude)
        {
            IntPtr found = IntPtr.Zero;
            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                if (hWnd == exclude || !NativeMethods.IsWindowVisible(hWnd)) return true;
                StringBuilder sb = new(512);
                NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
                if (sb.ToString().Contains(text, StringComparison.OrdinalIgnoreCase)) { found = hWnd; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        // ===== Native interop =================================================
        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion U; }
        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }
        [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

        private const uint INPUT_MOUSE = 0;
        private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004, MOUSEEVENTF_VIRTUALDESK = 0x4000;

        private static class NativeMethods
        {
            public const int WH_MOUSE_LL = 14, WH_KEYBOARD_LL = 13;
            public const uint GA_ROOT = 2;

            [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
            public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

            [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
            public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

            [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
            public static extern bool UnhookWindowsHookEx(IntPtr hhk);

            [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
            public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

            [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
            public static extern IntPtr GetModuleHandle(string? lpModuleName);

            public static IntPtr SetMouseHook(LowLevelMouseProc proc) => SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(null), 0);
            public static IntPtr SetKeyboardHook(LowLevelKeyboardProc proc) => SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);

            [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);
            [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
            [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
            [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
            [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
            [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
            [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

            // Hotkey
            [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
            [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

            // Enum windows
            public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
            [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
            [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
            [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        }
    }
}
