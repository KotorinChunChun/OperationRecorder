using System;
using System.Collections.Generic;
using System.Linq;

namespace OpRec
{
    // 記録の操作キー(開始・取消・取り直し・終了)。画面やフックに頼らない部分だけなので、自動テストから直接確かめられる
    public enum HotkeyAction { Start, Undo, Restart, Stop }

    public sealed class Hotkey
    {
        public bool Ctrl { get; set; }
        public bool Shift { get; set; }
        public bool Alt { get; set; }
        public bool Win { get; set; }
        public int Key { get; set; }           // 仮想キーコード(0 = 未設定)

        public Hotkey() { }
        public Hotkey(bool ctrl, bool shift, bool alt, bool win, int key) { Ctrl = ctrl; Shift = shift; Alt = alt; Win = win; Key = key; }

        public Hotkey Clone() => new Hotkey(Ctrl, Shift, Alt, Win, Key);

        public bool Matches(int vk, bool ctrl, bool shift, bool alt, bool win) =>
            Key != 0 && vk == Key && ctrl == Ctrl && shift == Shift && alt == Alt && win == Win;

        public bool SameAs(Hotkey o) => o != null && o.Key == Key && o.Ctrl == Ctrl && o.Shift == Shift && o.Alt == Alt && o.Win == Win;

        // RegisterHotKey に渡す修飾キー(MOD_NOREPEAT 付き)
        public uint Modifiers => (Alt ? 0x1u : 0) | (Ctrl ? 0x2u : 0) | (Shift ? 0x4u : 0) | (Win ? 0x8u : 0) | 0x4000u;

        // 「Ctrl+Shift+F9」のような表示
        public override string ToString()
        {
            var parts = new List<string>();
            if (Ctrl) parts.Add("Ctrl");
            if (Shift) parts.Add("Shift");
            if (Alt) parts.Add("Alt");
            if (Win) parts.Add("Win");
            parts.Add(Key == 0 ? "(未設定)" : HotkeyKeys.NameOf(Key));
            return string.Join("+", parts);
        }
    }

    public sealed class HotkeySet
    {
        public Hotkey Start { get; set; } = new Hotkey(true, true, false, false, 0x78);     // Ctrl+Shift+F9
        public Hotkey Undo { get; set; } = new Hotkey(true, true, false, false, 0x79);      // Ctrl+Shift+F10
        public Hotkey Restart { get; set; } = new Hotkey(true, true, false, false, 0x7A);   // Ctrl+Shift+F11
        public Hotkey Stop { get; set; } = new Hotkey(true, true, false, false, 0x7B);      // Ctrl+Shift+F12

        public static HotkeySet Defaults() => new HotkeySet();

        public static readonly HotkeyAction[] Order = { HotkeyAction.Start, HotkeyAction.Undo, HotkeyAction.Restart, HotkeyAction.Stop };

        public static string Label(HotkeyAction a)
        {
            switch (a)
            {
                case HotkeyAction.Start: return "記録の開始・再開";
                case HotkeyAction.Undo: return "直前の 1 手を取り消す";
                case HotkeyAction.Restart: return "最初から取り直す";
                default: return "保存して終了";
            }
        }

        public Hotkey Get(HotkeyAction a)
        {
            switch (a)
            {
                case HotkeyAction.Start: return Start;
                case HotkeyAction.Undo: return Undo;
                case HotkeyAction.Restart: return Restart;
                default: return Stop;
            }
        }

        public void Set(HotkeyAction a, Hotkey k)
        {
            switch (a)
            {
                case HotkeyAction.Start: Start = k; break;
                case HotkeyAction.Undo: Undo = k; break;
                case HotkeyAction.Restart: Restart = k; break;
                default: Stop = k; break;
            }
        }

        public HotkeySet Clone()
        {
            var c = new HotkeySet();
            foreach (var a in Order) c.Set(a, (Get(a) ?? new Hotkey()).Clone());
            return c;
        }

