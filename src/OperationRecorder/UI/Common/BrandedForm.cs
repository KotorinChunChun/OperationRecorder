using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace OperationRecorderApp
{
    internal sealed class BrandedForm : Form
    {
        readonly Icon appIcon;

        public BrandedForm()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            appIcon = File.Exists(path) ? new Icon(path) : (Icon)AppIcons.Idle.Clone();
            Icon = appIcon;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) appIcon.Dispose();
        }
    }
}
