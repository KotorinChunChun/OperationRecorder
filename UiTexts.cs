using OpRec;

namespace OperationRecorderApp
{
    // 画面・トレイ・通知に出す文章のうち、操作キーの設定で変わるもの。画面に頼らないので自動テストから確かめられる
    public static class UiTexts
    {
        // オーバーレイに出す、使えるキーの一覧
        public static string OverlayShortcuts(HotkeySet k) =>
            k.Start + ": 開始 / Ctrl+Shift だけ: 画面キャプチャ\n" +
            k.Undo + ": 1 手取消 / " + k.Restart + ": 取り直し\n" +
            k.Stop + ": 保存して終了";

        public static string ForegroundWindowOption(HotkeySet k) => "記録開始のキー(" + k.Start + ")を押したときに、前面にあるウィンドウにする";

        public static string ForegroundMonitorOption(HotkeySet k) => "記録開始のキー(" + k.Start + ")を押したときに、前面にあるウィンドウが映っているディスプレイにする";


        public static string Status(RecState st, HotkeySet k)
        {
            switch (st)
            {
                case RecState.Recording: return "記録中です。" + k.Stop + " で保存 / " + k.Undo + " で 1 手取消 / " + k.Restart + " で取り直し / Ctrl+Shift だけで画面キャプチャ";
                case RecState.Saving: return "記録を保存しています…";
                default: return "待機中です。「記録を開始」ボタンか " + k.Start + " で記録を始めます。";
            }
        }

        // タスクトレイのアイコンにマウスを載せたときの文字(Windows の上限は 127 文字)
        public static string TrayTip(RecState st, HotkeySet k)
        {
            string s = "操作記録ツール - " + (st == RecState.Recording ? "記録中" : st == RecState.Saving ? "保存中" : "待機中(" + k.Start + " で開始)");
            return s.Length > 127 ? s.Substring(0, 127) : s;
        }

        public static string MenuStart(HotkeySet k) => "記録を開始(" + k.Start + ")";
        public static string MenuUndo(HotkeySet k) => "直前の 1 手を取り消す(" + k.Undo + ")";
        public static string MenuRestart(HotkeySet k) => "最初から記録し直す(" + k.Restart + ")";
        public static string MenuStop(HotkeySet k) => "保存して終了(" + k.Stop + ")";

        public static string NotifySaveKey(HotkeySet k) => k.Stop + " で保存します。";
        public static string NotifyRestarted(HotkeySet k) => "同じ設定で取り直せます。" + k.Start + " で記録を始めます。";
        public static string TrayHint(HotkeySet k) => k.Start + " でいつでも記録を始められます。終了するときは、トレイのアイコンを右クリックして「終了」を選んでください。";
        public static string ShortcutMenuNote(HotkeySet k) =>
            "・操作記録ツール … この画面を開く\n・操作記録ツール(すぐに記録)… 前回の設定ですぐ記録を始める(待機中は " + k.Start + " でも始められる)";
    }
}
