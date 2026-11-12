using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace WarpSwitchNext
{
    static class Program
    {
        [STAThread]
        static int Main(string[] argv)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var args = new List<string>(argv);
            string Option(string name)
            {
                int i = args.IndexOf(name);
                return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
            }

            // Tests used by the build. --self-test changes nothing on the PC.
            if (args.Contains("--self-test")) return SelfTest.Units(Option("--out"));
            if (args.Contains("--proxy-test")) return SelfTest.Proxy(Option("--out"));
            if (args.Contains("--connect-test")) return SelfTest.Connect(Option("--data"), Option("--out"));
            if (args.Contains("--screenshots"))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                return SelfTest.Screenshots(Option("--screenshots"), Option("--out"));
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            using (var instance = new Mutex(true, @"Local\WarpSwitchNext.Instance", out bool first))
            using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\WarpSwitchNext.Show"))
            {
                if (!first)
                {
                    show.Set(); // bring the running copy's window to the front
                    return 0;
                }
                var controller = new AppController(Paths.Data, Paths.Usque);
                Application.ThreadException += (s, e) =>
                {
                    new LogFile(System.IO.Path.Combine(controller.DataDir, "worker.log")).Line("UI error: " + e.Exception);
                    MessageBox.Show(e.Exception.Message, "WARP 新开关", MessageBoxButtons.OK, MessageBoxIcon.Error);
                };
                // Never leave the PC pointing at a proxy that is gone.
                Application.ApplicationExit += (s, e) => controller.EmergencyRestore();
                using (var context = new TrayContext(controller, args.Contains("--autostart"), show))
                    Application.Run(context);
                GC.KeepAlive(instance);
            }
            return 0;
        }
    }
}
