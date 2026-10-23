using Microsoft.VisualBasic.FileIO;

namespace OperationRecorderApp
{
    // ごみ箱へ移す(記録・手順の画像・テンプレートを消すとき。ごみ箱から元に戻せる)
    public static class RecycleBin
    {
        public static void SendFile(string path) =>
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);

        public static void SendDirectory(string path) =>
            FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
    }
}