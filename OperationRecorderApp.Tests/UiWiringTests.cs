using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using OpRec;
using Xunit;

namespace OperationRecorderApp.Tests
{
    // 実コントロールのイベントをSTAで呼び、確認・OS副作用だけを差し替える。実デスクトップは操作しない。
    [Collection("管理画面の隔離設定")]
    public sealed class UiWiringTests
    {
        [Fact]
        public void 撮影規則とタイミング表示マーカー出力設定を画面から保存して復元する()
        {
            RunSta(() => WithMain((form, sample, interaction) =>
            {
                Named<TextBox>(form, "txtCaption").Text = "  設定の往復  ";
                Named<TextBox>(form, "txtDescription").Text = "  解説\r\n複数行  ";
                Named<TextBox>(form, "txtOutDir").Text = Path.Combine(sample.Root, "custom-records");
                Named<TextBox>(form, "txtReportDir").Text = Path.Combine(sample.Root, "custom-reports");
                Named<NumericUpDown>(form, "numBeforeMs").Value = 650;
                Named<NumericUpDown>(form, "numAfterMs").Value = 1700;
                Named<NumericUpDown>(form, "numRadius").Value = 37;
                foreach (string name in new[] { "chkOverlay", "chkOverlayFlee", "chkTargetFrame", "chkFlash", "chkMarkCircle", "chkMarkFrame" })
                    Named<CheckBox>(form, name).Checked = false;
                foreach (string name in new[] { "chkMarkPointer", "chkMarkArrow", "chkOutMd", "chkOutHtml", "chkOpen", "chkOpenHtml", "chkOpenFolder" })
                    Named<CheckBox>(form, name).Checked = true;
                Named<RadioButton>(form, "rbEmbed").Checked = true;
                var templates = Named<CheckedListBox>(form, "clbTemplates");
                for (int i = 0; i < templates.Items.Count; i++) templates.SetItemChecked(i, i % 2 == 0);
                int ruleIndex = 0;
                foreach (string kind in CaptureRules.Order)
                {
                    Named<CheckBox>(form, "captureRule." + kind + ".0").Checked = ruleIndex % 2 == 0;
                    Named<CheckBox>(form, "captureRule." + kind + ".1").Checked = ruleIndex % 3 == 0;
                    Named<CheckBox>(form, "captureRule." + kind + ".2").Checked = ruleIndex % 3 == 1;
                    Assert.Equal(ruleIndex % 2 == 0, Named<CheckBox>(form, "captureRule." + kind + ".1").Enabled);
                    ruleIndex++;
                }
                Invoke(form, "SaveRecordSettings");
                var saved = AppSettings.FromJson(File.ReadAllText(Path.Combine(AppSettings.SettingsDir, "settings.json")));
                Assert.Equal("設定の往復", saved.Caption);
                Assert.Equal("解説\r\n複数行", saved.Description);
                Assert.Equal(Path.Combine(sample.Root, "custom-records"), saved.OutDir);
                Assert.Equal(Path.Combine(sample.Root, "custom-reports"), saved.ReportDir);
                Assert.Equal(650, saved.BeforeMs);
                Assert.Equal(1700, saved.AfterMs);
                Assert.Equal(37, saved.MarkerRadius);
                Assert.True(saved.EmbedImages && saved.MarkPointer && saved.MarkArrow && saved.OutputMd && saved.OutputHtml && saved.OpenAfterSave && saved.OpenHtmlAfterSave && saved.OpenFolderAfterSave);
                Assert.False(saved.ShowOverlay || saved.OverlayFlee || saved.ShowTargetFrame || saved.FlashOnCapture || saved.MarkCircle || saved.MarkFrame);
                ruleIndex = 0;
                foreach (string kind in CaptureRules.Order)
                {
                    Assert.Equal(ruleIndex % 2 == 0, saved.Rule(kind).Record);
                    Assert.Equal(ruleIndex % 3 == 0, saved.Rule(kind).Before);
                    Assert.Equal(ruleIndex % 3 == 1, saved.Rule(kind).After);
                    ruleIndex++;
                }
                using var restored = new MainForm(false, true, interaction, UniquePrefix());
                Assert.Equal(saved.Caption, Named<TextBox>(restored, "txtCaption").Text);
                Assert.Equal(saved.BeforeMs, Named<NumericUpDown>(restored, "numBeforeMs").Value);
                Assert.Equal(saved.AfterMs, Named<NumericUpDown>(restored, "numAfterMs").Value);
                Assert.Equal(saved.MarkerRadius, Named<NumericUpDown>(restored, "numRadius").Value);
                Assert.True(Named<RadioButton>(restored, "rbEmbed").Checked);
                foreach (string kind in CaptureRules.Order)
                {
                    Assert.Equal(saved.Rule(kind).Record, Named<CheckBox>(restored, "captureRule." + kind + ".0").Checked);
                    Assert.Equal(saved.Rule(kind).Before, Named<CheckBox>(restored, "captureRule." + kind + ".1").Checked);
                    Assert.Equal(saved.Rule(kind).After, Named<CheckBox>(restored, "captureRule." + kind + ".2").Checked);
                }
                CloseMain(restored);
            }));
        }

