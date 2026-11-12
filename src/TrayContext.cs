using System;
using System.Threading;
using System.Windows.Forms;

namespace WarpSwitchNext
{
    /// <summary>
    /// Owns the window and the notification-area (tray) icon. Closing the window keeps the app
    /// running in the tray; 退出 closes the connection, restores the proxy, then exits.
    /// </summary>
    sealed class TrayContext : ApplicationContext
    {
        readonly AppController c;
        readonly MainForm form;
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ToolStripMenuItem statusItem = new ToolStripMenuItem { Enabled = false };
        readonly ToolStripMenuItem toggleItem = new ToolStripMenuItem();
        readonly ToolStripMenuItem retryItem = new ToolStripMenuItem("换入口重试");
        readonly ToolStripMenuItem autoItem = new ToolStripMenuItem("开机时自动连接");
        string lastPhase = "off";
        bool exiting;

        public TrayContext(AppController controller, bool autostart, EventWaitHandle showSignal)
        {
            c = controller;
            form = new MainForm(c);
            form.ExitRequested += Exit;
            form.FormClosing += OnFormClosing;
            // Create the window handle now so other threads can marshal onto it.
            var _ = form.Handle;

            var menu = new ContextMenuStrip { Font = Ui.Font(9f) };
            menu.Items.Add(statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("显示窗口", null, (s, e) => ShowWindow());
            menu.Items.Add(toggleItem);
            menu.Items.Add(retryItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(autoItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) => Exit());
            toggleItem.Click += (s, e) => { if (c.Working) c.UserStop(); else c.UserStart(); };
            retryItem.Click += (s, e) => c.Retry();
            autoItem.Click += (s, e) => c.SetAutoConnect(!c.AutoConnect);

            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
            tray.Visible = true;
            c.Changed += Refresh;
            Refresh();

            // A second copy of the app was opened: show this window instead.
            new Thread(() =>
            {
                while (showSignal.WaitOne())
                {
                    try { form.BeginInvoke((Action)ShowWindow); } catch { return; }
                }
            }) { IsBackground = true, Name = "ShowSignal" }.Start();

            // Started by Windows at sign-in: stay in the tray and connect quietly.
            if (autostart)
            {
                if (c.AutoConnect && !c.NeedsSetup) c.Start();
            }
            else ShowWindow();
        }

        void Refresh()
        {
            var phase = c.State.Phase;
            tray.Icon = Ui.TrayIcon(phase);
            var text = "WARP 新开关 · " + c.Title;
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
            statusItem.Text = text;
            toggleItem.Text = c.Working ? "关闭连接" : c.NeedsSetup ? "同意条款并注册" : "开启连接";
            toggleItem.Enabled = !c.Stopping;
            retryItem.Enabled = !c.Stopping && !c.NeedsSetup;
            autoItem.Checked = c.AutoConnect;
            autoItem.Enabled = !c.NeedsSetup;

            // Tell the user when a working connection drops while the window is hidden.
            if (phase == "error" && (lastPhase == "on" || lastPhase == "reconnecting") && !form.Visible)
                tray.ShowBalloonTip(5000, "WARP 新开关", c.State.Message, ToolTipIcon.Warning);
            lastPhase = phase;
        }

        void ShowWindow()
        {
            if (form.IsDisposed) return;
            if (!form.Visible) form.Show();
            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
            form.Activate();
            form.BringToFront();
        }

        void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (exiting || e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            form.Hide();
            if (!c.Settings.TrayTipShown)
            {
                c.Settings.TrayTipShown = true;
                c.SaveSettings();
                tray.ShowBalloonTip(6000, "WARP 新开关仍在运行",
                    "可以在任务栏右下角的盾牌图标里打开窗口或退出。", ToolTipIcon.Info);
            }
        }

        void Exit()
        {
            if (exiting) return;
            exiting = true;
            form.Hide();
            c.Shutdown(() =>
            {
                tray.Visible = false;
                tray.Dispose();
                form.Close();
                ExitThread();
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { tray.Dispose(); form.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
