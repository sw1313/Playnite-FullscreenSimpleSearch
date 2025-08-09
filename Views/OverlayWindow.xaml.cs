using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace OverlaySearch.Views
{
    public partial class OverlayWindow : Window
    {
        private readonly Action onClose;
        private DispatcherTimer padTimer;
        private ushort lastButtons;
        private bool childOpen = false;
        private bool absorbNextPadState = false;                  // 关闭子窗后吸收第一帧手柄状态
        private DateTime inputSquelchUntilUtc = DateTime.MinValue; // 关闭子窗后 200ms 消抖

        private KeyboardWindow kbOpen; // 当前打开的虚拟键盘窗

        public OverlayWindow(Action onClose = null)
        {
            InitializeComponent();
            this.onClose = onClose;
            Loaded += OnLoaded;
            Closed += delegate { if (this.onClose != null) this.onClose.Invoke(); };
        }

        internal void SetSearchText(string text)
        {
            SearchBox.Text = text;
            SearchBox.CaretIndex = SearchBox.Text != null ? SearchBox.Text.Length : 0;
        }

        public void FocusSearchBoxCaret()
        {
            SearchBox.Focus();
            SearchBox.CaretIndex = SearchBox.Text != null ? SearchBox.Text.Length : 0;
        }

        private void OnLoaded(object s, RoutedEventArgs e)
        {
            FocusSearchBoxCaret();
            padTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            padTimer.Tick += PollPad;
            padTimer.Start();
        }

        protected override void OnClosed(EventArgs e)
        {
            if (padTimer != null) padTimer.Stop();
            base.OnClosed(e);
        }

        private void PollPad(object s, EventArgs e)
        {
            if (childOpen) return;
            if (DateTime.UtcNow < inputSquelchUntilUtc) return;

            XINPUT_STATE st;
            if (!IsActive || XInputGetState(0, out st) != 0) return;

            ushort now = st.Gamepad.wButtons;
            if (absorbNextPadState) { lastButtons = now; absorbNextPadState = false; return; }

            ushort prev = lastButtons;

            bool A = (now & 0x1000) != 0 && (prev & 0x1000) == 0;
            bool B = (now & 0x2000) != 0 && (prev & 0x2000) == 0;

            if (A)
            {
                if (GameList.IsKeyboardFocusWithin) { DialogResult = true; Close(); }
                else { ShowKeyboard(); }
                lastButtons = now; return;
            }

            if (B)
            {
                DialogResult = false;
                Close();
                lastButtons = now; return;
            }

            lastButtons = now;
        }

        private void ShowKeyboard()
        {
            if (childOpen) return; // 已经开着就不重复开

            kbOpen = new KeyboardWindow();
            kbOpen.Owner = this;
            kbOpen.ShowActivated = false; // 关键：不激活子窗，IME 焦点留在 SearchBox
            childOpen = true;

            FocusSearchBoxCaret();

            kbOpen.Closed += delegate
            {
                childOpen = false;

                // 关闭后 200ms 消抖并吸收下一帧手柄，避免 B/ESC 穿透父窗
                inputSquelchUntilUtc = DateTime.UtcNow.AddMilliseconds(200);
                absorbNextPadState = true;

                kbOpen = null;
                FocusSearchBoxCaret();
            };

            kbOpen.Show(); // 非模态
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (DateTime.UtcNow < inputSquelchUntilUtc) { e.Handled = true; return; }

            // 子窗开启：仅拦截方向键/Enter/Esc，转发给虚拟键盘；其余（字母/数字/空格/Backspace）走 IME→SearchBox
            if (childOpen && kbOpen != null)
            {
                if (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down
                    || e.Key == Key.Enter || e.Key == Key.Escape)
                {
                    kbOpen.OwnerHandleKey(e.Key);
                    e.Handled = true;
                    return;
                }
            }

            if (Keyboard.FocusedElement == SearchBox && e.Key == Key.Enter)
            { ShowKeyboard(); e.Handled = true; return; }

            if (Keyboard.FocusedElement == SearchBox &&
               (e.Key == Key.Down || e.Key == Key.PageDown))
            {
                if (GameList.Items.Count > 0)
                {
                    GameList.SelectedIndex = 0;
                    GameList.UpdateLayout();
                    ListBoxItem itm = GameList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
                    if (itm != null) itm.Focus(); else GameList.Focus();
                    e.Handled = true;
                }
                return;
            }

            if ((Keyboard.FocusedElement == GameList || GameList.IsKeyboardFocusWithin) &&
               (e.Key == Key.Up || e.Key == Key.PageUp) && GameList.SelectedIndex <= 0)
            {
                FocusSearchBoxCaret();
                e.Handled = true; return;
            }

            // 仅在未打开虚拟键盘时允许 Enter/Esc 关闭 Overlay
            if (!childOpen && e.Key == Key.Enter) { e.Handled = true; DialogResult = true; Close(); return; }
            if (!childOpen && e.Key == Key.Escape) { e.Handled = true; DialogResult = false; Close(); return; }

            base.OnPreviewKeyDown(e);
        }

        // ====== 切换微软拼音(zh-CN) <-> 英文(US) ======
        public bool ToggleImeLanguage()
        {
            FocusSearchBoxCaret();

            CultureInfo zhCN = new CultureInfo("zh-CN");
            CultureInfo enUS = new CultureInfo("en-US");
            const string KLID_ZH_PINYIN = "E0200804"; // Microsoft Pinyin
            const string KLID_EN_US = "00000409";     // US Keyboard

            // 依据当前线程输入语言判断切换方向
            CultureInfo cur = InputLanguageManager.Current.CurrentInputLanguage;
            bool toChinese = cur == null || !cur.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

            // 1) 设置 WPF 线程输入语言
            try
            {
                CultureInfo target = toChinese ? zhCN : enUS;
                InputLanguageManager.Current.CurrentInputLanguage = target;
                InputLanguageManager.SetInputLanguage(SearchBox, target);
                InputMethod.SetPreferredImeState(SearchBox, toChinese ? InputMethodState.On : InputMethodState.Off);
            }
            catch { }

            // 2) Win32 激活具体布局
            try
            {
                IntPtr hkl = LoadKeyboardLayout(toChinese ? KLID_ZH_PINYIN : KLID_EN_US, KLF_ACTIVATE);
                if (hkl != IntPtr.Zero) ActivateKeyboardLayout(hkl, KLF_SETFORPROCESS);
            }
            catch { }

            FocusSearchBoxCaret();
            return toChinese;
        }

        private const uint KLF_ACTIVATE = 0x00000001;
        private const uint KLF_SETFORPROCESS = 0x00000100;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadKeyboardLayout(string pwszKLID, uint Flags);

        [DllImport("user32.dll")]
        private static extern IntPtr ActivateKeyboardLayout(IntPtr hkl, uint Flags);

        // XInput
        [DllImport("xinput1_4.dll")] private static extern int XInputGetState(uint idx, out XINPUT_STATE st);
        // 旧系统缺库可改为 "xinput9_1_0.dll"

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_STATE { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_GAMEPAD
        {
            public ushort wButtons;
            public byte bLeftTrigger, bRightTrigger;
            public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
        }
    }
}