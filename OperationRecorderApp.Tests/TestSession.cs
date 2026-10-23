using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OperationRecorderApp.Tests
{
    // テスト用の記録フォルダを一時フォルダに作る(画像はその場で描く。実際の画面は使わない)。
    //   手順 1: 「登録」ボタンをクリック      … 操作前と操作後が違う画面
    //   手順 2: Enter キー                    … 同じ画面(記録時に imagesSame = true と判定済み)
    //   手順 3: ホイールを下に 3 目盛り        … 違う画面
    //   手順 4: 「閉じる」ボタンをクリック    … 同じ画面だが imagesSame が無い(古い版の記録。ファイルを比べて判定する)
    public sealed class TestSession : IDisposable
    {
        public readonly string Root;        // 一時フォルダ(テストの後に消す)
        public readonly string Dir;         // 記録フォルダ
        public const string Name = "session-20261021-100000_テスト";

        // marksOutput = true: 画像に印を描かずに保存した記録(v1.3 から。出力するときに印を合成する)。false は v1.2 までの記録
        public TestSession(string mode = "Window", bool marksOutput = false)
        {
            Root = Path.Combine(Path.GetTempPath(), "oprec-test-" + Guid.NewGuid().ToString("N"));
            Dir = Path.Combine(Root, Name);
            Directory.CreateDirectory(Path.Combine(Dir, "img"));

            var session = new JsonObject
            {
                ["tool"] = "operation-recorder",
                ["mode"] = mode,
                ["virtualScreen"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["w"] = 3840, ["h"] = 1080 },
                ["start"] = "2026-10-21T10:00:00",
                ["end"] = "2026-10-21T10:00:30",
                ["beforeMs"] = 200,
                ["afterMs"] = 500,
                ["caption"] = "受注登録",
                ["description"] = "テスト用の記録です。",
                ["targetWindow"] = new JsonObject { ["hwnd"] = "0x1234", ["title"] = "販売管理", ["className"] = "TestWindow" },
                ["eventCount"] = 4,
            };
            if (marksOutput) session["marks"] = "output";
            File.WriteAllText(Path.Combine(Dir, "session.json"), session.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

            var lines = new List<string>
            {
                Click(1, "2026-10-21T10:00:01.000", "登録", "Button", "ボタン", "btnRegister", Color.LightBlue, Color.LightGreen, same: false),
                Key(2, "2026-10-21T10:00:05.000", "Enter", Color.Khaki),
                Wheel(3, "2026-10-21T10:00:10.000", -3, Color.LightPink, Color.Plum),
                Click(4, "2026-10-21T10:00:20.000", "閉じる", "Button", "ボタン", "btnClose", Color.Wheat, Color.Wheat, same: null),
            };
            File.WriteAllLines(Path.Combine(Dir, "events.jsonl"), lines, new UTF8Encoding(false));
        }

        public string ImagePath(int seq, string which) => Path.Combine(Dir, "img", seq.ToString("0000") + "_" + which + ".png");

        static JsonObject Window() => new JsonObject
        {
            ["hwnd"] = "0x1234", ["title"] = "販売管理", ["className"] = "TestWindow", ["processName"] = "hanbai", ["pid"] = 4321,
            ["frame"] = new JsonObject { ["x"] = 100, ["y"] = 100, ["w"] = 320, ["h"] = 200 },
            ["program"] = new JsonObject { ["name"] = "販売管理", ["version"] = "1.0.0", ["company"] = "テスト社", ["exe"] = "HANBAI.EXE", ["commandLine"] = "HANBAI.EXE" },
        };

        static JsonObject Uia(string name, string type, string localized, string id) => new JsonObject
        {
            ["controlType"] = type, ["localizedControlType"] = localized, ["name"] = name, ["automationId"] = id, ["className"] = "TestButton",
            ["frameworkId"] = "WinForm", ["isEnabled"] = true, ["isPassword"] = false,
            ["boundsInWindow"] = new JsonObject { ["x"] = 10, ["y"] = 10, ["w"] = 80, ["h"] = 24 },
            ["ancestors"] = new JsonArray(new JsonObject { ["controlType"] = "Window", ["name"] = "販売管理", ["automationId"] = "", ["className"] = "TestWindow" }),
        };

        JsonObject Image(int seq, string which, Color color, int offsetMs)
        {
            using (var b = new Bitmap(320, 200))
            {
                using (var g = Graphics.FromImage(b)) g.Clear(color);
                b.Save(ImagePath(seq, which), ImageFormat.Png);
            }
            return new JsonObject
            {
                ["file"] = "img/" + seq.ToString("0000") + "_" + which + ".png", ["offsetMs"] = offsetMs, ["offsetBasis"] = "eventStart",
                ["area"] = new JsonObject { ["x"] = 100, ["y"] = 100, ["w"] = 320, ["h"] = 200 }, ["masked"] = false,
            };
        }

        string Click(int seq, string time, string name, string type, string localized, string id, Color before, Color after, bool? same)
        {
            var e = new JsonObject
            {
                ["seq"] = seq, ["kind"] = "click", ["time"] = time, ["tMs"] = seq * 1000, ["button"] = "left",
                ["screen"] = new JsonObject { ["x"] = 150, ["y"] = 120 }, ["window"] = Window(),
                ["pointInWindow"] = new JsonObject { ["x"] = 50, ["y"] = 20 }, ["pointInClient"] = new JsonObject { ["x"] = 49, ["y"] = 0 },
                ["uia"] = Uia(name, type, localized, id), ["input"] = null,
            };
            if (same.HasValue) e["imagesSame"] = same.Value;
            e["markRect"] = new JsonObject { ["x"] = 110, ["y"] = 110, ["w"] = 80, ["h"] = 24 };   // 部品の画面上の位置(ウィンドウ 100,100 + 10,10)
            e["before"] = Image(seq, "before", before, -200);
            e["after"] = Image(seq, "after", after, 500);
            return e.ToJsonString();
        }

        string Key(int seq, string time, string key, Color color)
        {
            var e = new JsonObject
            {
                ["seq"] = seq, ["kind"] = "key", ["time"] = time, ["tMs"] = seq * 1000, ["key"] = key, ["window"] = Window(),
                ["uia"] = Uia("数量", "Edit", "編集", "txtQty"), ["input"] = null, ["imagesSame"] = true,
            };
            e["before"] = Image(seq, "before", color, -200);
            e["after"] = Image(seq, "after", color, 500);
            return e.ToJsonString();
        }

        string Wheel(int seq, string time, int net, Color before, Color after)
        {
            var e = new JsonObject
            {
                ["seq"] = seq, ["kind"] = "wheel", ["time"] = time, ["tMs"] = seq * 1000,
                ["screen"] = new JsonObject { ["x"] = 200, ["y"] = 180 }, ["window"] = Window(),
                ["pointInWindow"] = new JsonObject { ["x"] = 100, ["y"] = 80 },
                ["wheel"] = new JsonObject { ["direction"] = net > 0 ? "up" : "down", ["notches"] = Math.Abs(net) },
                ["uia"] = Uia("一覧", "List", "一覧", "lstItems"), ["input"] = null, ["imagesSame"] = false,
            };
            e["before"] = Image(seq, "before", before, -200);
            e["after"] = Image(seq, "after", after, 500);
            return e.ToJsonString();
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch { }
        }

        // ---------- 出力の確かめに使う小道具 ----------

        public static int Count(string text, string part)
        {
            int n = 0, i = 0;
            while ((i = text.IndexOf(part, i, StringComparison.Ordinal)) >= 0) { n++; i += part.Length; }
            return n;
        }

        // md の「## 手順 N」ごとに分ける(N → その手順の本文)
        public static Dictionary<int, string> Steps(string md)
        {
            var r = new Dictionary<int, string>();
            var parts = System.Text.RegularExpressions.Regex.Split(md, @"(?m)^## 手順 ");
            foreach (var p in parts.Skip(1))
            {
                var m = System.Text.RegularExpressions.Regex.Match(p, @"^(\d+)");
                if (m.Success) r[int.Parse(m.Groups[1].Value)] = p;
            }
            return r;
        }
    }
}
