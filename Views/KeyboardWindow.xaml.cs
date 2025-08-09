using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace OverlaySearch.Views
{
    public partial class KeyboardWindow : Window
    {
        public string ResultText { get; private set; } = "";

        private const int Rows = 5, Cols = 12;

        // 导航 & 渲染
        private TextBlock[,] cellMap;
        private TextBlock lastHL;
        private int row = 0, col = 0;
        private Brush normalBrush;

        // 状态
        private bool caps = false;       // 大小写
        private bool altLayout = false;  // 符号面板（true=符号，false=字母）

        // 轮询/去抖
        private DispatcherTimer poll;
        private ushort lastButtons;
        private bool lastLT;                          // LT 边沿
        private bool dpadHeld = false;                // D-Pad 按住
        private bool suppressNextKeyboardNav = false; // 手柄触发后吞掉下一次键盘导航
        private DateTime keyboardNavSquelchUntilUtc = DateTime.MinValue;

        // 功能键识别
        private readonly HashSet<string> funcTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Back","Done","Cancel","Shift","Caps","Space","Clear","PgUp","PgDn" };

        // 保存普通键原始字面
        private readonly Dictionary<TextBlock, string> baseFace = new Dictionary<TextBlock, string>();

        // 符号布局映射
        private readonly Dictionary<string, string> sym = new Dictionary<string, string>
        {
            ["q"] = "@",
            ["w"] = "#",
            ["e"] = "$",
            ["r"] = "%",
            ["t"] = "^",
            ["y"] = "&",
            ["u"] = "*",
            ["i"] = "(",
            ["o"] = ")",
            ["p"] = "_",
            ["a"] = "~",
            ["s"] = "`",
            ["d"] = "'",
            ["f"] = "\"",
            ["g"] = "/",
            ["h"] = "\\",
            ["j"] = "[",
            ["k"] = "]",
            ["l"] = "{",
            [";"] = "}",
            ["z"] = "+",
            ["x"] = "-",
            ["c"] = "=",
            ["v"] = "<",
            ["b"] = ">",
            ["n"] = ",",
            ["m"] = ".",
            ["-"] = ":",
            [":"] = ";",
            ["!"] = "?"
        };

        public KeyboardWindow()
        {
            InitializeComponent();

            BuildCellMap();

            foreach (TextBlock tb in GridKeys.Children.OfType<TextBlock>())
            {
                string tag = tb.Tag as string ?? "";
                if (!funcTags.Contains(tag) && !string.IsNullOrEmpty(tb.Text))
                    baseFace[tb] = tb.Text;
            }

            TextBlock any = GridKeys.Children.OfType<TextBlock>().FirstOrDefault();
            normalBrush = any != null ? any.Background : new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0));

            MoveToFirstUsable();

            poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            poll.Tick += PollPad;

            Loaded += OnLoaded;
            Closed += OnClosedInternal;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 宽度跟随 Owner 的 GameList
            Action sync = delegate
            {
                double target = 800;
                OverlayWindow ow = this.Owner as OverlayWindow;
                if (ow != null && ow.GameList != null && ow.GameList.ActualWidth > 0)
                    target = ow.GameList.ActualWidth;

                Chrome.Width = Math.Max(600, Math.Round(target));
            };
            sync();
            Dispatcher.BeginInvoke(sync, DispatcherPriority.Loaded);

            // IME 目标始终在 Owner 的 SearchBox
            OverlayWindow owner = Owner as OverlayWindow;
            if (owner != null) owner.FocusSearchBoxCaret();

            // 避免进入时 A 按键“上升沿”
            XSTATE st0;
            if (XInputGetState(0, out st0) == 0)
            {
                lastButtons = st0.Gamepad.wButtons;
                lastLT = st0.Gamepad.LT > 30;
            }

            poll.Start();
            TryEnableAcrylic(unchecked((int)0xCC101010)); // AABBGGRR

            // 安装低层键盘钩子：拦截 ←↑→↓ / Enter / Esc，避免被中文 IME 候选吞掉
            InstallNavKeyHook();
        }

        private void OnClosedInternal(object sender, EventArgs e)
        {
            if (poll != null) poll.Stop();
            UninstallNavKeyHook();
        }

        /* ---------- 供 Owner 转发键盘事件 ---------- */

        public void OwnerHandleKey(System.Windows.Input.Key key)
        {
            switch (key)
            {
                case System.Windows.Input.Key.Left: Move(-1, 0); break;
                case System.Windows.Input.Key.Right: Move(1, 0); break;
                case System.Windows.Input.Key.Up: Move(0, -1); break;
                case System.Windows.Input.Key.Down: Move(0, 1); break;
                case System.Windows.Input.Key.Enter: PressKey(); break;
                case System.Windows.Input.Key.Escape: Cancel(); break;
                default: break;
            }
        }

        /* ---------- 映射/导航 ---------- */

        // 支持 ColumnSpan/RowSpan：把控件占据的所有格子都映射到同一 TextBlock
        private void BuildCellMap()
        {
            cellMap = new TextBlock[Rows, Cols];

            foreach (TextBlock tb in GridKeys.Children.OfType<TextBlock>())
            {
                int r = Grid.GetRow(tb);
                int c = Grid.GetColumn(tb);
                int cs = Math.Max(1, Grid.GetColumnSpan(tb));
                int rs = Math.Max(1, Grid.GetRowSpan(tb)); // 兼容 RowSpan

                for (int rr = 0; rr < rs; rr++)
                {
                    for (int cc = 0; cc < cs; cc++)
                    {
                        int tr = r + rr;
                        int tc = c + cc;
                        if (tr >= 0 && tr < Rows && tc >= 0 && tc < Cols)
                            cellMap[tr, tc] = tb;
                    }
                }
            }
        }

        private static bool IsSelectable(TextBlock tb, HashSet<string> funcTags)
        {
            if (tb == null) return false;
            string tag = (tb.Tag as string) ?? "";
            if (funcTags.Contains(tag)) return true;
            if (!string.IsNullOrEmpty(tb.Text)) return true;
            return tb.Inlines != null && tb.Inlines.Count > 0;
        }

        private void MoveToFirstUsable()
        {
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                {
                    TextBlock tb = cellMap[r, c];
                    if (IsSelectable(tb, funcTags)) { row = r; col = c; SetHighlight(tb); return; }
                }
        }

        private void SetHighlight(TextBlock tb)
        {
            if (tb == lastHL) return;
            if (lastHL != null) lastHL.Background = normalBrush;
            lastHL = tb;
            if (tb != null) tb.Background = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
        }

        // 一次跳到“下一个不同控件”，不被跨列/跨行“吃格子”
        private void Move(int dx, int dy)
        {
            // 只支持水平或垂直一次移动（不走对角）
            if ((dx == 0 && dy == 0) || (dx != 0 && dy != 0)) return;

            TextBlock cur = cellMap[row, col];
            int nr = row;
            int nc = col;

            if (dx != 0) // 水平
            {
                int step = dx > 0 ? 1 : -1;
                int c = col;
                TextBlock tb = cur;

                // 跨过当前控件占据的列（ColumnSpan）
                do
                {
                    c += step;
                    if (c < 0 || c >= Cols) return; // 边界停止
                    tb = cellMap[row, c];
                } while (tb == cur);

                // 继续跨过空白/不可选
                while (c >= 0 && c < Cols && !IsSelectable(tb, funcTags))
                {
                    c += step;
                    if (c < 0 || c >= Cols) return;
                    tb = cellMap[row, c];
                    if (tb == cur) return;
                }

                nr = row;
                nc = c;
            }
            else // 垂直
            {
                int step = dy > 0 ? 1 : -1;
                int r = row;
                TextBlock tb = cur;

                // 跨过当前控件占据的行（RowSpan）
                do
                {
                    r += step;
                    if (r < 0 || r >= Rows) return;
                    tb = cellMap[r, col];
                } while (tb == cur);

                // 继续跨过空白/不可选
                while (r >= 0 && r < Rows && !IsSelectable(tb, funcTags))
                {
                    r += step;
                    if (r < 0 || r >= Rows) return;
                    tb = cellMap[r, col];
                    if (tb == cur) return;
                }

                nr = r;
                nc = col;
            }

            TextBlock next = cellMap[nr, nc];
            if (next == null || !IsSelectable(next, funcTags)) return;

            row = nr;
            col = nc;
            SetHighlight(next);
        }

        /* ---------- 手柄轮询（RB=中英；LT=符号） ---------- */
        private void PollPad(object s, EventArgs e)
        {
            XSTATE st;
            if (XInputGetState(0, out st) != 0) return;

            ushort b = st.Gamepad.wButtons, o = lastButtons;
            Func<ushort, bool> Edge = m => (b & m) != 0 && (o & m) == 0;

            bool moved = false, acted = false;

            // D-Pad：只在边沿变化时移动
            if (Edge(0x0004)) { Move(-1, 0); moved = true; } // 左
            if (Edge(0x0008)) { Move(1, 0); moved = true; }  // 右
            if (Edge(0x0001)) { Move(0, -1); moved = true; } // 上
            if (Edge(0x0002)) { Move(0, 1); moved = true; }  // 下

            // D-Pad 是否按住（用于手柄优先的键盘屏蔽）
            dpadHeld = (b & 0x000F) != 0;

            // 动作键
            if (Edge(0x1000)) { PressKey(); acted = true; }                     // A
            if (Edge(0x2000)) { Cancel(); acted = true; }                       // B
            if (Edge(0x0010)) { Done(); acted = true; }                         // Start
            if (Edge(0x4000)) { SendBackspace(); acted = true; }                // X 删除
            if (Edge(0x8000)) { Append(" "); acted = true; }                    // Y 空格
            if (Edge(0x0100)) { ClearAllByCtrlADelete(); acted = true; }        // LB 清空

            // RB：切换系统中/英输入法
            if (Edge(0x0200)) { ToggleSystemIme(); acted = true; }

            // LT：切换符号/字母面板（阈值>30；边沿）
            bool ltNow = st.Gamepad.LT > 30;
            if (ltNow && !lastLT) { ToggleSymbols(); acted = true; }
            lastLT = ltNow;

            if (moved || acted)
            {
                suppressNextKeyboardNav = true;
                keyboardNavSquelchUntilUtc = DateTime.UtcNow.AddMilliseconds(220);
            }

            lastButtons = b;
        }

        /* ---------- 键盘方向/功能（与手柄互斥） ---------- */
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            // 非激活窗通常收不到硬件键盘，这里保留以兼容某些激活场景
            if (dpadHeld &&
               (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down ||
                e.Key == Key.Enter || e.Key == Key.Escape))
            { e.Handled = true; return; }

            if (DateTime.UtcNow < keyboardNavSquelchUntilUtc &&
               (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down ||
                e.Key == Key.Enter || e.Key == Key.Escape))
            { e.Handled = true; return; }

            if (suppressNextKeyboardNav &&
               (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down ||
                e.Key == Key.Enter || e.Key == Key.Escape))
            { e.Handled = true; suppressNextKeyboardNav = false; return; }

            switch (e.Key)
            {
                case Key.Left: Move(-1, 0); e.Handled = true; return;
                case Key.Right: Move(1, 0); e.Handled = true; return;
                case Key.Up: Move(0, -1); e.Handled = true; return;
                case Key.Down: Move(0, 1); e.Handled = true; return;

                case Key.Enter: PressKey(); e.Handled = true; return;
                case Key.Space: Append(" "); e.Handled = true; return;
                case Key.Back: SendBackspace(); e.Handled = true; return;

                case Key.LeftShift:
                case Key.RightShift:
                    ToggleSystemIme(); e.Handled = true; return;

                case Key.Escape: Cancel(); e.Handled = true; return;
            }

            base.OnPreviewKeyDown(e);
        }

        /* ---------- 输入/功能 ---------- */
        private TextBlock CurrentCell() { return cellMap[row, col]; }

        private void SendBackspace() { TapVK(VK_BACK); }

        private void PressKey()
        {
            TextBlock tb = CurrentCell();
            if (tb == null) return;

            string tag = tb.Tag as string ?? "";
            string t = tb.Text ?? "";

            switch (tag)
            {
                case "Back": SendBackspace(); return;
                case "Done": Done(); return;
                case "Cancel": Cancel(); return;
                case "Shift": ToggleSystemIme(); return; // ⇧：切换中/英（与 RB 一致）
                case "Caps": ToggleCaps(); return;       // ⇪：大小写
                case "Space": Append(" "); return;
                case "Clear": ClearAllByCtrlADelete(); return;
                case "PgUp": TapVK(VK_PRIOR); return;   // Page Up
                case "PgDn": TapVK(VK_NEXT); return;   // Page Down
                default:
                    if (!string.IsNullOrEmpty(t)) Append(t);
                    return;
            }
        }

        private void Append(string s)
        {
            if (!altLayout && caps) s = s.ToUpperInvariant();
            TypeIntoFocused(s);
        }

        private void ToggleCaps()
        {
            caps = !caps;
            RefreshFaces();
        }

        private void ToggleSymbols()
        {
            altLayout = !altLayout;
            RefreshFaces();
        }

        private void ToggleSystemIme()
        {
            OverlayWindow ow = Owner as OverlayWindow;
            if (ow != null)
            {
                ow.ToggleImeLanguage();
                ow.FocusSearchBoxCaret();
            }
        }

        private void RefreshFaces()
        {
            foreach (KeyValuePair<TextBlock, string> kv in baseFace)
            {
                TextBlock tb = kv.Key;
                string face = kv.Value;

                if (altLayout)
                {
                    string v;
                    tb.Text = sym.TryGetValue(face, out v) ? v : face;
                }
                else
                {
                    tb.Text = caps ? face.ToUpper() : face.ToLower();
                }
            }
        }

        private void Done() { Close(); }
        private void Cancel() { Close(); }

        /* ---------- 真实输入：SendInput ---------- */

        [DllImport("user32.dll")]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public INPUTUNION U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL, wParamH;
        }

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const uint KEYEVENTF_SCANCODE = 0x0008;

        private const ushort VK_BACK = 0x08;
        private const ushort VK_SPACE = 0x20;
        private const ushort VK_DELETE = 0x2E;
        private const ushort VK_CONTROL = 0x11;
        private const ushort VK_SHIFT = 0x10;
        private const ushort VK_PRIOR = 0x21; // Page Up
        private const ushort VK_NEXT = 0x22; // Page Down

        private static readonly int INPUT_SIZE = Marshal.SizeOf(typeof(INPUT));

        private static void KeyDownVK(ushort vk)
        {
            INPUT input = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION
                {
                    ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = 0, time = 0, dwExtraInfo = IntPtr.Zero }
                }
            };
            SendInput(1, new INPUT[] { input }, INPUT_SIZE);
        }

        private static void KeyUpVK(ushort vk)
        {
            INPUT input = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION
                {
                    ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = KEYEVENTF_KEYUP, time = 0, dwExtraInfo = IntPtr.Zero }
                }
            };
            SendInput(1, new INPUT[] { input }, INPUT_SIZE);
        }

        private static void TapVK(ushort vk, bool withShift)
        {
            if (withShift) KeyDownVK(VK_SHIFT);
            KeyDownVK(vk);
            KeyUpVK(vk);
            if (withShift) KeyUpVK(VK_SHIFT);
        }

        private static void TapVK(ushort vk)
        {
            TapVK(vk, false);
        }

        private static void SendUnicodeChar(char ch)
        {
            INPUT down = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION
                {
                    ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE, time = 0, dwExtraInfo = IntPtr.Zero }
                }
            };
            INPUT up = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION
                {
                    ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP, time = 0, dwExtraInfo = IntPtr.Zero }
                }
            };
            SendInput(1, new INPUT[] { down }, INPUT_SIZE);
            SendInput(1, new INPUT[] { up }, INPUT_SIZE);
        }

        private static void TapCtrlPlus(ushort vk)
        {
            KeyDownVK(VK_CONTROL);
            TapVK(vk);
            KeyUpVK(VK_CONTROL);
        }

        private static void ClearAllByCtrlADelete()
        {
            // Ctrl+A 选中，再 Delete 清空（让控件/IME自己处理）
            TapCtrlPlus((ushort)('A'));
            TapVK(VK_DELETE);
        }

        private static void TypeIntoFocused(string s)
        {
            foreach (char ch in s)
            {
                if (ch == ' ')
                {
                    TapVK(VK_SPACE);
                    continue;
                }

                // 字母：用 VK + (可选)Shift —— 这样 IME 能接管拼音组合
                if (ch >= 'a' && ch <= 'z')
                {
                    ushort vk = (ushort)('A' + (ch - 'a')); // VK_A..VK_Z
                    TapVK(vk, false);
                    continue;
                }
                if (ch >= 'A' && ch <= 'Z')
                {
                    ushort vkUpper = (ushort)ch; // VK_A..VK_Z
                    TapVK(vkUpper, true);
                    continue;
                }

                // 数字：用 VK_0..VK_9
                if (ch >= '0' && ch <= '9')
                {
                    ushort vkDigit = (ushort)ch; // VK_0..VK_9 与 ASCII 对齐
                    TapVK(vkDigit);
                    continue;
                }

                // 其它符号：直接送 Unicode（绕过 IME 组合，直接提交）
                SendUnicodeChar(ch);
            }
        }

        /* ---------- XInput & Acrylic ---------- */
        [DllImport("xinput1_4.dll")] private static extern int XInputGetState(uint i, out XSTATE s);
        // 如缺库可改为 xinput9_1_0.dll

        [StructLayout(LayoutKind.Sequential)]
        private struct XSTATE { public uint dwPacketNumber; public XGAMEPAD Gamepad; }

        [StructLayout(LayoutKind.Sequential)]
        private struct XGAMEPAD
        {
            public ushort wButtons;
            public byte LT, RT;
            public short LX, LY, RX, RY;
        }

        private void TryEnableAcrylic(int argbAABBGGRR)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (!SetAccent(hwnd, 4, argbAABBGGRR)) SetAccent(hwnd, 3, argbAABBGGRR);
            }
            catch { }
        }

        private static bool SetAccent(IntPtr hwnd, int state, int gradientColor)
        {
            ACCENT_POLICY accent = new ACCENT_POLICY { AccentState = state, AccentFlags = 2, GradientColor = gradientColor };
            int size = Marshal.SizeOf(typeof(ACCENT_POLICY));
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                WINDOWCOMPOSITIONATTRIBDATA data = new WINDOWCOMPOSITIONATTRIBDATA
                {
                    Attribute = WINDOWCOMPOSITIONATTRIB.WCA_ACCENT_POLICY,
                    Data = ptr,
                    SizeOfData = size
                };
                return SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ACCENT_POLICY { public int AccentState, AccentFlags, GradientColor, AnimationId; }

        private enum WINDOWCOMPOSITIONATTRIB { WCA_UNDEFINED = 0, WCA_ACCENT_POLICY = 19 }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWCOMPOSITIONATTRIBDATA
        { public WINDOWCOMPOSITIONATTRIB Attribute; public IntPtr Data; public int SizeOfData; }

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);

        /* ================= 低层键盘钩子：拦截导航键，避开 IME ================= */

        private static IntPtr _hookId = IntPtr.Zero;
        private static LowLevelKeyboardProc _hookProc = HookCallback;
        private static KeyboardWindow _hookTarget; // 当前需要拦截导航键的窗口

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        private const int VK_LEFT = 0x25;
        private const int VK_UP = 0x26;
        private const int VK_RIGHT = 0x27;
        private const int VK_DOWN = 0x28;
        private const int VK_RETURN = 0x0D;
        private const int VK_ESCAPE = 0x1B;

        private void InstallNavKeyHook()
        {
            try
            {
                if (_hookId != IntPtr.Zero) return;
                _hookTarget = this;
                IntPtr hMod = GetModuleHandle(Process.GetCurrentProcess().MainModule.ModuleName);
                _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, hMod, 0);
            }
            catch { _hookId = IntPtr.Zero; }
        }

        private void UninstallNavKeyHook()
        {
            try
            {
                if (_hookId != IntPtr.Zero) UnhookWindowsHookEx(_hookId);
            }
            catch { }
            finally
            {
                _hookId = IntPtr.Zero;
                _hookTarget = null;
            }
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
                {
                    KBDLLHOOKSTRUCT data = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                    int vk = (int)data.vkCode;

                    KeyboardWindow target = _hookTarget;
                    if (target != null && target.IsVisible)
                    {
                        OverlayWindow owner = target.Owner as OverlayWindow;
                        bool ownerActive = owner != null && owner.IsActive;

                        if (ownerActive &&
                            (vk == VK_LEFT || vk == VK_RIGHT || vk == VK_UP || vk == VK_DOWN || vk == VK_RETURN || vk == VK_ESCAPE))
                        {
                            // 把导航键交给虚拟键盘，并吞掉它，避免被 IME 候选框抢走
                            Key routed = KeyInterop.KeyFromVirtualKey(vk);
                            target.Dispatcher.BeginInvoke(new Action<Key>(target.OwnerHandleKey), DispatcherPriority.Send, routed);
                            return new IntPtr(1); // 吞键
                        }
                    }
                }
            }
            catch { }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }
    }
}