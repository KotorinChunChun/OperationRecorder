using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using OpRec;

namespace OperationRecorderApp
{
    // 「撮影の設定」タブ: 何をきっかけに撮るか(操作の種類ごとに記録するか・操作前 / 操作後を撮るか)、撮影のタイミング(ms)
    public sealed partial class MainForm
    {
        readonly Dictionary<string, CheckBox[]> ruleBoxes = new Dictionary<string, CheckBox[]>();   // [記録する, 操作前, 操作後]
        NumericUpDown numBeforeMs, numAfterMs;
        CheckBox chkOverlay, chkTargetFrame, chkFlash, chkOverlayFlee;
        CheckBox chkRecordVideo, chkRecordNarration;
        TextBox txtFfmpegPath;

        void BuildCaptureTab()
        {
            var p = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            pageCapture.Controls.Add(p);

            p.Controls.Add(Caption("記録する操作と撮る画像", true));

            var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 4, CellBorderStyle = TableLayoutPanelCellBorderStyle.Single, Margin = new Padding(3, 6, 3, 6) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 460));
            for (int i = 0; i < 3; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            foreach (var h in new[] { "操作", "記録する", "操作前を撮る", "操作後を撮る" })
                grid.Controls.Add(new Label { Text = h, AutoSize = true, Font = new Font("Yu Gothic UI", 9.5f, FontStyle.Bold), Margin = new Padding(6) });
            foreach (var kind in CaptureRules.Order)
            {
                var rule = settings.Rule(kind);
                grid.Controls.Add(new Label { Text = CaptureRules.Label(kind), AutoSize = true, Margin = new Padding(6, 8, 6, 6) });
                var boxes = new[]
                {
                    new CheckBox { Checked = rule.Record, AutoSize = true, Margin = new Padding(40, 6, 6, 6) },
                    new CheckBox { Checked = rule.Before, AutoSize = true, Margin = new Padding(40, 6, 6, 6) },
                    new CheckBox { Checked = rule.After, AutoSize = true, Margin = new Padding(40, 6, 6, 6) },
                };
                boxes[0].CheckedChanged += (s, e) => { boxes[1].Enabled = boxes[2].Enabled = boxes[0].Checked; };
                boxes[1].Enabled = boxes[2].Enabled = rule.Record;
                foreach (var b in boxes) grid.Controls.Add(b);
                ruleBoxes[kind] = boxes;
            }
            p.Controls.Add(grid);

            p.Controls.Add(Caption("撮影のタイミング", true));
            numBeforeMs = new NumericUpDown { Minimum = 0, Maximum = 1000, Increment = 50, Width = 90, Value = Math.Max(0, Math.Min(1000, settings.BeforeMs)) };
            numAfterMs = new NumericUpDown { Minimum = 0, Maximum = 5000, Increment = 100, Width = 90, Value = Math.Max(0, Math.Min(5000, settings.AfterMs)) };
            p.Controls.Add(Row(new Label { Text = "操作前: 操作した瞬間の", AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, numBeforeMs,
                new Label { Text = "ms 前の画面(大きくすると、マウスを動かす前の画面になる)", AutoSize = true, Margin = new Padding(3, 7, 3, 3) }));
            p.Controls.Add(Row(new Label { Text = "操作後: 操作が終わってから", AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, numAfterMs,
                new Label { Text = "ms 後の画面(画面の切り替わりが遅いアプリでは大きくする)", AutoSize = true, Margin = new Padding(3, 7, 3, 3) }));

            chkRecordVideo = new CheckBox { Name = nameof(chkRecordVideo), Text = "撮影範囲を録画する（MP4）", AutoSize = true, Checked = settings.RecordVideo };
            chkRecordNarration = new CheckBox { Name = nameof(chkRecordNarration), Text = "マイクを録音する（WAV）", AutoSize = true, Checked = settings.RecordNarration };
            p.Controls.Add(chkRecordVideo);
            p.Controls.Add(PathSelectionRow("FFmpegの実行ファイル", nameof(txtFfmpegPath), settings.FfmpegPath, false, out txtFfmpegPath));
            p.Controls.Add(chkRecordNarration);
            AddTranscriptionSettings(p);
        }

        Control PathSelectionRow(string label, string name, string value, bool folder, out TextBox box)
        {
            var path = new TextBox { Name = name, AccessibleName = label, Width = 430, Text = value };
            box = path;
            var choose = new Button { Text = "参照…", AccessibleName = label + "を参照", AutoSize = true };
            choose.Click += (_, _) =>
            {
                if (folder)
                {
                    using var dialog = new FolderBrowserDialog { SelectedPath = path.Text };
                    if (dialog.ShowDialog(this) == DialogResult.OK) path.Text = dialog.SelectedPath;
                }
                else
                {
                    using var dialog = new OpenFileDialog { Filter = "実行ファイル (*.exe)|*.exe", FileName = path.Text, CheckFileExists = true };
                    if (dialog.ShowDialog(this) == DialogResult.OK) path.Text = dialog.FileName;
                }
            };
            return Row(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, path, choose);
        }

        void SaveCaptureSettings()
        {
            foreach (var kv in ruleBoxes)
            {
                var r = settings.Rule(kv.Key);
                r.Record = kv.Value[0].Checked;
                r.Before = kv.Value[1].Checked;
                r.After = kv.Value[2].Checked;
            }
            settings.BeforeMs = (int)numBeforeMs.Value;
            settings.AfterMs = (int)numAfterMs.Value;
            settings.RecordVideo = chkRecordVideo.Checked;
            settings.RecordNarration = chkRecordNarration.Checked;
            settings.FfmpegPath = txtFfmpegPath.Text.Trim();
            SaveTranscriptionSettings();
            settings.ShowOverlay = chkOverlay.Checked;
            settings.OverlayFlee = chkOverlayFlee.Checked;
            overlay.FleeEnabled = settings.OverlayFlee;
            settings.ShowTargetFrame = chkTargetFrame.Checked;
            settings.FlashOnCapture = chkFlash.Checked;
        }
    }
}
