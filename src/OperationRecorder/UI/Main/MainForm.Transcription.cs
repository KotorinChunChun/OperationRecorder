using System;
using System.Windows.Forms;

namespace OperationRecorderApp
{
    public sealed partial class MainForm
    {
        sealed record TranscriptionChoice(TranscriptionProvider Provider, string Label)
        {
            public override string ToString() => Label;
        }

        ComboBox narrationProvider;
        TextBox transcriptionPython, transcriptionLocalModel, transcriptionEndpoint, transcriptionServerModel;
        TextBox transcriptionOpenAIModel, transcriptionGeminiModel, transcriptionKey;
        TranscriptionProvider NarrationProvider => (narrationProvider.SelectedItem as TranscriptionChoice)?.Provider ?? TranscriptionProvider.None;

        void AddTranscriptionSettings(FlowLayoutPanel panel)
        {
            var choices = new[]
            {
                new TranscriptionChoice(TranscriptionProvider.None, "文字起こししない"),
                new TranscriptionChoice(TranscriptionProvider.Whisper, "Whisper（ローカル）"),
                new TranscriptionChoice(TranscriptionProvider.Haas, "Haas（ローカル）"),
                new TranscriptionChoice(TranscriptionProvider.Compatible, "互換サーバー（音声を送信）"),
                new TranscriptionChoice(TranscriptionProvider.OpenAI, "OpenAI GPT（音声を送信）"),
                new TranscriptionChoice(TranscriptionProvider.Gemini, "Gemini（音声を送信）"),
            };
            narrationProvider = new ComboBox { Name = nameof(narrationProvider), AccessibleName = "文字起こし方式",
                DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
            narrationProvider.Items.AddRange(choices);
            narrationProvider.SelectedItem = Array.Find(choices, choice => choice.Provider == TranscriptionSettings.SelectedProvider(settings));
            panel.Controls.Add(Row(TranscriptionLabel("文字起こし方式"), narrationProvider));

            var pythonRow = PathSelectionRow("文字起こし用のPython", nameof(transcriptionPython), settings.TranscriptionPython,
                false, out transcriptionPython);
            var localModelRow = PathSelectionRow("ローカルモデルのフォルダー", nameof(transcriptionLocalModel),
                settings.TranscriptionModel, true, out transcriptionLocalModel);
            var endpointRow = TranscriptionTextRow("互換サーバーのURL", settings.TranscriptionServerUrl, out transcriptionEndpoint);
            var serverModelRow = TranscriptionTextRow("モデルID（任意）", settings.TranscriptionServerModel, out transcriptionServerModel);
            var openAIModelRow = TranscriptionTextRow("OpenAIのモデルID", settings.TranscriptionOpenAIModel, out transcriptionOpenAIModel);
            var geminiModelRow = TranscriptionTextRow("GeminiのモデルID", settings.TranscriptionGeminiModel, out transcriptionGeminiModel);
            transcriptionKey = new TextBox { AccessibleName = "APIキー", UseSystemPasswordChar = true, Width = 275, MaxLength = 2048 };
            var saveKey = new Button { Text = "キーを保存", AutoSize = true };
            var removeKey = new Button { Text = "キーを削除", AutoSize = true };
            var keyStatus = TranscriptionLabel("");
            var keyRow = Row(TranscriptionLabel("APIキー"), transcriptionKey, saveKey, removeKey, keyStatus);
            var credentials = new TranscriptionCredentials(AppSettings.SettingsDir);

            Uri SelectedEndpoint() => NarrationProvider == TranscriptionProvider.Compatible
                ? TranscriptionSettings.ServerEndpoint(transcriptionEndpoint.Text) : null;

            void UpdateKeyStatus()
            {
                try
                {
                    var address = SelectedEndpoint();
                    bool exists = credentials.Exists(NarrationProvider, address);
                    keyStatus.Text = exists ? "保存済み" : "未登録";
                    saveKey.Enabled = true;
                    removeKey.Enabled = exists;
                }
                catch (ArgumentException)
                {
                    keyStatus.Text = "";
                    saveKey.Enabled = removeKey.Enabled = false;
                }
            }

            void UpdateRows()
            {
                var provider = NarrationProvider;
                pythonRow.Visible = localModelRow.Visible = provider is TranscriptionProvider.Whisper or TranscriptionProvider.Haas;
                endpointRow.Visible = serverModelRow.Visible = provider == TranscriptionProvider.Compatible;
                openAIModelRow.Visible = provider == TranscriptionProvider.OpenAI;
                geminiModelRow.Visible = provider == TranscriptionProvider.Gemini;
                keyRow.Visible = provider is TranscriptionProvider.Compatible or TranscriptionProvider.OpenAI or TranscriptionProvider.Gemini;
                transcriptionKey.Clear();
                UpdateKeyStatus();
            }

            void ChangeKey(bool remove)
            {
                try
                {
                    var address = SelectedEndpoint();
                    if (remove) credentials.Remove(NarrationProvider, address);
                    else credentials.Save(NarrationProvider, address, transcriptionKey.Text);
                    transcriptionKey.Clear();
                    UpdateKeyStatus();
                }
                catch (Exception error)
                {
                    string message = error is ArgumentException ? error.Message : "APIキーを変更できません。現在のWindowsユーザーの設定フォルダーを確認してください。";
                    MessageBox.Show(this, message, "文字起こし", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            panel.Controls.AddRange([pythonRow, localModelRow, endpointRow, serverModelRow, openAIModelRow, geminiModelRow, keyRow]);
            narrationProvider.SelectedIndexChanged += (_, _) => UpdateRows();
            transcriptionEndpoint.TextChanged += (_, _) => { transcriptionKey.Clear(); UpdateKeyStatus(); };
            saveKey.Click += (_, _) => ChangeKey(false);
            removeKey.Click += (_, _) => ChangeKey(true);
            UpdateRows();
        }

        static Label TranscriptionLabel(string text) => new Label { Text = text, AutoSize = true, Margin = new Padding(3, 7, 3, 3) };

        Control TranscriptionTextRow(string label, string value, out TextBox box)
        {
            box = new TextBox { AccessibleName = label, Text = value, Width = 400 };
            return Row(TranscriptionLabel(label), box);
        }

        void SaveTranscriptionSettings()
        {
            settings.NarrationProvider = NarrationProvider.ToString();
            settings.TranscriptionPython = transcriptionPython.Text.Trim();
            settings.TranscriptionModel = transcriptionLocalModel.Text.Trim();
            try
            {
                settings.TranscriptionServerUrl = transcriptionEndpoint.Text.Length == 0
                    ? "" : TranscriptionSettings.ServerEndpoint(transcriptionEndpoint.Text).AbsoluteUri;
            }
            catch (ArgumentException) { settings.TranscriptionServerUrl = ""; }
            settings.TranscriptionServerModel = transcriptionServerModel.Text.Trim();
            settings.TranscriptionOpenAIModel = transcriptionOpenAIModel.Text.Trim();
            settings.TranscriptionGeminiModel = transcriptionGeminiModel.Text.Trim();
        }
    }
}
