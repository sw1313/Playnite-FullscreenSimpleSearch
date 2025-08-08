using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Interop;

namespace OverlaySearch
{
    public class Plugin : GenericPlugin
    {
        private static readonly ILogger log = LogManager.GetLogger();

        private const ModifierKeys HOTKEY_MODS = ModifierKeys.Control | ModifierKeys.Shift;
        private const Key HOTKEY_KEY = Key.Y;          // Ctrl-Shift-Y
        private const int HOTKEY_ID = 0x0BEE;

        private readonly IPlayniteAPI api;
        private HotkeyWnd hotWnd;
        private static bool overlayOpen = false;

        public Plugin(IPlayniteAPI api) : base(api) { this.api = api; }
        public override Guid Id => Guid.Parse("45c8ea9b-d95a-4fe7-807d-bb1f8d36d9a6");

        public override void OnApplicationStarted(OnApplicationStartedEventArgs _)
        {
            hotWnd = new HotkeyWnd((uint)HOTKEY_MODS,
                                   (uint)KeyInterop.VirtualKeyFromKey(HOTKEY_KEY),
                                   HOTKEY_ID);
            hotWnd.Pressed += ShowOverlay;
        }
        public override void OnApplicationStopped(OnApplicationStoppedEventArgs _) => hotWnd?.Dispose();

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
                    ExtendedWindowStyle = 0x00000080                  // WS_EX_TOOLWINDOW
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

            [DllImport("user32.dll")] internal static extern IntPtr GetKeyboardLayout(int idThread);
            [DllImport("user32.dll")] internal static extern IntPtr ActivateKeyboardLayout(IntPtr hkl, uint flags);
            [DllImport("user32.dll")] internal static extern IntPtr LoadKeyboardLayout(string pwszKLID, uint flags);
            [DllImport("user32.dll")] internal static extern int GetKeyboardLayoutList(int n, IntPtr[] list);

            [DllImport("user32.dll")] internal static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        }
    }
}