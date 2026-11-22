namespace OpRec
{
    // events.jsonl の kind に保存する操作の種類。文字列は保存形式・出力・リプレイと共有するので変えない
    internal static class EventKinds
    {
        public const string Click = "click", DoubleClick = "doubleclick", RightClick = "rightclick", MiddleClick = "middleclick";
        public const string MouseBack = "mouseback", MouseForward = "mouseforward";
        public const string Drag = "drag", RightDrag = "rightdrag";
        public const string Wheel = "wheel", Key = "key", Input = "input", Capture = "capture";
        public const string WindowOpen = "window-open", WindowSwitch = "window-switch";

        public static string ClickOf(int button) => button switch
        {
            RawButton.Left => Click, RawButton.Right => RightClick, RawButton.Middle => MiddleClick,
            RawButton.Back => MouseBack, _ => MouseForward,
        };
        public static string DragOf(int button) => button == RawButton.Right ? RightDrag : Drag;
        public static bool IsDrag(string kind) => kind == Drag || kind == RightDrag;
    }

    // WindowChangeTracker が観測したウィンドウ変化の種類(windowEvent.type に保存する)
    internal static class WindowChangeKinds
    {
        public const string Open = "open", Switch = "switch";
    }

    // Raw.Button / Ev.Button の値。openDown の添字にも使う
    internal static class RawButton
    {
        public const int Left = 0, Right = 1, Middle = 2, Back = 3, Forward = 4, Count = Forward + 1;

        // events.jsonl の button に保存する名前
        public static string Name(int button) => button switch
        {
            Left => "left", Right => "right", Middle => "middle", Back => "x1", _ => "x2",
        };
    }
}
