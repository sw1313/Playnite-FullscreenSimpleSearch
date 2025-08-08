using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace OverlaySearch.Views
{
    public partial class OverlayWindow : Window
    {
        private readonly Action onClose;

        public OverlayWindow(Action onClose = null)
        {
            InitializeComponent();
            this.onClose = onClose;
            Loaded += (_, __) => SearchBox.Focus();
            Closed += (_, __) => this.onClose?.Invoke();
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            // ───────────────── 搜索框 -> 列表 ─────────────────
            if (Keyboard.FocusedElement == SearchBox &&
               (e.Key == Key.Down || e.Key == Key.PageDown))
            {
                if (GameList.Items.Count > 0)
                {
                    // 选中第 1 个并把焦点直接放到它的容器上
                    GameList.SelectedIndex = 0;
                    GameList.UpdateLayout();
                    GameList.ScrollIntoView(GameList.Items[0]);
                    var item = GameList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
                    if (item != null)
                    {
                        item.Focus(); // 下一次按↓就会移动到第二个
                    }
                    else
                    {
                        // 极少数情况下容器尚未生成：先把焦点给列表
                        GameList.Focus();
                    }

                    e.Handled = true;
                }
                return;
            }

            // ───────────────── 列表无选中时的首次↓ 安全兜底 ─────────────────
            if ((Keyboard.FocusedElement == GameList || GameList.IsKeyboardFocusWithin) &&
                (e.Key == Key.Down || e.Key == Key.PageDown) &&
                GameList.SelectedIndex < 0 && GameList.Items.Count > 0)
            {
                GameList.SelectedIndex = 0;
                GameList.UpdateLayout();
                var item = GameList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
                item?.Focus();
                e.Handled = true;
                return;
            }

            // ───────────────── 列表 -> 搜索框（在第 1 行按↑/PgUp） ─────────────────
            if ((Keyboard.FocusedElement == GameList || GameList.IsKeyboardFocusWithin) &&
               (e.Key == Key.Up || e.Key == Key.PageUp))
            {
                // 有时容器获得焦点但 SelectedIndex 还没同步，做个兜底
                int idx = GameList.SelectedIndex;
                if (idx <= 0)
                {
                    SearchBox.Focus();
                    // 把光标放到末尾，便于继续输入
                    SearchBox.CaretIndex = SearchBox.Text?.Length ?? 0;
                    e.Handled = true;
                    return;
                }
            }

            // ───────────────── 接受 / 取消 ─────────────────
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                DialogResult = true;
                Close();
                return;
            }
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                DialogResult = false;
                Close();
                return;
            }

            base.OnPreviewKeyDown(e);
        }
    }
}
