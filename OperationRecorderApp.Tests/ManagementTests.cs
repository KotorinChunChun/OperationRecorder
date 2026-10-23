using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows.Forms;
using OpRec;
using Xunit;

namespace OperationRecorderApp.Tests
{
    // AppSettingsの試験用保存先はプロセス共通のため、ほかのコレクションとは並列実行しない。
    [CollectionDefinition("管理画面の隔離設定", DisableParallelization = true)]
    public sealed class ManagementSettingsCollection { }

    [Collection("管理画面の隔離設定")]
    public class ManagementTests
    {
        [Theory]
        [InlineData(RecState.Recording)]
        [InlineData(RecState.Saving)]
        public void 記録と保存を中断する管理停止は拒否する(RecState state) =>
            Assert.Contains("保存が完了", ManagementStopPolicy.Reason(state, false));

        [Fact]
        public void ダイアログを閉じるまで停止しない() =>
            Assert.Contains("ダイアログ", ManagementStopPolicy.Reason(RecState.Idle, true));

        [Fact]
        public void 待機中は正常停止できる() =>
            Assert.Null(ManagementStopPolicy.Reason(RecState.Idle, false));

        [Theory]
        [InlineData("txtSesCaption", "保存後の記録名")]
        [InlineData("txtSesDescription", "保存後の解説")]
        [InlineData("txtStepHeadline", "保存後の手順見出し")]
        [InlineData("txtStepBody", "保存後の本文\r\n2行目")]
        public void 未保存の記録編集は管理停止を拒否し保存すると解除する(string fieldName, string editedText)
        {
            RunSta(() =>
            {
                using var session = new TestSession();
                string originalSettingsDirectory = AppSettings.SettingsDirectoryOverride;
                bool originalPassive = AppSettings.Passive;
                MainForm form = null;
                try
                {
                    AppSettings.SettingsDirectoryOverride = Path.Combine(session.Root, "isolated-settings");
                    AppSettings.Passive = true;
                    form = new MainForm(false, true);
                    // 非表示フォームの実ハンドルは作るが、通常アプリの名前付きイベントは作らない。
                    Assert.False(form.IsHandleCreated);
                    SetField(form, "instanceSignal", new InstanceSignal(null, null,
                        @"Local\OperationRecorderManagementTest." + Guid.NewGuid().ToString("N") + "."));
                    _ = form.Handle;
                    var sessions = Field<ListView>(form, "lvSessions");
                    _ = sessions.Handle;
                    Field<TextBox>(form, "txtRecordsDir").Text = session.Root;
                    Invoke(form, "RefreshSessions", session.Dir);
                    Invoke(form, "LoadSelectedSession");

                    Assert.False(form.Visible);
                    Assert.Single(sessions.SelectedItems);
                    Assert.Equal(4, Field<ListBox>(form, "lbSteps").Items.Count);
                    Assert.Null(StopReason(form));

                    Field<TextBox>(form, fieldName).Text = editedText;
                    Assert.Contains("保存してから停止", StopReason(form));

                    if (fieldName == "txtSesCaption" || fieldName == "txtSesDescription")
                    {
                        Invoke(form, "SaveSessionMeta");
                        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(session.Dir, "session.json")));
                        Assert.Equal(editedText, (string)saved[fieldName == "txtSesCaption" ? "caption" : "description"]);
                    }
                    else
                    {
                        Invoke(form, "SaveStepText", false);
                        var saved = ReportGenerator.ListSteps(session.Dir)[0];
                        string actual = fieldName == "txtStepHeadline" ? saved.Headline : saved.Body;
                        Assert.Equal(editedText.Replace("\r\n", "\n"), actual.Replace("\r\n", "\n"));
                    }
                    Assert.Null(StopReason(form));
                    Assert.False(form.Visible);
                }
                finally
                {
                    // 失敗した場合も閉じる処理を通し、タイマー・トレイ・名前付きイベントを片付ける。
                    try
                    {
                        if (form != null)
                        {
                            SetField(form, "exiting", true);
                            form.Close();
                            form.Dispose();
                        }
                    }
                    finally
                    {
                        AppSettings.SettingsDirectoryOverride = originalSettingsDirectory;
                        AppSettings.Passive = originalPassive;
                    }
                }
            });
        }

        const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        static T Field<T>(MainForm form, string name) => (T)typeof(MainForm).GetField(name, PrivateInstance).GetValue(form);
        static void SetField(MainForm form, string name, object value) => typeof(MainForm).GetField(name, PrivateInstance).SetValue(form, value);
        static object Invoke(MainForm form, string name, params object[] args) => typeof(MainForm).GetMethod(name, PrivateInstance).Invoke(form, args);
        static string StopReason(MainForm form) => (string)typeof(MainForm).GetProperty("ManagementStopBlockReason", PrivateInstance).GetValue(form);

        static void RunSta(Action action)
        {
            Exception error = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { error = ex; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "非表示管理画面の検証が終了しませんでした。");
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        }
    }
}
