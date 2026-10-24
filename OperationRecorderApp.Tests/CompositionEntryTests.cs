using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows.Forms;
using OpRec;
using Xunit;

namespace OperationRecorderApp.Tests
{
    // 通常設定と名前付きイベントを分離した実フォームで、合成の入口と停止保護を確認する。
    [Collection("管理画面の隔離設定")]
    public sealed class CompositionEntryTests
    {
        [Fact]
        public void 複数記録の選択では合成だけを有効にし単独選択で編集を戻す()
        {
            RunSta(() => WithForm((form, fixture) =>
            {
                var sessions = Field<ListView>(form, "lvSessions");
                var compose = Named<Button>(form, "composeSessions");
                var resume = Named<Button>(form, "resumeComposition");
                Assert.True(sessions.MultiSelect);
                Assert.Equal(3, sessions.Items.Count);
                Assert.Empty(sessions.SelectedItems.Cast<ListViewItem>());
                Assert.False(compose.Enabled);
                Assert.True(resume.Enabled); // 選択中の記録がなくても保存済み作業を再開できる。

                sessions.Items[0].Selected = true;
                Pump();
                Assert.Single(sessions.SelectedItems.Cast<ListViewItem>());
                AssertIndividualEditing(form, true);
                Assert.True(compose.Enabled);
                Assert.Equal(4, Field<ListBox>(form, "lbSteps").Items.Count);

                sessions.Items[1].Selected = true;
                Pump();
                Assert.Equal(2, sessions.SelectedItems.Count);
                AssertIndividualEditing(form, false);
                Assert.True(compose.Enabled);
                Assert.True(resume.Enabled);
                Assert.Empty(Field<ListBox>(form, "lbSteps").Items.Cast<object>());
                Assert.Null(StopReason(form));

                sessions.Items[0].Selected = false;
                Pump();
                Assert.Single(sessions.SelectedItems.Cast<ListViewItem>());
                AssertIndividualEditing(form, true);
                Assert.Equal(4, Field<ListBox>(form, "lbSteps").Items.Count);

                sessions.Items[1].Selected = false;
                Pump();
                Assert.False(compose.Enabled);
                Assert.True(resume.Enabled);
                AssertIndividualEditing(form, false);
                fixture.AssertSourcesUnchanged();
            }));
        }

        [Fact]
        public void 一覧を更新しても未保存の記録名と手順本文を保持する()
        {
            RunSta(() => WithForm((form, fixture) =>
            {
                var sessions = Field<ListView>(form, "lvSessions");
                sessions.Items[0].Selected = true;
                Pump();
                var caption = Field<TextBox>(form, "txtSesCaption");
                var body = Field<TextBox>(form, "txtStepBody");
                caption.Text = "途中で編集した合成元の名前";
                body.Text = "途中で編集した手順の本文\r\n保存前の追記";
                Assert.Contains("保存してから停止", StopReason(form));

                Invoke(form, "RefreshSessions", (object)null);
                Pump();

                Assert.Equal("途中で編集した合成元の名前", caption.Text);
                Assert.Equal("途中で編集した手順の本文\r\n保存前の追記", body.Text);
                Assert.Single(sessions.SelectedItems.Cast<ListViewItem>());
                Assert.Contains("保存してから停止", StopReason(form));
                fixture.AssertSourcesUnchanged();
            }));
        }

        [Fact]
        public void 選択記録の合成をモーダルで開いている間は管理停止を拒否し原記録を保持する()
        {
            RunSta(() => WithForm((form, fixture) =>
            {
                var sessions = Field<ListView>(form, "lvSessions");
                foreach (ListViewItem item in sessions.Items) item.Selected = true;
                Pump();
                Assert.Equal(3, sessions.SelectedItems.Count);
                Assert.Null(StopReason(form));

                Exception timerFailure = null;
                bool inspected = false;
                int ticks = 0;
                using var timer = new System.Windows.Forms.Timer { Interval = 30 };
                timer.Tick += (sender, args) =>
                {
                    var dialog = Application.OpenForms.Cast<Form>().OfType<MergeForm>()
                        .SingleOrDefault(candidate => candidate.Visible && candidate.Modal);
                    if (dialog == null && ++ticks < 300) return;
                    timer.Stop();
                    try
                    {
                        Assert.NotNull(dialog);
                        Assert.Contains("ダイアログ", StopReason(form));
                        // MainFormの日時順ソートが新しい合成画面へ渡されることも確認する。
                        var project = typeof(MergeForm).GetFields(PrivateInstance)
                            .Where(field => field.FieldType == typeof(CompositionProject))
                            .Select(field => field.GetValue(dialog)).Cast<CompositionProject>().Single();
                        Assert.Equal(fixture.ChronologicalDirectories,
                            project.Sources.Select(source => source.OriginalDirectory).ToArray());
                        fixture.AssertSourcesUnchanged();
                        inspected = true;
                    }
                    catch (Exception error) { timerFailure = error; }
                    finally
                    {
                        if (dialog != null)
                        {
                            dialog.DialogResult = DialogResult.Cancel;
                            dialog.Close();
                        }
                    }
                };
                timer.Start();
                Invoke(form, "OpenMerge");
                timer.Stop();
                if (timerFailure != null) ExceptionDispatchInfo.Capture(timerFailure).Throw();
                Assert.True(inspected, "実際の合成ダイアログを確認できませんでした。");
                Assert.Null(StopReason(form));
                fixture.AssertSourcesUnchanged();
            }));
        }

