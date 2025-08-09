using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;   // DispatcherTimer

namespace OverlaySearch
{
    public class Plugin : GenericPlugin
    {
        private static readonly ILogger log = LogManager.GetLogger();

        private const ModifierKeys HOTKEY_MODS = ModifierKeys.Control | ModifierKeys.Shift;
        private const Key HOTKEY_KEY = Key.Y;          // Ctrl-Shift-Y
        private const int HOTKEY_ID = 0x0BEE;

        // === 手柄触发（LT+RT）参数 ===
        private const byte LT_THRESHOLD = 30;          // 超过阈值算“按下”
        private const byte RT_THRESHOLD = 30;
        private DispatcherTimer gpPoller;              // XInput 轮询
        private bool lastLtRtDown = false;             // 上一帧 LT+RT 是否“组合按下”

        private readonly IPlayniteAPI api;
        private HotkeyWnd hotWnd;
        private static bool overlayOpen = false;

        public Plugin(IPlayniteAPI api) : base(api) { this.api = api; }
        public override Guid Id => Guid.Parse("45c8ea9b-d95a-4fe7-807d-bb1f8d36d9a6");

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            // 键盘热键
            hotWnd = new HotkeyWnd((uint)HOTKEY_MODS,
                                   (uint)KeyInterop.VirtualKeyFromKey(HOTKEY_KEY),
                                   HOTKEY_ID);
            hotWnd.Pressed += ShowOverlay;

            // 手柄 LT+RT 轮询
            gpPoller = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            gpPoller.Tick += (s, e) => PollGamepadForHotkey();
            gpPoller.Start();
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            hotWnd?.Dispose();
            if (gpPoller != null)
            {
                gpPoller.Stop();
                gpPoller = null;
            }
        }

        private void PollGamepadForHotkey()
        {
            // 仅在 Playnite 主窗口前台时响应，避免在别的程序/游戏里误触
            var app = System.Windows.Application.Current;
            var mainWin = app?.MainWindow;
            if (mainWin == null) { lastLtRtDown = false; return; }

            IntPtr hwndMain = new WindowInteropHelper(mainWin).Handle;
            IntPtr hwndFg = Native.GetForegroundWindow();
            if (hwndFg != hwndMain) { lastLtRtDown = false; return; }

            // 读取 XInput（手柄 0）
            XSTATE st;
            if (XInputGetState(0, out st) != 0) { lastLtRtDown = false; return; } // 手柄不在线

            bool ltDown = st.Gamepad.LT > LT_THRESHOLD;
            bool rtDown = st.Gamepad.RT > RT_THRESHOLD;
            bool comboDown = ltDown && rtDown;

            // 组合上升沿：上帧未同时按下，这一帧变为同时按下 → 触发一次
            if (comboDown && !lastLtRtDown)
            {
                ShowOverlay(); // 与 Ctrl+Shift+Y 相同的行为
            }

            lastLtRtDown = comboDown;
        }

        private void ShowOverlay()
        {
            if (overlayOpen) return;
            overlayOpen = true;

            var app = System.Windows.Application.Current;
            var mainWin = app.MainWindow;
            var hwndMain = new WindowInteropHelper(mainWin).Handle;
            Guid? picked = null;

            try
            {
                // 必须在 UI 线程里创建 / 显示窗口
                app.Dispatcher.Invoke(() =>
                {
                    var vm = new OverlayVM(api);
                    var win = new Views.OverlayWindow(() => overlayOpen = false)
                    {
                        DataContext = vm,
                        Owner = mainWin   // 仅置于 Playnite 之上
                    };
                    if (win.ShowDialog() == true && vm.SelectedGame is Game g)
                        picked = g.Id;
                });
            }
            catch (Exception ex)
            {
                log.Error(ex, "[OverlaySearch] ShowOverlay crashed");
                overlayOpen = false;   // 避免死锁
            }

            if (picked == null) return;   // 用户取消

            // 后续操作仍在 UI 线程
            app.Dispatcher.InvokeAsync(async () =>
            {
                Native.SetForegroundWindow(hwndMain);

                SendKey(VK_ESCAPE);                 // 退出残留输入
                await Task.Delay(150);

                api.MainView.SelectGame(picked.Value);
                await Task.Delay(200);

                IntPtr original = Native.GetKeyboardLayout(0);
                bool switched = SwitchToUSEnglish(original);
                if (switched) await Task.Delay(50);

                SendKey(VK_A);                      // 手柄 A / 键盘 A → 详情

                if (switched)
                {
                    await Task.Delay(100);
                    Native.ActivateKeyboardLayout(original, 0);
                }
            });
        }

