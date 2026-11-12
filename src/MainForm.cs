using System;
using System.Drawing;
using System.Windows.Forms;

namespace WarpSwitchNext
{
    /// <summary>The main window. Built in code at 96 DPI; Windows scales it for larger displays.</summary>
    sealed class MainForm : Form
    {
        static readonly string[] ModeKeys = { "auto", "fragment", "compatible" };
        static readonly string[] ModeNames = { "自动尝试", "握手分片", "兼容模式" };

        readonly AppController c;
        public event Action ExitRequested;

        readonly StatusRing ring = new StatusRing();
        readonly Label stateLabel = new Label(), messageLabel = new Label();
        readonly InfoPanel info = new InfoPanel();
        readonly Label ipValue = new Label(), endpointValue = new Label(), sniValue = new Label(), coloValue = new Label(), proxyValue = new Label();
        readonly LinkLabel editLink = new LinkLabel();
        readonly ComboBox modeBox = new ComboBox();
        readonly ToggleSwitch autoSwitch = new ToggleSwitch();
        readonly PillButton mainButton = new PillButton();
        readonly LinkLabel retryLink = new LinkLabel(), logLink = new LinkLabel(), exitLink = new LinkLabel();
        readonly Label footer = new Label();
        readonly LinkLabel termsLink = new LinkLabel();
        bool updating;

        public MainForm(AppController controller)
        {
            c = controller;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "WARP 新开关";
            Icon = Ui.AppIcon();
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(420, 748);
            BackColor = Palette.Window;
            Font = Ui.Font(9f);

            // Header
            var logo = new PictureBox { Bounds = new Rectangle(24, 22, 48, 48), SizeMode = PictureBoxSizeMode.Zoom, Image = Ui.AppImage(48) };
            var title = new Label { Text = "WARP 新开关", Bounds = new Rectangle(82, 20, 300, 30), Font = Ui.Font(15f, FontStyle.Bold), ForeColor = Palette.Text };
            var subtitle = new Label { Text = "不同入口 · HTTPS 连接 · 握手分片", Bounds = new Rectangle(84, 50, 300, 20), ForeColor = Palette.Secondary };

            // Status
            ring.Bounds = new Rectangle(148, 92, 124, 124);
            stateLabel.Bounds = new Rectangle(24, 226, 372, 36);
            stateLabel.TextAlign = ContentAlignment.MiddleCenter;
            stateLabel.Font = Ui.Font(17f, FontStyle.Bold);
            stateLabel.ForeColor = Palette.Text;
            messageLabel.Bounds = new Rectangle(24, 264, 372, 40);
            messageLabel.TextAlign = ContentAlignment.MiddleCenter;
            messageLabel.ForeColor = Palette.Secondary;

            // Details card
            info.Bounds = new Rectangle(24, 314, 372, 190);
            info.Rows = 5;
            AddRow(0, "出口 IP", ipValue);
            AddRow(1, "WARP 入口", endpointValue);
            AddRow(2, "伪装域名", sniValue, narrow: true);
            AddRow(3, "节点", coloValue);
            AddRow(4, "本机代理", proxyValue);
            editLink.Text = "修改";
            editLink.Bounds = new Rectangle(320, 2 * 38 + 10, 36, 20);
            editLink.TextAlign = ContentAlignment.MiddleRight;
            editLink.LinkBehavior = LinkBehavior.HoverUnderline;
            editLink.BackColor = Palette.Card;
            editLink.LinkClicked += (s, e) => c.EditDomain(this);
            info.Controls.Add(editLink);

            // Connection mode
            var modeLabel = new Label { Text = "连接模式", Bounds = new Rectangle(26, 520, 150, 24), TextAlign = ContentAlignment.MiddleLeft, ForeColor = Palette.Secondary };
            modeBox.DropDownStyle = ComboBoxStyle.DropDownList;
            modeBox.Items.AddRange(ModeNames);
            modeBox.Bounds = new Rectangle(246, 519, 150, 26);
            modeBox.SelectedIndexChanged += (s, e) => { if (!updating && modeBox.SelectedIndex >= 0) c.SetMode(ModeKeys[modeBox.SelectedIndex]); };

            // Auto-connect
            var autoLabel = new Label { Text = "开机时自动连接", Bounds = new Rectangle(26, 556, 260, 20), TextAlign = ContentAlignment.MiddleLeft, ForeColor = Palette.Secondary };
            var autoCaption = new Label { Text = "唤醒、网络恢复后也会自动重连", Bounds = new Rectangle(26, 576, 260, 18), Font = Ui.Font(8f), ForeColor = Palette.Tertiary };
            autoSwitch.Bounds = new Rectangle(354, 564, 42, 22);
            autoSwitch.AccessibleName = "开机时自动连接";
            autoSwitch.Toggle += (s, e) => c.SetAutoConnect(!c.AutoConnect);

            // Main button
            mainButton.Bounds = new Rectangle(24, 610, 372, 50);
            mainButton.Font = Ui.Font(13f, FontStyle.Bold);
            mainButton.Click += (s, e) => { if (c.Working) c.UserStop(); else c.UserStart(); };

            // Small actions
            Link(retryLink, "换入口重试", new Rectangle(26, 674, 120, 22), ContentAlignment.MiddleLeft, () => c.Retry());
            Link(logLink, "打开日志", new Rectangle(244, 674, 76, 22), ContentAlignment.MiddleRight, () => Util.OpenFolder(c.DataDir));
            Link(exitLink, "退出", new Rectangle(326, 674, 70, 22), ContentAlignment.MiddleRight, () => ExitRequested?.Invoke());

            // Footer
            footer.Bounds = new Rectangle(24, 704, 372, 18);
            footer.TextAlign = ContentAlignment.MiddleCenter;
            footer.Font = Ui.Font(8f);
            footer.ForeColor = Palette.Tertiary;
            termsLink.Text = "Cloudflare 服务条款";
            termsLink.Bounds = new Rectangle(24, 722, 372, 18);
            termsLink.TextAlign = ContentAlignment.MiddleCenter;
            termsLink.Font = Ui.Font(8f);
            termsLink.LinkBehavior = LinkBehavior.HoverUnderline;
            termsLink.LinkClicked += (s, e) => Util.OpenUrl("https://www.cloudflare.com/terms/");

            Controls.AddRange(new Control[]
            {
                logo, title, subtitle, ring, stateLabel, messageLabel, info,
                modeLabel, modeBox, autoLabel, autoCaption, autoSwitch, mainButton,
                retryLink, logLink, exitLink, footer, termsLink,
            });
            mainButton.TabIndex = 0;
            ResumeLayout(false);

            c.Changed += RefreshView;
            RefreshView();
        }