        [Theory]
        [InlineData("rbWindow", "Window", "rbPick", "pick")]
        [InlineData("rbWindow", "Window", "rbForeground", "foreground")]
        [InlineData("rbApp", "App", "rbChoose", "list")]
        [InlineData("rbDesktop", "Desktop", "rbPick", "pick")]
        [InlineData("rbDesktop", "Desktop", "rbForeground", "foreground")]
        [InlineData("rbActive", "Active", "rbPick", "pick")]
        [InlineData("rbAllDesktops", "AllDesktops", "rbPick", "pick")]
        public void 各撮影モードと対象方式を保存する(string modeControl, string mode, string targetControl, string target)
        {
            RunSta(() => WithMain((form, sample, interaction) =>
            {
                Named<RadioButton>(form, modeControl).Checked = true;
                Named<RadioButton>(form, targetControl).Checked = true;
                Named<CheckBox>(form, "chkAllSame").Checked = true;
                if (mode == "Desktop") Named<ComboBox>(form, "cmbMonitors").SelectedIndex = target == "foreground" ? 1 : 0;
                if (target == "list")
                {
                    var type = typeof(MainForm).GetNestedType("WindowItem", Private);
                    object window = Activator.CreateInstance(type, true);
                    type.GetField("Title").SetValue(window, "テスト対象");
                    type.GetField("Process").SetValue(window, "fixture");
                    type.GetField("Hwnd").SetValue(window, new IntPtr(1234));
                    var combo = Named<ComboBox>(form, "cmbWindows");
                    combo.Items.Add(window); combo.SelectedItem = window;
                }
                Invoke(form, "SaveRecordSettings");
                var settings = AppSettings.FromJson(File.ReadAllText(Path.Combine(AppSettings.SettingsDir, "settings.json")));
                Assert.Equal(mode, settings.Mode);
                Assert.Equal(target, settings.Target);
                Assert.Equal(mode == "App" && target == "list", Named<CheckBox>(form, "chkAllSame").Enabled);
                if (target == "list") { Assert.Equal("テスト対象", settings.WindowTitle); Assert.Equal("fixture", settings.ProcessName); Assert.True(settings.AllSameProcess); }
            }));
        }

        [Fact]
        public void 出力しない形式は開く指定を解除し再有効化できる()
        {
            RunSta(() => WithMain((form, sample, interaction) =>
            {
                foreach (var pair in new[] { ("chkOutMd", "chkOpen"), ("chkOutHtml", "chkOpenHtml") })
                {
                    var create = Named<CheckBox>(form, pair.Item1); var open = Named<CheckBox>(form, pair.Item2);
                    create.Checked = true; open.Checked = true; create.Checked = false;
                    Assert.False(open.Enabled); Assert.False(open.Checked);
                    create.Checked = true; Assert.True(open.Enabled); Assert.False(open.Checked);
                }
            }));
        }

