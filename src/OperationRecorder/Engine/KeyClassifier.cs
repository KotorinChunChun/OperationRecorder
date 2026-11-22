using System.Collections.Generic;
using System.Text;

namespace OpRec
{
    // 仮想キーの分類と表示名。OS の状態に依存しない判断だけを置く(表示名の最後の手段だけ GetKeyNameText を使う)
    internal static class KeyClassifier
    {
        public static bool IsModifier(int vk)
        {
            return vk == VK.Shift || vk == VK.Control || vk == VK.Menu || (vk >= VK.LShift && vk <= VK.RMenu) || vk == VK.LWin || vk == VK.RWin;
        }

        // 文字を入力するキー(0〜9・A〜Z・テンキー・記号)
        public static bool IsPrintable(int vk)
        {
            return (vk >= VK.D0 && vk <= VK.D9) || (vk >= VK.A && vk <= VK.Z) || (vk >= VK.NumPad0 && vk <= VK.Divide)
                || (vk >= VK.Oem1 && vk <= VK.Oem3) || (vk >= VK.Oem4 && vk <= VK.Oem8) || vk == VK.Oem102 || vk == VK.Packet;
        }

        // 日本語入力の切り替え・変換のキーと、CapsLock・NumLock・ScrollLock
        public static bool IsImeOrLockKey(int vk)
        {
            return vk == VK.Kana || vk == VK.Kanji || vk == VK.Convert || vk == VK.NonConvert || vk == VK.Accept || vk == VK.ModeChange
                || vk == VK.ProcessKey || (vk >= VK.DbeAlphanumeric && vk <= VK.DbeNoRoman)
                || vk == VK.Capital || vk == VK.NumLock || vk == VK.Scroll;
        }

        // テキストボックスの中では編集操作とみなすキー(外では操作として撮る)
        public static bool IsEditingKey(int vk)
        {
            return vk == VK.Space || vk == VK.Back || vk == VK.Delete || vk == VK.Insert || (vk >= VK.Prior && vk <= VK.Down);
        }

        // 日本語入力の変換中に押すと、変換の確定・取り消し・文字種の変換になるキー(Enter / Esc / F6〜F10)
        public static bool IsCompositionKey(int vk)
        {
            return vk == VK.Return || vk == VK.Escape || (vk >= VK.F6 && vk <= VK.F10);
        }

        // 変換中に押すと変換を終えるキー(Enter = 確定 / Esc = 取り消し)
        public static bool EndsComposition(int vk) => vk == VK.Return || vk == VK.Escape;

        // Alt+`(日本語キーボードの 半角/全角)は日本語入力の切り替え
        public static bool IsImeToggle(int vk, bool alt) => alt && vk == VK.Oem3;

        // Win+Shift+S(Windows の切り取り)
        public static bool IsWindowsSnip(int vk, bool ctrl, bool shift, bool alt, bool win) => win && shift && !ctrl && !alt && vk == VK.S;

        public static string KeyText(int vk, int scan, bool extended, bool ctrl, bool alt, bool shift, bool win)
        {
            var parts = new List<string>();
            if (ctrl) parts.Add("Ctrl");
            if (alt) parts.Add("Alt");
            if (shift) parts.Add("Shift");
            if (win) parts.Add("Win");
            parts.Add(VkName(vk, scan, extended));
            return string.Join("+", parts);
        }

        public static string VkName(int vk, int scan, bool extended)
        {
            switch (vk)
            {
                case VK.Return: return "Enter";
                case VK.Tab: return "Tab";
                case VK.Escape: return "Esc";
                case VK.Space: return "Space";
                case VK.Back: return "Backspace";
                case VK.Delete: return "Delete";
                case VK.Insert: return "Insert";
                case VK.Home: return "Home";
                case VK.End: return "End";
                case VK.Prior: return "PageUp";
                case VK.Next: return "PageDown";
                case VK.Left: return "←";
                case VK.Up: return "↑";
                case VK.Right: return "→";
                case VK.Down: return "↓";
                case VK.Apps: return "Apps";
                case VK.Snapshot: return "PrintScreen";
                case VK.Pause: return "Pause";
            }
            if (vk >= VK.F1 && vk <= VK.F24) return "F" + (vk - VK.F1 + 1);
            if ((vk >= VK.D0 && vk <= VK.D9) || (vk >= VK.A && vk <= VK.Z)) return ((char)vk).ToString();
            if (vk >= VK.NumPad0 && vk <= VK.NumPad9) return "Num" + (vk - VK.NumPad0);
            var sb = new StringBuilder(64);
            if (N.GetKeyNameText((scan << 16) | (extended ? 1 << 24 : 0), sb, sb.Capacity) > 0) return sb.ToString();
            return "VK_0x" + vk.ToString("X2");
        }
    }
}