        void AddRow(int index, string name, Label value, bool narrow = false)
        {
            int y = index * 38 + 10;
            var key = new Label { Text = name, Bounds = new Rectangle(16, y, 110, 20), TextAlign = ContentAlignment.MiddleLeft, ForeColor = Palette.Secondary, BackColor = Palette.Card };
            value.Bounds = new Rectangle(126, y, narrow ? 190 : 230, 20);
            value.TextAlign = ContentAlignment.MiddleRight;
            value.Font = Ui.Mono(10f);
            value.ForeColor = Palette.Text;
            value.BackColor = Palette.Card;
            value.AutoEllipsis = true;
            info.Controls.Add(key);
            info.Controls.Add(value);
        }

        static void Link(LinkLabel link, string text, Rectangle bounds, ContentAlignment align, Action action)
        {
            link.Text = text;
            link.Bounds = bounds;
            link.TextAlign = align;
            link.LinkBehavior = LinkBehavior.HoverUnderline;
            link.LinkClicked += (s, e) => action();
        }

        public void RefreshView()
        {
            if (IsDisposed) return;
            updating = true;
            var s = c.State;
            ring.Tint = Palette.ForPhase(s.Phase);
            ring.Busy = c.Busy;
            ring.Connected = s.Phase == "on";
            stateLabel.Text = c.Title;
            messageLabel.Text = s.Message;
            ipValue.Text = s.Ip;
            endpointValue.Text = s.Endpoint;
            sniValue.Text = c.Settings.Sni;
            coloValue.Text = s.Colo;
            proxyValue.Text = Ports.ProxyAddress;
            editLink.Enabled = !c.Working;

            int mode = Array.IndexOf(ModeKeys, c.Settings.Mode);
            modeBox.SelectedIndex = mode < 0 ? 0 : mode;
            modeBox.Enabled = !c.Working;
            autoSwitch.Checked = c.AutoConnect;
            autoSwitch.Enabled = !c.NeedsSetup;

            mainButton.Text = c.Working ? (c.Registering ? "取消注册" : "关闭") : (c.NeedsSetup ? "同意条款并注册" : "开启");
            if (c.Working) mainButton.SetAccent(Palette.Red, Palette.RedHover);
            else mainButton.SetAccent(Palette.Blue, Palette.BlueHover);
            mainButton.Enabled = !c.Stopping;
            retryLink.Enabled = !c.Stopping && !c.NeedsSetup;

            footer.Text = c.NeedsSetup ? "首次使用将向 Cloudflare 注册属于你自己的免费账号。" : "通过系统代理连接。服务器公钥校验保持开启。";
            termsLink.Visible = c.NeedsSetup;
            updating = false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) c.Changed -= RefreshView;
            base.Dispose(disposing);
        }
    }

    static class Dialogs
    {
        /// <summary>Asks for a new 伪装域名; returns null when cancelled.</summary>
        public static string AskDomain(IWin32Window owner, string current)
        {
            using (var form = new Form())
            {
                form.SuspendLayout();
                form.AutoScaleDimensions = new SizeF(96F, 96F);
                form.AutoScaleMode = AutoScaleMode.Dpi;
                form.Text = "修改伪装域名";
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false; form.MinimizeBox = false; form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.CenterParent;
                form.ClientSize = new Size(400, 176);
                form.Font = Ui.Font(9f);
                var text = new Label
                {
                    Text = "这是连接时展示的 TLS 域名。它不会把入口 IP 变成该网站的 IP，也不能保证网络设备识别不到 WARP。修改后请重新连接。",
                    Bounds = new Rectangle(16, 14, 368, 56),
                };
                var box = new TextBox { Text = current, Bounds = new Rectangle(16, 78, 368, 26) };
                var ok = new Button { Text = "保存", DialogResult = DialogResult.OK, Bounds = new Rectangle(208, 128, 84, 32) };
                var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(300, 128, 84, 32) };
                form.AcceptButton = ok; form.CancelButton = cancel;
                form.Controls.AddRange(new Control[] { text, box, ok, cancel });
                form.ResumeLayout(false);
                form.Shown += (s, e) => { box.Focus(); box.SelectAll(); };
                return form.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
            }
        }
    }
}
