using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace OperationRecorderApp
{
    internal static class CommandConsole
    {
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int processId);
        [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int handle);
        public static void Prepare()
        {
            if (GetStdHandle(-11) == IntPtr.Zero) AttachConsole(-1);
            var encoding = new UTF8Encoding(false);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), encoding) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), encoding) { AutoFlush = true });
        }
    }
}
