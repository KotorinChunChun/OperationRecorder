using System;
using System.Threading;
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
        TextBox haasPython, haasModel;
        TextBox transcriptionOpenAIModel, transcriptionGeminiModel, transcriptionKey;
        CheckBox realtimeTranscription;
        TranscriptionProvider NarrationProvider => (narrationProvider.SelectedItem as TranscriptionChoice)?.Provider ?? TranscriptionProvider.None;

        static readonly TranscriptionChoice[] transcriptionChoices =
        [
            new(TranscriptionProvider.None, "文字起こししない"),
            new(TranscriptionProvider.Whisper, "Whisper（ローカル）"),
            new(TranscriptionProvider.Haas, "Haas（ローカル）"),
            new(TranscriptionProvider.Compatible, "互換サーバー（音声を送信）"),
            new(TranscriptionProvider.OpenAI, "OpenAI GPT（音声を送信）"),
            new(TranscriptionProvider.Gemini, "Gemini（音声を送信）"),
            new(TranscriptionProvider.Windows, "OS標準音声モデル（非推奨）"),
        ];

        void AddTranscriptionSettings(FlowLayoutPanel panel)
        {
            narrationProvider = new ComboBox { Name = nameof(narrationProvider), AccessibleName = "文字起こし方式",
                DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
            narrationProvider.Items.AddRange(transcriptionChoices);
            narrationProvider.SelectedItem = Array.Find(transcriptionChoices, choice => choice.Provider == TranscriptionSettings.SelectedProvider(settings));
            panel.Controls.Add(Row(TranscriptionLabel("文字起こし方式"), narrationProvider));
            realtimeTranscription = new CheckBox { Text = "録音中にリアルタイムで文字起こしする", AutoSize = true, Checked = settings.RealtimeTranscription };
            panel.Controls.Add(realtimeTranscription);

            var pythonRow = PathSelectionRow("文字起こし用のPython", nameof(transcriptionPython), settings.TranscriptionPython,
                false, out transcriptionPython, 245);
            var localModelRow = PathSelectionRow("ローカルモデルのフォルダー", nameof(transcriptionLocalModel),
                settings.TranscriptionModel, true, out transcriptionLocalModel, 245);
            var haasPythonRow = PathSelectionRow("文字起こし用のPython", nameof(haasPython), settings.HaasPython, false, out haasPython, 245);
            var haasModelRow = PathSelectionRow("ローカルモデルのフォルダー", nameof(haasModel), settings.HaasModel, true, out haasModel, 245);
            var install = new Button { Text = "モデルを導入", AutoSize = true };
            var delete = new Button { Text = "導入したモデルを削除", AutoSize = true };
            var cancel = new Button { Text = "中止", AutoSize = true, Visible = false };
            var modelStatus = TranscriptionLabel("");
            var installRow = Row(install, delete, cancel);
            var installer = new LocalTranscriptionInstaller(AppSettings.SettingsDir);
            CancellationTokenSource installing = null;
            var endpointRow = TranscriptionTextRow("互換サーバーのURL", settings.TranscriptionServerUrl, out transcriptionEndpoint);
            var serverModelRow = TranscriptionTextRow("モデルID（任意）", settings.TranscriptionServerModel, out transcriptionServerModel);
            var openAIModelRow = TranscriptionTextRow("OpenAIのモデルID", settings.TranscriptionOpenAIModel, out transcriptionOpenAIModel);
            var geminiModelRow = TranscriptionTextRow("GeminiのモデルID", settings.TranscriptionGeminiModel, out transcriptionGeminiModel);
            transcriptionKey = new TextBox { AccessibleName = "APIキー", UseSystemPasswordChar = true, Width = 205, MaxLength = 2048 };
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
                pythonRow.Visible = localModelRow.Visible = provider == TranscriptionProvider.Whisper;
                haasPythonRow.Visible = haasModelRow.Visible = provider == TranscriptionProvider.Haas;
                bool local = provider is TranscriptionProvider.Whisper or TranscriptionProvider.Haas;
                installRow.Visible = modelStatus.Visible = local;
                realtimeTranscription.Enabled = provider != TranscriptionProvider.None;
                modelStatus.Text = local && installer.Installed(provider) ? "導入済み" : "";
                delete.Enabled = local && installer.Installed(provider);
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

            (TextBox Python, TextBox Model) LocalBoxes(TranscriptionProvider provider) =>
                provider == TranscriptionProvider.Haas ? (haasPython, haasModel) : (transcriptionPython, transcriptionLocalModel);
            install.Click += async (_, _) =>
            {
                var provider = NarrationProvider;
                if (installing != null) return;
                using var operation = new CancellationTokenSource(TimeSpan.FromHours(1));
                installing = operation;
                install.Enabled = delete.Enabled = narrationProvider.Enabled = false;
                cancel.Visible = true;
                try
                {
                    await installer.Install(provider, new Progress<string>(message => modelStatus.Text = message), operation.Token);
                    var boxes = LocalBoxes(provider);
                    (boxes.Python.Text, boxes.Model.Text) = installer.Paths(provider);
                    SaveTranscriptionSettings();
                    settings.Save();
                }
                catch (OperationCanceledException) { }
                catch (Exception error) { ShowInfo("モデルを導入できません。\n" + error.Message); }
                finally
                {
                    installing = null;
                    cancel.Visible = false;
                    install.Enabled = narrationProvider.Enabled = true;
                    UpdateRows();
                }
            };
            cancel.Click += (_, _) => installing?.Cancel();
            delete.Click += (_, _) =>
            {
                var provider = NarrationProvider;
                if (controller.State != RecState.Idle || regeneratingBody)
                {
                    ShowInfo("記録と文字起こしの処理が終わってから削除できます。");
                    return;
                }
                try
                {
                    var boxes = LocalBoxes(provider);
                    var paths = installer.Paths(provider);
                    installer.Remove(provider);
                    if (string.Equals(boxes.Python.Text.Trim(), paths.Python, StringComparison.OrdinalIgnoreCase)) boxes.Python.Clear();
                    if (string.Equals(boxes.Model.Text.Trim(), paths.Model, StringComparison.OrdinalIgnoreCase)) boxes.Model.Clear();
                    SaveTranscriptionSettings();
                    settings.Save();
                    UpdateRows();
                }
                catch (Exception error) { ShowInfo("モデルを削除できません。\n" + error.Message); }
            };
            panel.Controls.AddRange([installRow, modelStatus, pythonRow, localModelRow, haasPythonRow, haasModelRow,
                endpointRow, serverModelRow, openAIModelRow, geminiModelRow, keyRow]);
            narrationProvider.SelectedIndexChanged += (_, _) => UpdateRows();
            transcriptionEndpoint.TextChanged += (_, _) => { transcriptionKey.Clear(); UpdateKeyStatus(); };
            saveKey.Click += (_, _) => ChangeKey(false);
            removeKey.Click += (_, _) => ChangeKey(true);
            UpdateRows();
        }

        static Label TranscriptionLabel(string text) => new Label { Text = text, AutoSize = true, Margin = new Padding(3, 7, 3, 3) };

        Control TranscriptionTextRow(string label, string value, out TextBox box)
        {
            box = new TextBox { AccessibleName = label, Text = value, Width = 300 };
            return Row(TranscriptionLabel(label), box);
        }

        void SaveTranscriptionSettings()
        {
            settings.NarrationProvider = NarrationProvider.ToString();
            settings.TranscriptionPython = transcriptionPython.Text.Trim();
            settings.TranscriptionModel = transcriptionLocalModel.Text.Trim();
            settings.HaasPython = haasPython.Text.Trim();
            settings.HaasModel = haasModel.Text.Trim();
            settings.RealtimeTranscription = realtimeTranscription.Checked;
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
