using System;
using System.Threading;
using System.Windows;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DownloadsFlyout
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            using var mutex = new Mutex(true, "DownloadsFlyout_SingleInstance", out bool isNew);
            if (!isNew) return;
            new TrayApp().Run();
        }
    }

    class TrayApp : Application
    {
        Forms.NotifyIcon tray;
        FlyoutWindow flyout;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            flyout = new FlyoutWindow();

            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open", null, (s, a) => flyout.Toggle());
            menu.Items.Add("Choose folder...", null, (s, a) => flyout.ChooseFolder());
            var startup = new Forms.ToolStripMenuItem("Start with Windows")
            {
                CheckOnClick = true,
                Checked = Settings.StartWithWindows
            };
            startup.Click += (s, a) => Settings.StartWithWindows = startup.Checked;
            menu.Items.Add(startup);
            menu.Items.Add("Quit", null, (s, a) => { tray.Visible = false; tray.Dispose(); Shutdown(); });

            tray = new Forms.NotifyIcon
            {
                Icon = MakeIcon(),
                Text = "Downloads",
                Visible = true,
                ContextMenuStrip = menu
            };
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += (s, a) =>
                Dispatcher.BeginInvoke(new Action(() => tray.Icon = MakeIcon()));
            tray.MouseUp += (s, a) => { if (a.Button == Forms.MouseButtons.Left) flyout.Toggle(); };
        }

        static Drawing.Icon MakeIcon()
        {
            var color = Settings.IsLightTheme(system: true) ? Drawing.Color.Black : Drawing.Color.White;
            using var bmp = new Drawing.Bitmap(32, 32);
            using (var g = Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var pen = new Drawing.Pen(color, 3.5f)
                {
                    StartCap = System.Drawing.Drawing2D.LineCap.Round,
                    EndCap = System.Drawing.Drawing2D.LineCap.Round,
                    LineJoin = System.Drawing.Drawing2D.LineJoin.Round
                };
                g.DrawLine(pen, 16, 5, 16, 21);
                g.DrawLines(pen, new[] { new Drawing.PointF(8, 14), new Drawing.PointF(16, 22), new Drawing.PointF(24, 14) });
                g.DrawLine(pen, 6, 27, 26, 27);
            }
            return Drawing.Icon.FromHandle(bmp.GetHicon());
        }
    }
}
