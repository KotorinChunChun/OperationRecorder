using System.Drawing;

namespace OperationRecorderApp
{
    internal sealed class OverlayProximity
    {
        const int Near = 100;
        const int Rearm = 160;
        bool keepVisible;

        public void Reset() => keepVisible = false;

        public bool IsTransparent(Rectangle bounds, Point pointer, bool modifier)
        {
            bool near = Rectangle.Inflate(bounds, Near, Near).Contains(pointer);
            if (!Rectangle.Inflate(bounds, Rearm, Rearm).Contains(pointer)) keepVisible = false;
            if (near && modifier) keepVisible = true;
            return near && !modifier && !keepVisible;
        }
    }
}