        [Fact]
        public void 操作キーの画面変更は有効な値だけ保存してメニュー表示へ反映する()
        {
            RunSta(() => WithMain((form, sample, interaction) =>
            {
                var settings = Field<AppSettings>(form, "settings");
                var rows = (System.Collections.IDictionary)Field<object>(form, "hotkeyRows");
                object row = rows[HotkeyAction.Start];
                var key = (ComboBox)row.GetType().GetField("Key").GetValue(row);
                key.SelectedItem = HotkeyKeys.All.Single(item => item.Vk == HotkeyKeys.Parse("F8"));
                Assert.Equal(HotkeyKeys.Parse("F8"), settings.Hotkeys.Start.Key);
                Assert.Contains("F8", Field<ToolStripMenuItem>(form, "miStart").Text);
                Assert.Equal(HotkeyKeys.Parse("F8"), AppSettings.FromJson(File.ReadAllText(Path.Combine(AppSettings.SettingsDir, "settings.json"))).Hotkeys.Start.Key);
                // Undoと重なる値は警告を表示して前回保存値を維持する。
                key.SelectedItem = HotkeyKeys.All.Single(item => item.Vk == settings.Hotkeys.Undo.Key);
                Assert.Equal(HotkeyKeys.Parse("F8"), settings.Hotkeys.Start.Key);
                Assert.Contains("同じキー", Field<Label>(form, "lblHotkeyResult").Text);
                Assert.Contains("F8", Field<ToolStripMenuItem>(form, "miStart").Text);
                key.SelectedItem = HotkeyKeys.All.Single(item => item.Vk == HotkeyKeys.Parse("F8"));
            }));
        }

