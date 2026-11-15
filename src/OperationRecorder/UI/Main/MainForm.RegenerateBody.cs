using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OperationRecorderApp
{
    public sealed partial class MainForm
    {
        async Task RegenerateStepBody()
        {
            var session = SelectedSession;
            var step = SelectedStep;
            if (session == null || step == null || regeneratingBody) return;

            SaveTranscriptionSettings();
            var options = PickRegenerationModel();
            if (options == null) return;
            long start = step.Number == 1 ? 0 : step.StartMs;
            long? end = currentSteps.Skip(step.Number).FirstOrDefault()?.StartMs;
            string originalDraft = txtStepBody.Text;
            regeneratingBody = true;
            btnRegenerateBody.Enabled = false;
            btnRegenerateBody.Text = "処理中…";
            try
            {
                string narration = await Task.Run(() => RecordedNarration.Read(session.Dir).Regenerate(options, start, end));
                if (IsDisposed || SelectedSession?.Dir != session.Dir || SelectedStep?.Seq != step.Seq) return;
                if (narration.Length == 0)
                {
                    ShowInfo("この手順の録音から本文を生成できませんでした。");
                    return;
                }
                if (txtStepBody.Text != originalDraft)
                {
                    ShowInfo("処理中に本文が編集されたため、再生成した内容の反映を見送りました。");
                    return;
                }
                string body = string.Join("\n\n", new[] { step.BodyWithoutNarration, narration }
                    .Where(text => !string.IsNullOrWhiteSpace(text)));
                txtStepBody.Text = body.Replace("\r\n", "\n").Replace("\n", "\r\n");
            }
            catch (Exception error) { if (!IsDisposed) ShowInfo("本文を再生成できません。\n" + error.Message); }
            finally
            {
                regeneratingBody = false;
                if (!IsDisposed)
                {
                    btnRegenerateBody.Text = "再生成…";
                    EnableStepEditor(SelectedStep != null);
                }
            }
        }

        TranscriptionSettings PickRegenerationModel()
        {
            using var dialog = new BrandedForm
            {
                Text = WindowTitle.Create("本文を再生成"), StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
                ShowInTaskbar = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            };
            var panel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                Padding = new Padding(12), Dock = DockStyle.Fill };
            var choices = transcriptionChoices.Where(choice => choice.Provider != TranscriptionProvider.None).ToArray();
            var picker = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "文字起こし方式", Width = 260 };
            picker.Items.AddRange(choices);
            picker.SelectedItem = choices.FirstOrDefault(choice => choice.Provider == NarrationProvider) ?? choices[0];
            var modelRow = TranscriptionTextRow("モデルID", "", out var model);
            var folderRow = PathSelectionRow("モデルのフォルダー", "regenerationModelFolder", "", true, out var folder);
            var execute = new Button { Text = "実行", AutoSize = true };
            var cancel = new Button { Text = "キャンセル", AutoSize = true, DialogResult = DialogResult.Cancel };
            panel.Controls.AddRange([Row(TranscriptionLabel("文字起こし方式"), picker), modelRow, folderRow, Row(execute, cancel)]);
            dialog.Controls.Add(panel);
            dialog.AcceptButton = execute;
            dialog.CancelButton = cancel;
            TranscriptionSettings result = null;
            TranscriptionProvider Provider() => ((TranscriptionChoice)picker.SelectedItem).Provider;

            void UpdateModelFields()
            {
                var provider = Provider();
                modelRow.Visible = provider is TranscriptionProvider.Compatible or TranscriptionProvider.TranscriptionServer or
                    TranscriptionProvider.OpenAI or TranscriptionProvider.Gemini;
                folderRow.Visible = provider is TranscriptionProvider.Whisper or TranscriptionProvider.Haas;
                if (provider == TranscriptionProvider.Compatible) model.Text = settings.TranscriptionServerModel;
                else if (provider == TranscriptionProvider.TranscriptionServer) model.Text = settings.KotorinTranscriptionServerModel;
                else
                {
                    var selected = TranscriptionSettings.From(settings, provider);
                    model.Text = selected.Model ?? "";
                    folder.Text = selected.LocalModel ?? "";
                }
            }

            picker.SelectedIndexChanged += (_, _) => UpdateModelFields();
            execute.Click += (_, _) =>
            {
                try
                {
                    result = TranscriptionSettings.From(settings, Provider());
                    if (modelRow.Visible && !string.IsNullOrWhiteSpace(model.Text)) result = result with { Model = model.Text.Trim() };
                    if (folderRow.Visible) result = result with { LocalModel = folder.Text.Trim() };
                    dialog.DialogResult = DialogResult.OK;
                }
                catch (ArgumentException error) { MessageBox.Show(dialog, error.Message, dialog.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            UpdateModelFields();
            return dialog.ShowDialog(this) == DialogResult.OK ? result : null;
        }
    }
}
