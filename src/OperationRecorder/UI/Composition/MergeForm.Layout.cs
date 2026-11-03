using System;
using System.Drawing;
using System.Windows.Forms;

namespace OperationRecorderApp
{
    public sealed partial class MergeForm
    {
        Button ActionButton(string name, string text, Action action)
        {
            var button = new Button { Name = name, Text = text, AutoSize = true, Margin = new Padding(3) };
            button.Click += (s, e) => action();
            mutating.Add(button);
            return button;
        }

        static FlowLayoutPanel Bar() => new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty };
        static Label Caption(string text) => new Label { Text = text, AutoSize = true, Padding = new Padding(0, 5, 0, 3) };

        void BuildLayout()
        {
            var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8) };
            shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(shell);

            var top = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4 };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            top.Controls.Add(Caption("完成する記録の名前"), 0, 0); top.Controls.Add(caption, 1, 0);
            top.Controls.Add(Caption("記録全体の説明"), 2, 0); top.Controls.Add(description, 3, 0);
            var guide = Caption("①元の記録から手順を選ぶ → ②完成順へ差し込む・並べ替える → ③採用元と画像を確認して保存・完成");
            top.Controls.Add(guide, 0, 1); top.SetColumnSpan(guide, 4);
            var warning = Caption("重複は操作の対象や順序から推定した候補です。同じ操作でも画面や入力内容が異なる場合があります。画像と文章を確認してください。");
            warning.ForeColor = Color.FromArgb(95, 75, 25);
            top.Controls.Add(warning, 0, 2); top.SetColumnSpan(warning, 4);
            shell.Controls.Add(top, 0, 0);

            var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
            columns.Controls.Add(BuildSources(), 0, 0);
            columns.Controls.Add(BuildRows(), 1, 0);
            columns.Controls.Add(BuildEditor(), 2, 0);
            shell.Controls.Add(columns, 0, 1);

            var bottom = Bar();
            undoButton = ActionButton("compositionUndo", "元に戻す", Undo);
            redoButton = ActionButton("compositionRedo", "やり直す", Redo);
            finishButton = ActionButton("compositionFinish", "完成して新しい記録を作る", Complete);
            finishButton.Font = new Font(Font, FontStyle.Bold);
            bottom.Controls.AddRange(new Control[]
            {
                undoButton, redoButton, ActionButton("compositionSave", "途中保存 (Ctrl+S)", () => SaveDraft()), finishButton,
                ActionButton("compositionClose", "閉じる", Close), summary, saveState
            });
            shell.Controls.Add(bottom, 0, 2);
            shell.Controls.Add(draftPath, 0, 3);
        }

        Control BuildSources()
        {
            var group = new GroupBox { Text = "元の記録・差し込む手順", Dock = DockStyle.Fill, Padding = new Padding(6) };
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var sourceButtons = Bar();
            sourceButtons.Controls.Add(ActionButton("compositionAddSource", "別の記録を追加…", AddSource));
            sourceButtons.Controls.Add(suggest);
            tips.SetToolTip(suggest, "OFFで取り込んだ記録は、元手順の一覧から必要な範囲を手動で差し込めます。");
            grid.Controls.Add(sourceButtons, 0, 0);
            grid.Controls.Add(sources, 0, 1);
            grid.Controls.Add(sourcePath, 0, 2);
            sourceSteps.Columns.Add("元番号", 58); sourceSteps.Columns.Add("手順 (Ctrl/Shiftで複数選択)", 260);
            grid.Controls.Add(sourceSteps, 0, 3);
            var buttons = Bar();
            buttons.Controls.AddRange(new Control[]
            {
                ActionButton("compositionInsertBefore", "選択行の前へ", () => Insert("before")),
                ActionButton("compositionInsertAfter", "選択行の後へ", () => Insert("after")),
                ActionButton("compositionAppend", "末尾へ追加", () => Insert("end"))
            });
            grid.Controls.Add(buttons, 0, 4);
            group.Controls.Add(grid);
            return group;
        }

        Control BuildRows()
        {
            var group = new GroupBox { Text = "完成する手順・順番", Dock = DockStyle.Fill, Padding = new Padding(6) };
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            rows.Columns.Add("順", 45); rows.Columns.Add("手順", 225); rows.Columns.Add("採用元 / 候補数", 155);
            grid.Controls.Add(rows, 0, 0);
            var move = Bar();
            move.Controls.AddRange(new Control[]
            {
                ActionButton("compositionMoveFirst", "先頭へ", () => MoveRows("first")),
                ActionButton("compositionMoveUp", "↑ 上へ", () => MoveRows("up")),
                ActionButton("compositionMoveDown", "↓ 下へ", () => MoveRows("down")),
                ActionButton("compositionMoveLast", "末尾へ", () => MoveRows("last"))
            });
            grid.Controls.Add(move, 0, 1);
            var include = Bar();
            include.Controls.AddRange(new Control[]
            {
                ActionButton("compositionExclude", "選択手順を除外", () => SetIncluded(false)),
                ActionButton("compositionInclude", "選択手順を採用", () => SetIncluded(true)),
                Caption("チェックを外した手順は完成記録に入れません。")
            });
            grid.Controls.Add(include, 0, 2);
            group.Controls.Add(grid);
            return group;
        }

        Control BuildEditor()
        {
            var group = new GroupBox { Text = "選択した手順の採用元・文章・画像", Dock = DockStyle.Fill, Padding = new Padding(6) };
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8 };
            for (int i = 0; i < 8; i++) grid.RowStyles.Add(new RowStyle(i == 3 || i == 6 ? SizeType.Percent : SizeType.AutoSize, i == 3 ? 24 : i == 6 ? 76 : 0));
            grid.Controls.Add(Caption("採用する元の記録 (重複候補を選び直せます)"), 0, 0);
            grid.Controls.Add(candidate, 0, 1);
            grid.Controls.Add(headline, 0, 2);
            grid.Controls.Add(body, 0, 3);
            var textButtons = Bar();
            textButtons.Controls.Add(ActionButton("compositionApplyText", "この文章を反映", ApplyText));
            textButtons.Controls.Add(ActionButton("compositionAddNote", "説明だけの手順を追加", AddNote));
            grid.Controls.Add(textButtons, 0, 4);
            grid.Controls.Add(selectedText, 0, 5);
            selectedText.Height = 35;
            var tabs = new TabControl { Name = "compositionPreviewTabs", Dock = DockStyle.Fill };
            var chosen = new TabPage("採用手順の画像");
            chosen.Controls.Add(ImagePair(before, after));
            var compare = new TabPage("重複候補を比較");
            var compareGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
            compareGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            compareGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            compareGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            compareGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            compareGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 10));
            compareGrid.Controls.Add(Caption("現在の採用元"), 0, 0);
            compareGrid.Controls.Add(ImagePair(chosenBefore, chosenAfter), 0, 1);
            compareGrid.Controls.Add(comparison, 0, 2);
            compareGrid.Controls.Add(ImagePair(compareBefore, compareAfter), 0, 3);
            compareGrid.Controls.Add(comparedText, 0, 4);
            compare.Controls.Add(compareGrid);
            tabs.TabPages.Add(chosen); tabs.TabPages.Add(compare);
            grid.Controls.Add(tabs, 0, 6);
            grid.Controls.Add(Caption("画像はダブルクリックで拡大できます。"), 0, 7);
            group.Controls.Add(grid);
            return group;
        }

        static Control ImagePair(PictureBox before, PictureBox after)
        {
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.Controls.Add(Caption("操作前"), 0, 0); grid.Controls.Add(Caption("操作後"), 1, 0);
            grid.Controls.Add(before, 0, 1); grid.Controls.Add(after, 1, 1);
            return grid;
        }
    }
}