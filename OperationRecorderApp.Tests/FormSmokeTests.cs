using System;
using System.Threading;
using System.Windows.Forms;
using Xunit;

namespace OperationRecorderApp.Tests
{
    // 画面(フォーム)を実際に表示して、表示しただけで例外が出ないかを確かめる(クリックなどの操作はしない)。
    // ListView のチェックのように、ハンドルを作るときに初めて呼ばれるイベントの誤りは、作るだけのテストでは見つからない
    public class FormSmokeTests
    {
        static Exception ShowBriefly(Func<Form> create)
        {
            Exception err = null;
            var t = new Thread(() =>
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => err = err ?? e.Exception;
                try
                {
                    using (var f = create())
                    {
                        f.StartPosition = FormStartPosition.Manual;
                        f.Location = new System.Drawing.Point(-32000, -32000);   // 画面の外に出す
                        f.ShowInTaskbar = false;
                        f.Show();
                        for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(15); }
                        f.Close();
                    }
                }
                catch (Exception ex) { err = err ?? ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            return err;
        }

        [Fact]
        public void 記録の合成の画面を表示できる()
        {
            using (var a = new TestSession())
            using (var b = new TestSession(marksOutput: true))
            {
                var err = ShowBriefly(() => new MergeForm(a.Dir, new[] { (b.Dir, "B") }));
                Assert.True(err == null, err?.ToString());
            }
        }

        [Fact]
        public void テンプレートのエディタを表示できる()
        {
            using (var s = new TestSession())
            {
                var err = ShowBriefly(() => new TemplateEditorForm(System.IO.Path.Combine(AppContext.BaseDirectory, "templates"), s.Dir));
                Assert.True(err == null, err?.ToString());
            }
        }
    }
}