        // 押されたキーがどの操作キーか(どれでもなければ null)
        public HotkeyAction? ActionOf(int vk, bool ctrl, bool shift, bool alt, bool win)
        {
            foreach (var a in Order)
            {
                var k = Get(a);
                if (k != null && k.Matches(vk, ctrl, shift, alt, win)) return a;
            }
            return null;
        }

        // ふだんのアプリの操作とぶつかるので、操作キーにしないもの
        static readonly string[] Reserved = { "Alt+F4", "Ctrl+Alt+Delete", "Win+L", "Win+D", "Win+E", "Win+R",
            "Ctrl+A", "Ctrl+C", "Ctrl+V", "Ctrl+X", "Ctrl+Z", "Ctrl+Y", "Ctrl+S", "Ctrl+P", "Ctrl+F", "Ctrl+N", "Ctrl+O", "Ctrl+W" };

        // 設定の誤りを、画面にそのまま出せる文章で返す(空なら使える)
        public List<string> Validate()
        {
            var errors = new List<string>();
            foreach (var a in Order)
            {
                var k = Get(a);
                string name = Label(a);
                if (k == null || k.Key == 0) { errors.Add(name + ": キーを選んでください"); continue; }
                if (!HotkeyKeys.IsKnown(k.Key)) errors.Add(name + ": このキーは使えません(" + k.Key + ")");
                if (!k.Ctrl && !k.Alt && !k.Win)
                    errors.Add(name + ": Ctrl・Alt・Win のどれかと組み合わせてください(修飾キーなしや Shift だけでは、ふだんの入力と区別できません)");
                if (Reserved.Contains(k.ToString())) errors.Add(name + ": " + k + " は Windows やアプリでよく使うキーなので使えません");
            }
            for (int i = 0; i < Order.Length; i++)
                for (int j = i + 1; j < Order.Length; j++)
                {
                    var a = Get(Order[i]); var b = Get(Order[j]);
                    if (a != null && a.Key != 0 && a.SameAs(b))
                        errors.Add(Label(Order[i]) + "と" + Label(Order[j]) + "が同じキー(" + a + ")です");
                }
            return errors;
        }
    }

    // 操作キーに選べる通常キーの一覧(画面のコンボボックスの中身)
    public static class HotkeyKeys
    {
        public sealed class Item
        {
            public int Vk; public string Name;
            public override string ToString() => Name;
        }

        public static readonly Item[] All = Build();

        static Item[] Build()
        {
            var list = new List<Item>();
            for (int i = 1; i <= 24; i++) list.Add(new Item { Vk = 0x6F + i, Name = "F" + i });
            for (char c = 'A'; c <= 'Z'; c++) list.Add(new Item { Vk = c, Name = c.ToString() });
            for (char c = '0'; c <= '9'; c++) list.Add(new Item { Vk = c, Name = c.ToString() });
            list.Add(new Item { Vk = 0x2D, Name = "Insert" });
            list.Add(new Item { Vk = 0x2E, Name = "Delete" });
            list.Add(new Item { Vk = 0x24, Name = "Home" });
            list.Add(new Item { Vk = 0x23, Name = "End" });
            list.Add(new Item { Vk = 0x21, Name = "PageUp" });
            list.Add(new Item { Vk = 0x22, Name = "PageDown" });
            list.Add(new Item { Vk = 0x13, Name = "Pause" });
            list.Add(new Item { Vk = 0x91, Name = "ScrollLock" });
            return list.ToArray();
        }

        public static bool IsKnown(int vk) => All.Any(i => i.Vk == vk);

        public static string NameOf(int vk) => All.FirstOrDefault(i => i.Vk == vk)?.Name ?? ("0x" + vk.ToString("X2"));

        // 「F9」「a」のような名前から仮想キーコード(知らない名前は 0)
        public static int Parse(string name) =>
            All.FirstOrDefault(i => string.Equals(i.Name, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase))?.Vk ?? 0;
    }
}