        // —— 输入法切换 —— //
        private static bool SwitchToUSEnglish(IntPtr current)
        {
            const uint US_ID = 0x0409;
            if (((uint)current & 0xFFFF) == US_ID) return false;

            int count = Native.GetKeyboardLayoutList(0, null);
            IntPtr[] list = new IntPtr[count];
            Native.GetKeyboardLayoutList(count, list);

            foreach (var hkl in list)
            {
                if (((uint)hkl & 0xFFFF) == US_ID)
                {
                    Native.ActivateKeyboardLayout(hkl, 0);
                    return true;
                }
            }

            const uint KLF_ACTIVATE = 0x00000001;
            const uint KLF_SETFORPROCESS = 0x00000100;
            return Native.LoadKeyboardLayout("00000409", KLF_ACTIVATE | KLF_SETFORPROCESS) != IntPtr.Zero;
        }

        // —— 键盘模拟 —— //
        private const byte VK_ESCAPE = 0x1B;
        private const byte VK_A = 0x41;
        private const uint KEYEVENTF_KEYUP = 0x02;
        private static void SendKey(byte vk)
        {
            Native.keybd_event(vk, 0, 0, UIntPtr.Zero);
            Native.keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        // —— 隐藏的热键窗口 —— //
        private sealed class HotkeyWnd : HwndSource, IDisposable
        {
            private readonly int id;
            public event Action Pressed;
            public HotkeyWnd(uint mods, uint vk, int id)
                : base(new HwndSourceParameters("OverlaySearchHotkey")
                {
                    Width = 0,
                    Height = 0,
                    WindowStyle = unchecked((int)0x80000000), // WS_DISABLED
                    ExtendedWindowStyle = 0x00000080          // WS_EX_TOOLWINDOW
                })
            {
                this.id = id;
                AddHook(WndProc);
                Native.RegisterHotKey(Handle, id, mods, vk);
            }
            private IntPtr WndProc(IntPtr h, int m, IntPtr wp, IntPtr lp, ref bool handled)
            {
                const int WM_HOTKEY = 0x0312;
                if (m == WM_HOTKEY && wp.ToInt32() == id)
                {
                    Pressed?.Invoke();
                    handled = true;
                }
                return IntPtr.Zero;
            }
            public new void Dispose()
            {
                Native.UnregisterHotKey(Handle, id);
                base.Dispose();
            }
        }

        // —— P/Invoke —— //
        private static class Native
        {
            [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
            [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr h, int id);
            [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hWnd);
            [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();

            [DllImport("user32.dll")] internal static extern IntPtr GetKeyboardLayout(int idThread);
            [DllImport("user32.dll")] internal static extern IntPtr ActivateKeyboardLayout(IntPtr hkl, uint flags);
            [DllImport("user32.dll")] internal static extern IntPtr LoadKeyboardLayout(string pwszKLID, uint flags);
            [DllImport("user32.dll")] internal static extern int GetKeyboardLayoutList(int n, IntPtr[] list);

            [DllImport("user32.dll")] internal static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        }

        // —— XInput（用于检测 LT/RT）—— //
        [DllImport("xinput1_4.dll")]
        private static extern int XInputGetState(uint dwUserIndex, out XSTATE pState);
        // 如需兼容老系统，可改为 "xinput9_1_0.dll"

        [StructLayout(LayoutKind.Sequential)]
        private struct XSTATE
        {
            public uint dwPacketNumber;
            public XGAMEPAD Gamepad;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XGAMEPAD
        {
            public ushort wButtons;
            public byte LT, RT;
            public short LX, LY, RX, RY;
        }
    }
}