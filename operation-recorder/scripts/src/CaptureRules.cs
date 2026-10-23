using System.Collections.Generic;

namespace OpRec
{
    // 撮影ルール: 操作の種類ごとに「記録するか」「操作前を撮るか」「操作後を撮るか」を決める
    public sealed class CaptureRule
    {
        public bool Record { get; set; } = true;
        public bool Before { get; set; } = true;
        public bool After { get; set; } = true;

        public CaptureRule() { }
        public CaptureRule(bool record, bool before, bool after) { Record = record; Before = before; After = after; }
        public CaptureRule Clone() => new CaptureRule(Record, Before, After);
    }

    public static class CaptureRules
    {
        public const string LeftClick = "leftClick";       // 左クリック(ダブルクリック・ドラッグを含む)
        public const string RightClick = "rightClick";     // 右クリック(右ドラッグを含む)
        public const string MiddleClick = "middleClick";   // 中クリック
        public const string MouseWheel = "mouseWheel";     // マウスホイール(続けて回した分は 1 手順にまとめる)
        // キーは種類ごとのグループで、まとめて記録する / しないを決める
        public const string Key = "key";                   // Ctrl / Alt / Win との組み合わせ(Ctrl+S など)。v1.2 までは「操作キー」全部だった
        public const string KeyLetters = "keyLetters";     // A〜Z(修飾キーなし。入力欄の中では文字入力として扱う)
        public const string KeyDigits = "keyDigits";       // 1〜0(テンキーを含む。修飾キーなし。入力欄の中では文字入力として扱う)
        public const string KeyFunction = "keyFunction";   // F1〜F24
        public const string KeyArrows = "keyArrows";       // 十字キー(← ↑ → ↓)
        public const string KeySpecial = "keySpecial";     // 特殊キー(Enter / Tab / Esc / BackSpace / Delete / Home / End / PageUp / PageDown など)
        public const string TextInput = "textInput";       // テキストボックスへの入力(操作前 = フォーカスイン、操作後 = フォーカスアウト)
        public const string CtrlShift = "ctrlShift";       // Ctrl+Shift だけを押して離す(手動キャプチャ)
        public const string WinShiftS = "winShiftS";       // Win+Shift+S(Windows の切り取り。押した瞬間の画面をキャプチャする)

        // 表示順と、画面に出す名前
        public static readonly string[] Order = { LeftClick, RightClick, MiddleClick, MouseWheel,
            KeyLetters, KeyDigits, KeyFunction, KeyArrows, KeySpecial, Key, TextInput, CtrlShift, WinShiftS };

        // キーのグループ(Key と KeyLetters〜KeySpecial)
        public static readonly string[] KeyGroups = { KeyLetters, KeyDigits, KeyFunction, KeyArrows, KeySpecial, Key };

        // 押したキーがどのグループか。Ctrl / Alt / Win が付いていれば組み合わせ(Key)、Shift だけなら付いていないものとして扱う
        public static string KeyGroupOf(int vk, bool ctrl, bool alt, bool win)
        {
            if (ctrl || alt || win) return Key;
            if (vk >= 0x41 && vk <= 0x5A) return KeyLetters;
            if ((vk >= 0x30 && vk <= 0x39) || (vk >= 0x60 && vk <= 0x69)) return KeyDigits;
            if (vk >= 0x70 && vk <= 0x87) return KeyFunction;
            if (vk >= 0x25 && vk <= 0x28) return KeyArrows;
            return KeySpecial;
        }

        public static string Label(string kind)
        {
            switch (kind)
            {
                case LeftClick: return "左クリック(ダブルクリック・ドラッグを含む)";
                case RightClick: return "右クリック";
                case MiddleClick: return "中クリック(ホイールボタン)";
                case MouseWheel: return "マウスホイール(続けて回した分は 1 手順にまとめ、回して戻した分は差し引く)";
                case KeyLetters: return "キー: A〜Z(入力欄の中では文字入力として扱う)";
                case KeyDigits: return "キー: 1〜0(テンキーを含む。入力欄の中では文字入力として扱う)";
                case KeyFunction: return "キー: F1〜F24";
                case KeyArrows: return "キー: 十字キー(← ↑ → ↓)";
                case KeySpecial: return "キー: 特殊キー(Enter・Tab・Esc・BackSpace・Delete・Home・End・PageUp・PageDown など)";
                case Key: return "キー: Ctrl / Alt / Win との組み合わせ(Ctrl+S・Alt+F など)";
                case TextInput: return "テキスト入力(操作前 = フォーカスイン、操作後 = フォーカスアウト)";
                case CtrlShift: return "Ctrl+Shift(手動キャプチャ)";
                case WinShiftS: return "Win+Shift+S(押した瞬間の画面をキャプチャ)";
                default: return kind;
            }
        }

        public static Dictionary<string, CaptureRule> Defaults()
        {
            var d = new Dictionary<string, CaptureRule>();
            foreach (var k in Order) d[k] = new CaptureRule();
            d[MouseWheel].Record = false;   // ホイールは手順として細かくなりすぎるので、既定では記録しない
            // キーは既定では記録しない(手順が細かくなりすぎるので、必要なグループだけ「記録する」にする)
            foreach (var k in KeyGroups) d[k].Record = false;
            return d;
        }
    }
}
