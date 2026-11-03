using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using OpRec;

namespace OperationRecorderApp
{
    // 「ショートカットキー」タブ: 記録の操作キーを、修飾キー 4 つ(チェックボックス)と通常キー(コンボボックス)で決める。
    // 誤りの判定は HotkeySet.Validate、表示の文字は UiTexts にあり、ここは値の受け渡しだけ
    public sealed partial class MainForm
    {
        sealed class HotkeyRow { public CheckBox Ctrl, Shift, Alt, Win; public ComboBox Key; }

        readonly Dictionary<HotkeyAction, HotkeyRow> hotkeyRows = new Dictionary<HotkeyAction, HotkeyRow>();
        Label lblHotkeyResult;
        bool loadingHotkeys;

        void BuildHotkeysTab()
        {
            var p = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            pageHotkeys.Controls.Add(p);
            p.Controls.Add(Caption("記録の操作キー", true));

            var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 6, CellBorderStyle = TableLayoutPanelCellBorderStyle.Single, Margin = new Padding(3, 6, 3, 6) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
            for (int i = 0; i < 4; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
            foreach (var h in new[] { "操作", "Ctrl", "Shift", "Alt", "Win", "キー" })
                grid.Controls.Add(new Label { Text = h, AutoSize = true, Font = new Font("Yu Gothic UI", 9.5f, FontStyle.Bold), Margin = new Padding(6) });

            foreach (var a in HotkeySet.Order)
            {
                grid.Controls.Add(new Label { Text = HotkeySet.Label(a), AutoSize = true, Margin = new Padding(6, 9, 6, 6) });
                var row = new HotkeyRow
                {
                    Ctrl = ModifierBox(), Shift = ModifierBox(), Alt = ModifierBox(), Win = ModifierBox(),
                    Key = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140, Margin = new Padding(6, 5, 6, 5), MaxDropDownItems = 20 },
                };
                row.Key.Items.AddRange(HotkeyKeys.All);
                foreach (Control c in new Control[] { row.Ctrl, row.Shift, row.Alt, row.Win, row.Key }) grid.Controls.Add(c);
                row.Ctrl.CheckedChanged += (s, e) => OnHotkeyEdited();
                row.Shift.CheckedChanged += (s, e) => OnHotkeyEdited();
                row.Alt.CheckedChanged += (s, e) => OnHotkeyEdited();
                row.Win.CheckedChanged += (s, e) => OnHotkeyEdited();
                row.Key.SelectedIndexChanged += (s, e) => OnHotkeyEdited();
                hotkeyRows[a] = row;
            }

            // 画面キャプチャ(Ctrl+Shift だけを押して離す)は変えられないので、参考として同じ表に載せる
            grid.Controls.Add(new Label { Text = "画面キャプチャ(手動)", AutoSize = true, Margin = new Padding(6, 9, 6, 6), ForeColor = Color.DimGray });
            foreach (bool on in new[] { true, true, false, false }) grid.Controls.Add(new CheckBox { Checked = on, Enabled = false, AutoSize = true, Margin = new Padding(28, 8, 6, 6) });
            grid.Controls.Add(new Label { Text = "(なし・変更不可)", AutoSize = true, Margin = new Padding(6, 9, 6, 6), ForeColor = Color.DimGray });
            p.Controls.Add(grid);

            lblHotkeyResult = new Label { AutoSize = true, Margin = new Padding(3, 6, 3, 3), MaximumSize = new Size(900, 0) };
            p.Controls.Add(lblHotkeyResult);
            LoadHotkeyRows(settings.Hotkeys);
        }

        static CheckBox ModifierBox() => new CheckBox { AutoSize = true, Margin = new Padding(28, 8, 6, 6) };

        void LoadHotkeyRows(HotkeySet keys)
        {
            loadingHotkeys = true;
            foreach (var a in HotkeySet.Order)
            {
                var k = keys.Get(a); var row = hotkeyRows[a];
                row.Ctrl.Checked = k.Ctrl; row.Shift.Checked = k.Shift; row.Alt.Checked = k.Alt; row.Win.Checked = k.Win;
                row.Key.SelectedItem = HotkeyKeys.All.FirstOrDefault(i => i.Vk == k.Key);
            }
            loadingHotkeys = false;
            ShowHotkeyResult(null);
        }

        HotkeySet HotkeysFromRows()
        {
            var set = new HotkeySet();
            foreach (var a in HotkeySet.Order)
            {
                var row = hotkeyRows[a];
                set.Set(a, new Hotkey(row.Ctrl.Checked, row.Shift.Checked, row.Alt.Checked, row.Win.Checked, (row.Key.SelectedItem as HotkeyKeys.Item)?.Vk ?? 0));
            }
            return set;
        }

        // 変えるたびに確かめ、使える組み合わせならすぐ保存して反映する(使えなければ、前の設定のまま理由を出す)
        void OnHotkeyEdited()
        {
            if (loadingHotkeys) return;
            var set = HotkeysFromRows();
            var errors = set.Validate();
            if (errors.Count > 0) { ShowHotkeyResult(errors); return; }
            settings.Hotkeys = set;
            try { settings.Save(); } catch { }
            controller.ReloadHotkeys();
            RefreshHotkeyTexts();
            ShowHotkeyResult(null);
        }

        void ShowHotkeyResult(List<string> errors)
        {
            if (errors != null && errors.Count > 0)
            {
                lblHotkeyResult.ForeColor = Color.Firebrick;
                lblHotkeyResult.Text = "この組み合わせは使えません(いまは前の設定のままです):\n・" + string.Join("\n・", errors);
            }
            else if (controller != null && controller.HotkeyError != null)
            {
                lblHotkeyResult.ForeColor = Color.Firebrick;
                lblHotkeyResult.Text = controller.HotkeyError;
            }
            else
            {
                lblHotkeyResult.ForeColor = Color.DarkGreen;
                lblHotkeyResult.Text = "使用中: " + string.Join(" / ", HotkeySet.Order.Select(a => HotkeySet.Label(a) + " = " + settings.Hotkeys.Get(a)));
            }
        }
    }
}