        static void AssertIndividualEditing(MainForm form, bool enabled)
        {
            foreach (var field in new[] { "txtSesCaption", "txtSesDescription", "btnSaveMeta", "btnOutHtml",
                "btnOutMd", "btnOpenSaveFolder", "btnDeleteSession", "txtStepHeadline", "txtStepBody",
                "btnSaveStep", "btnResetStep", "btnDeleteStep" })
                Assert.Equal(enabled, Field<Control>(form, field).Enabled);
        }

        static void WithForm(Action<MainForm, Fixture> action)
        {
            using var fixture = new Fixture();
            string originalDirectory = AppSettings.SettingsDirectoryOverride;
            bool originalPassive = AppSettings.Passive;
            MainForm form = null;
            try
            {
                AppSettings.SettingsDirectoryOverride = Path.Combine(fixture.Root, "isolated-settings");
                AppSettings.Passive = true;
                form = new MainForm(false, true);
                Assert.False(form.IsHandleCreated);
                SetField(form, "instanceSignal", new InstanceSignal(null, null,
                    @"Local\OperationRecorderCompositionEntryTest." + Guid.NewGuid().ToString("N") + "."));
                _ = form.Handle;
                var sessions = Field<ListView>(form, "lvSessions");
                _ = sessions.Handle;
                Field<TextBox>(form, "txtRecordsDir").Text = fixture.Records;
                Invoke(form, "RefreshSessions", (object)null);
                Invoke(form, "LoadSelectedSession");
                action(form, fixture);
            }
            finally
            {
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
                    AppSettings.SettingsDirectoryOverride = originalDirectory;
                    AppSettings.Passive = originalPassive;
                }
            }
        }

        sealed class Fixture : IDisposable
        {
            readonly TestSession sample = new TestSession(marksOutput: true);
            readonly Dictionary<string, string> hashes;
            public string Root => sample.Root;
            public string Records { get; }
            public string[] ChronologicalDirectories { get; }

            public Fixture()
            {
                Records = Path.Combine(Root, "entry-records");
                Directory.CreateDirectory(Records);
                // 一覧のフォルダー名順と開始日時順を変え、合成入口の順序を検証する。
                var directories = new List<string>();
                foreach (var pair in new[] { ("session-003_A", "2026-10-21T10:00:00"),
                    ("session-001_B", "2026-10-21T11:00:00"), ("session-002_C", "2026-10-21T12:00:00") })
                {
                    string target = Path.Combine(Records, pair.Item1);
                    foreach (string source in Directory.GetFiles(sample.Dir, "*", SearchOption.AllDirectories))
                    {
                        string destination = Path.Combine(target, Path.GetRelativePath(sample.Dir, source));
                        Directory.CreateDirectory(Path.GetDirectoryName(destination));
                        File.Copy(source, destination);
                    }
                    string sessionPath = Path.Combine(target, "session.json");
                    var session = JsonNode.Parse(File.ReadAllText(sessionPath)).AsObject();
                    session["caption"] = pair.Item1;
                    session["start"] = pair.Item2;
                    File.WriteAllText(sessionPath, session.ToJsonString());
                    directories.Add(Path.GetFullPath(target));
                }
                ChronologicalDirectories = directories.ToArray();
                hashes = SourceHashes();
            }

            Dictionary<string, string> SourceHashes() => ChronologicalDirectories
                .SelectMany(directory => Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                .ToDictionary(file => file, file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));

            public void AssertSourcesUnchanged()
            {
                var after = SourceHashes();
                Assert.Equal(hashes.Count, after.Count);
                foreach (var pair in hashes) Assert.Equal(pair.Value, after[pair.Key]);
            }

            public void Dispose() => sample.Dispose();
        }

        const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        static T Field<T>(MainForm form, string name) => (T)typeof(MainForm).GetField(name, PrivateInstance).GetValue(form);
        static void SetField(MainForm form, string name, object value) => typeof(MainForm).GetField(name, PrivateInstance).SetValue(form, value);
        static object Invoke(MainForm form, string name, params object[] args) => typeof(MainForm).GetMethod(name, PrivateInstance).Invoke(form, args);
        static string StopReason(MainForm form) => (string)typeof(MainForm).GetProperty("ManagementStopBlockReason", PrivateInstance).GetValue(form);
        static T Named<T>(Control parent, string name) where T : Control => parent.Controls.Find(name, true).Cast<T>().Single();
        static void Pump() { for (int i = 0; i < 3; i++) Application.DoEvents(); }

        static void RunSta(Action action)
        {
            Exception error = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception exception) { error = exception; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "合成入口の実フォーム検証が終了しませんでした。");
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        }
    }
}