        [Fact]
        public void 記録名解説と手順文章を保存し自動文章へ戻して出力する()
        {
            RunSta(() => WithMain((form, sample, interaction) =>
            {
                SelectSession(form, sample);
                Named<TextBox>(form, "txtSesCaption").Text = "新しい見出し";
                Named<TextBox>(form, "txtSesDescription").Text = "新しい解説";
                Click(Named<Button>(form, "btnSaveMeta"));
                var metadata = JsonNode.Parse(File.ReadAllText(Path.Combine(sample.Dir, "session.json")));
                Assert.Equal("新しい見出し", (string)metadata["caption"]); Assert.Equal("新しい解説", (string)metadata["description"]);
                Named<TextBox>(form, "txtStepHeadline").Text = "修正した手順";
                Named<TextBox>(form, "txtStepBody").Text = "修正した本文\r\n追記";
                Click(Named<Button>(form, "btnSaveStep"));
                Assert.Equal("修正した手順", ReportGenerator.ListSteps(sample.Dir)[0].Headline);
                Click(Named<Button>(form, "btnResetStep"));
                Assert.Equal(ReportGenerator.ListSteps(sample.Dir)[0].OriginalHeadline, ReportGenerator.ListSteps(sample.Dir)[0].Headline);
                var templates = Named<CheckedListBox>(form, "clbSesTemplates");
                for (int i = 0; i < templates.Items.Count; i++) templates.SetItemChecked(i, ((OutputChoices.TemplateItem)templates.Items[i]).Name == "user");
                Named<RadioButton>(form, "rbSesEmbed").Checked = true;
                Click(Named<Button>(form, "btnOutMd"));
                Click(Named<Button>(form, "btnOutHtml"));
                Assert.Equal(2, interaction.Opened.Count);
                Assert.Contains("新しい見出し", File.ReadAllText(interaction.Opened[0]));
                Assert.Contains("data:image/png;base64,", File.ReadAllText(interaction.Opened[1]));
                Assert.Empty(interaction.Messages);
            }));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void 手順削除の確認取消と画像移動失敗でも手順削除を続ける既存契約を検証する(bool confirm, bool fail)
        {
            RunSta(() => WithMain((form, sample, interaction) =>
            {
                SelectSession(form, sample);
                string events = File.ReadAllText(Path.Combine(sample.Dir, "events.jsonl"));
                interaction.Answers.Enqueue(confirm ? DialogResult.Yes : DialogResult.No); interaction.FailRecycle = fail;
                Click(Named<Button>(form, "btnDeleteStep"));
                // 画像の移動は既存エンジンのbest effort。失敗時も手順を削除し、元画像を残す。
                Assert.Equal(confirm ? 3 : 4, ReportGenerator.ListSteps(sample.Dir).Count);
                Assert.Equal(confirm ? 2 : 0, interaction.RecycledFiles.Count);
                if (!confirm) Assert.Equal(events, File.ReadAllText(Path.Combine(sample.Dir, "events.jsonl")));
                if (fail) Assert.True(File.Exists(sample.ImagePath(1, "before")));
            }));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void 記録削除の確認取消成功失敗をOSごみ箱へ触れず処理する(bool confirm, bool fail)
        {
            RunSta(() => WithMain((form, sample, interaction) =>
            {
                SelectSession(form, sample);
                interaction.Answers.Enqueue(confirm ? DialogResult.Yes : DialogResult.No); interaction.FailRecycle = fail;
                Click(Named<Button>(form, "btnDeleteSession"));
                Assert.Equal(!confirm || fail, Directory.Exists(sample.Dir));
                Assert.Equal(confirm ? 1 : 0, interaction.RecycledDirectories.Count);
                if (fail) Assert.Contains(interaction.Messages, m => m.Contains("削除できませんでした"));
            }));
        }

        [Fact]
        public void 保存出力と外部起動の失敗を確認口へ返す()
        {
            RunSta(() => WithMain((form, sample, interaction) =>
            {
                SelectSession(form, sample);
                string metadata = Path.Combine(sample.Dir, "session.json"); File.WriteAllText(metadata, "{");
                Click(Named<Button>(form, "btnSaveMeta"));
                Assert.Contains(interaction.Messages, m => m.Contains("保存できませんでした"));
                var templates = Named<CheckedListBox>(form, "clbSesTemplates");
                for (int i = 0; i < templates.Items.Count; i++) templates.SetItemChecked(i, false);
                Click(Named<Button>(form, "btnOutMd"));
                Assert.Contains(interaction.Messages, m => m.Contains("1 つ以上"));
                templates.SetItemChecked(0, true);
                Click(Named<Button>(form, "btnOutHtml"));
                Assert.Contains(interaction.Messages, m => m.Contains("出力できませんでした"));
                interaction.FailOpen = true; Click(Named<Button>(form, "openManual"));
                Assert.Contains(interaction.Messages, m => m.Contains("開けませんでした"));
            }));
        }

        [Fact]
        public void ヘルプ設定フォルダ参照と合成再開取消を差替え口から処理する()
        {
            RunSta(() => WithMain((form, sample, interaction) =>
            {
                Assert.Contains("記録", Named<RichTextBox>(form, "manualText").Text);
                Click(Named<Button>(form, "openManual"));
                Assert.EndsWith("MANUAL.md", interaction.Opened.Single());
                string templateDir = Path.Combine(sample.Root, "custom-templates"); Directory.CreateDirectory(templateDir);
                TemplateStore.Create(templateDir, "専用", "# {{caption}}\n");
                Named<TextBox>(form, "templateDirectory").Text = templateDir;
                Click(Named<Button>(form, "applyTemplateDirectory"));
                Assert.Single(Named<CheckedListBox>(form, "clbTemplates").Items.Cast<object>());
                Assert.Single(Named<CheckedListBox>(form, "clbSesTemplates").Items.Cast<object>());
                Assert.Equal(templateDir, AppSettings.FromJson(File.ReadAllText(Path.Combine(AppSettings.SettingsDir, "settings.json"))).TemplateDir);
                Click(Named<Button>(form, "openTemplateDirectory")); Assert.Equal(templateDir, interaction.Opened.Last());
                Assert.Equal(templateDir, Invoke(form, "BrowseFolder", form, "old", "説明"));
                interaction.FolderResult = null; Assert.Null(Invoke(form, "BrowseFolder", form, "old", "説明"));
                Click(Named<Button>(form, "resumeComposition")); Assert.Equal(1, interaction.OpenFileRequests);
                Assert.False(Named<Button>(form, "installShortcuts").Enabled);
                Assert.False(Named<Button>(form, "uninstallShortcuts").Enabled);
                Assert.False(Named<Button>(form, "restartAsAdmin").Enabled);
                Invoke(form, "RestartAsAdmin"); Assert.Equal(0, interaction.Elevations);
                Assert.Contains(interaction.Messages, m => m.Contains("隔離した実行"));
            }));
        }

        [Fact]
        public void 保存後に指定した成果物だけを開く()
        {
            RunSta(() => WithMain((form, sample, interaction) =>
            {
                var settings = Field<AppSettings>(form, "settings"); settings.OutputMd = true; settings.OpenAfterSave = true;
                settings.OutputHtml = false; settings.OpenHtmlAfterSave = true; settings.OpenFolderAfterSave = true;
                Invoke(form, "OnSaved", sample.Dir, new List<string> { "one.md", "two.md" }, new List<string> { "unused.html" });
                Assert.Equal(new[] { "one.md", "two.md", sample.Dir }, interaction.Opened);
            }));
        }

        [Fact]
        public void テンプレートフォルダの反映失敗は以前の設定を保ち警告を返す()
        {
            RunSta(() => WithMain((form, sample, interaction) =>
            {
                var settings = Field<AppSettings>(form, "settings"); string previous = settings.TemplateDir;
                string oldDirectory = AppSettings.SettingsDirectoryOverride;
                string blocked = Path.Combine(sample.Root, "settings-blocker"); File.WriteAllText(blocked, "保存先をファイルで塞ぐ");
                try
                {
                    AppSettings.SettingsDirectoryOverride = blocked;
                    Named<TextBox>(form, "templateDirectory").Text = Path.Combine(sample.Root, "new-template-dir");
                    Click(Named<Button>(form, "applyTemplateDirectory"));
                    Assert.Equal(previous, settings.TemplateDir);
                    Assert.Contains(interaction.Messages, message => message.Contains("反映できませんでした"));
                    Assert.Equal("保存先をファイルで塞ぐ", File.ReadAllText(blocked));
                }
                finally { AppSettings.SettingsDirectoryOverride = oldDirectory; }
            }));
        }

        [Theory]
        [InlineData(DialogResult.Yes)]
        [InlineData(DialogResult.No)]
        [InlineData(DialogResult.Cancel)]
        public void テンプレートの未保存変更は保存破棄取消を選べる(DialogResult answer)
        {
            RunSta(() => WithTemplates((form, dir, sample, interaction) =>
            {
                var list = Named<ListBox>(form, "templateList"); string originalName = ((OutputChoices.TemplateItem)list.SelectedItem).Name;
                string original = TemplateStore.Read(dir, originalName); Named<TextBox>(form, "templateBody").Text = "# 保存前の変更 {{caption}}\n";
                interaction.Answers.Enqueue(answer); list.SelectedIndex = 1;
                Assert.Equal(answer == DialogResult.Cancel ? 0 : 1, list.SelectedIndex);
                Assert.Equal(answer == DialogResult.Yes ? "# 保存前の変更 {{caption}}\n" : original, TemplateStore.Read(dir, originalName));
                if (answer == DialogResult.Cancel)
                {
                    var closing = new FormClosingEventArgs(CloseReason.UserClosing, false); interaction.Answers.Enqueue(DialogResult.Cancel);
                    Invoke(form, "OnFormClosing", closing); Assert.True(closing.Cancel);
                }
                interaction.Answers.Enqueue(DialogResult.No);
            }));
        }

        [Fact]
        public void テンプレートの追加保存戻す削除と検証エラーを実コントロールから処理する()
        {
            RunSta(() => WithTemplates((form, dir, sample, interaction) =>
            {
                var body = Named<TextBox>(form, "templateBody"); var name = Named<TextBox>(form, "newTemplateName");
                string originalName = ((OutputChoices.TemplateItem)Named<ListBox>(form, "templateList").SelectedItem).Name;
                string original = TemplateStore.Read(dir, originalName);
                body.Text = "{{#if caption}}"; Click(Named<Button>(form, "saveTemplate"));
                Assert.Contains("保存できません", Named<Label>(form, "templateResult").Text); Assert.Equal(original, TemplateStore.Read(dir, originalName));
                interaction.Answers.Enqueue(DialogResult.Yes); Named<ListBox>(form, "templateList").SelectedIndex = 1;
                Assert.Equal(0, Named<ListBox>(form, "templateList").SelectedIndex);
                Click(Named<Button>(form, "revertTemplate")); Assert.Equal(original.Replace("\n", "\r\n"), body.Text);
                name.Text = "bad/name"; Click(Named<Button>(form, "createTemplate")); Assert.Contains("使えない文字", Named<Label>(form, "templateResult").Text);
                name.Text = "追加様式"; Click(Named<Button>(form, "createTemplate")); Assert.True(File.Exists(TemplateStore.PathOf(dir, "追加様式")));
                body.Text = "# 編集済み {{caption}}\n"; Click(Named<Button>(form, "saveTemplate"));
                Assert.Equal("# 編集済み {{caption}}\n", TemplateStore.Read(dir, "追加様式"));
                interaction.Answers.Enqueue(DialogResult.No); Click(Named<Button>(form, "deleteTemplate")); Assert.True(File.Exists(TemplateStore.PathOf(dir, "追加様式")));
                interaction.FailRecycle = true; interaction.Answers.Enqueue(DialogResult.Yes); Click(Named<Button>(form, "deleteTemplate"));
                Assert.Contains("削除できません", Named<Label>(form, "templateResult").Text);
                interaction.FailRecycle = false; interaction.Answers.Enqueue(DialogResult.Yes); Click(Named<Button>(form, "deleteTemplate"));
                Assert.False(File.Exists(TemplateStore.PathOf(dir, "追加様式")));
            }));
        }

        [Fact]
        public void テンプレートの未保存プレビューは原本を変えず失敗を表示する()
        {
            RunSta(() => WithTemplates((form, dir, sample, interaction) =>
            {
                string originalName = ((OutputChoices.TemplateItem)Named<ListBox>(form, "templateList").SelectedItem).Name;
                string original = TemplateStore.Read(dir, originalName);
                Named<TextBox>(form, "templateBody").Text = "# 未保存 {{caption}}\n{{#each steps}}\n{{images.side}}\n{{#endeach}}";
                Click(Named<Button>(form, "refreshTemplatePreview"));
                Assert.Contains("未保存 受注登録", form.PreviewHtml); Assert.Contains("data:image/png;base64,", form.PreviewHtml);
                Assert.Equal(original, TemplateStore.Read(dir, originalName));
                Named<TextBox>(form, "templateBody").Text = "{{#each steps}}"; Click(Named<Button>(form, "refreshTemplatePreview"));
                Assert.Contains("プレビューできません", Named<Label>(form, "templateResult").Text);
                interaction.Answers.Enqueue(DialogResult.No);
            }));
        }

        [Fact]
        public void 選択直前に消えたテンプレートは警告を出し編集していた内容を保持する()
        {
            RunSta(() => WithTemplates((form, dir, sample, interaction) =>
            {
                var body = Named<TextBox>(form, "templateBody"); string original = body.Text;
                File.Delete(TemplateStore.PathOf(dir, "b"));
                Named<ListBox>(form, "templateList").SelectedIndex = 1;
                Assert.Equal(0, Named<ListBox>(form, "templateList").SelectedIndex);
                Assert.Equal(original, body.Text);
                Assert.Contains("開けません", Named<Label>(form, "templateResult").Text);
                body.Text = "# 再編集できる {{caption}}"; Click(Named<Button>(form, "saveTemplate"));
                Assert.Equal("# 再編集できる {{caption}}", TemplateStore.Read(dir, "a"));
            }));
        }

        [Fact]
        public async Task プレビューを並列生成しても別々の内容になり成功失敗とも作業領域を残さない()
        {
            using var sample = new TestSession();
            string root = Path.Combine(Path.GetTempPath(), "OperationRecorder-template-preview");
            var before = Directory.Exists(root) ? Directory.GetDirectories(root).OrderBy(p => p).ToArray() : Array.Empty<string>();
            var source = Directory.GetFiles(sample.Dir, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
            var generated = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() => TemplateStore.Preview(sample.Dir, "# 並列" + i + " {{caption}}\n"))));
            for (int i = 0; i < generated.Length; i++) Assert.Equal("# 並列" + i + " 受注登録\n", generated[i].Replace("\r\n", "\n"));
            Assert.ThrowsAny<Exception>(() => TemplateStore.Preview(Path.Combine(sample.Root, "missing"), "# {{caption}}"));
            Assert.Equal(before, Directory.GetDirectories(root).OrderBy(p => p).ToArray());
            foreach (var file in source) Assert.Equal(file.Value, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file.Key))));
        }

        [Fact]
        public void オーバーレイの状態ボタン直近十手取消消去と逃避無効を非表示で検証する()
        {
            RunSta(() =>
            {
                using var sample = new TestSession(); using var overlay = new RecordingOverlay();
                int undo = 0, restart = 0, stop = 0, open = 0;
                overlay.UndoClicked += () => undo++; overlay.RestartClicked += () => restart++; overlay.StopClicked += () => stop++; overlay.OpenMainClicked += () => open++;
                overlay.SetTarget("試験対象"); overlay.SetShortcuts("試験キー"); overlay.SetStatus("記録中", Color.Red, true);
                foreach (string button in new[] { "btnUndo", "btnRestart", "btnStop", "btnOpen" }) Click(Field<Button>(overlay, button));
                Assert.Equal((1, 1, 1, 1), (undo, restart, stop, open));
                Assert.Contains("試験対象", Field<Label>(overlay, "lblTarget").Text); Assert.Equal("試験キー", Field<Label>(overlay, "lblKeys").Text);
                for (int i = 1; i <= 12; i++) overlay.AddStep("{\"seq\":" + i + ",\"kind\":\"capture\"}", sample.Dir, i);
                Assert.Equal(10, ((System.Collections.ICollection)Field<object>(overlay, "items")).Count);
                overlay.RemoveStep(12); Assert.Equal(9, ((System.Collections.ICollection)Field<object>(overlay, "items")).Count);
                overlay.SetStatus("保存中", Color.Orange, false);
                Assert.False(Field<Button>(overlay, "btnUndo").Enabled || Field<Button>(overlay, "btnRestart").Enabled || Field<Button>(overlay, "btnStop").Enabled);
                Assert.True(Field<Button>(overlay, "btnOpen").Enabled);
                overlay.FleeEnabled = false; Point original = overlay.Location; Invoke(overlay, "FleeFromPointerWindow"); Assert.Equal(original, overlay.Location);
                overlay.ClearItems(); Assert.Empty((System.Collections.IEnumerable)Field<object>(overlay, "items"));
                overlay.HideOverlay(); Assert.False(Field<System.Windows.Forms.Timer>(overlay, "fleeTimer").Enabled); Assert.False(overlay.Visible);
            });
        }

        static void WithMain(Action<MainForm, TestSession, FakeInteraction> action)
        {
            using var sample = new TestSession(); var interaction = new FakeInteraction { FolderResult = Path.Combine(sample.Root, "custom-templates") };
            string oldDirectory = AppSettings.SettingsDirectoryOverride; bool oldPassive = AppSettings.Passive; MainForm form = null;
            try
            {
                AppSettings.SettingsDirectoryOverride = Path.Combine(sample.Root, "isolated-settings"); AppSettings.Passive = true;
                form = new MainForm(false, true, interaction, UniquePrefix()); _ = form.Handle;
                action(form, sample, interaction);
            }
            finally { if (form != null) CloseMain(form); AppSettings.SettingsDirectoryOverride = oldDirectory; AppSettings.Passive = oldPassive; }
        }

        static void SelectSession(MainForm form, TestSession sample)
        {
            Named<TextBox>(form, "txtRecordsDir").Text = sample.Root; Invoke(form, "RefreshSessions", sample.Dir);
            var sessions = Field<ListView>(form, "lvSessions"); _ = sessions.Handle; sessions.Items[0].Selected = true;
            for (int i = 0; i < 3; i++) Application.DoEvents(); Invoke(form, "LoadSelectedSession");
            Assert.Equal(4, Named<ListBox>(form, "lbSteps").Items.Count);
        }

        static void WithTemplates(Action<TemplateEditorForm, string, TestSession, FakeInteraction> action)
        {
            using var sample = new TestSession(); string dir = Path.Combine(sample.Root, "templates"); Directory.CreateDirectory(dir);
            TemplateStore.Create(dir, "a", "# 元の様式 {{caption}}\n"); TemplateStore.Create(dir, "b", "# 二つ目 {{caption}}\n");
            var interaction = new FakeInteraction(); using var form = new TemplateEditorForm(dir, sample.Dir, interaction);
            try { action(form, dir, sample, interaction); }
            finally { interaction.Answers.Enqueue(DialogResult.No); form.Close(); }
        }

        sealed class FakeInteraction : AppInteraction
        {
            public readonly Queue<DialogResult> Answers = new Queue<DialogResult>();
            public readonly List<string> Messages = new List<string>(), Opened = new List<string>(), RecycledFiles = new List<string>(), RecycledDirectories = new List<string>();
            public string FolderResult; public bool FailRecycle, FailOpen; public int OpenFileRequests, Elevations;
            public override DialogResult Message(IWin32Window owner, string text, string title, MessageBoxButtons buttons, MessageBoxIcon icon) { Messages.Add(text); return Answers.Count > 0 ? Answers.Dequeue() : DialogResult.OK; }
            public override string SelectFolder(IWin32Window owner, string description, string selectedPath) => FolderResult;
            public override string OpenFile(IWin32Window owner, string title, string filter, string initialDirectory) { OpenFileRequests++; return null; }
            public override void OpenPath(string path) { Opened.Add(path); if (FailOpen) throw new IOException("試験用の起動失敗"); }
            public override void RecycleFile(string path) { RecycledFiles.Add(path); if (FailRecycle) throw new IOException("試験用の削除失敗"); File.Delete(path); }
            public override void RecycleDirectory(string path) { RecycledDirectories.Add(path); if (FailRecycle) throw new IOException("試験用の削除失敗"); Directory.Delete(path, true); }
            public override void RestartAsAdmin(string executable) { Elevations++; }
            public override bool ShortcutIsInstalled => false;
            public override bool ShortcutPointsToThisExe => false;
            public override bool InstallerShortcutPointsToThisExe => false;
        }

        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static string UniquePrefix() => @"Local\OperationRecorderUiTest." + Guid.NewGuid().ToString("N") + ".";
        static T Named<T>(Control parent, string name) where T : Control => parent.Controls.Find(name, true).Cast<T>().Single();
        static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Private).GetValue(instance);
        static object Invoke(object instance, string name, params object[] args)
        {
            var method = instance.GetType().GetMethod(name, Private) ?? typeof(Form).GetMethod(name, Private);
            try { return method.Invoke(instance, args); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        static void Click(Button button) => typeof(Button).GetMethod("OnClick", Private).Invoke(button, new object[] { EventArgs.Empty });
        static void CloseMain(MainForm form) { if (!form.IsDisposed) { _ = form.Handle; typeof(MainForm).GetField("exiting", Private).SetValue(form, true); form.Close(); form.Dispose(); } }
        static void RunSta(Action action)
        {
            Exception error = null;
            var thread = new Thread(() => { try { Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, true); action(); } catch (Exception ex) { error = ex; } }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(40)), "非表示STA試験が終了しませんでした。");
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        }
    }
}
