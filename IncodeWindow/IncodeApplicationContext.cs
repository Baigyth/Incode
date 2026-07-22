// (C) 2015-20 christian.schladetsch@gmail.com

using System.Windows.Forms;

namespace Incode
{
    using System;
    using System.Diagnostics;
    using System.Drawing;

    internal class IncodeApplicationContext : ApplicationContext
    {
        private IncodeEngine _engine;
        private NotifyIcon _notifyIcon;

        public IncodeApplicationContext()
        {
            _engine = new IncodeEngine();

            var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            _notifyIcon = new NotifyIcon
            {
                Icon = icon,
                Text = "InCode",
                Visible = true
            };

            _notifyIcon.ContextMenuStrip = new ContextMenuStrip();
            _notifyIcon.ContextMenuStrip.Items.Add("Restart", null, (s, e) => Restart());
            _notifyIcon.ContextMenuStrip.Items.Add("Exit", null, (s, e) => Exit());
            _notifyIcon.DoubleClick += (s, e) => Restart();
        }

        private void Restart()
        {
            // Cleanup current instance
            _engine?.Dispose();
            if (_notifyIcon != null) _notifyIcon.Visible = false;
            _notifyIcon?.Dispose();

            // Launch new process
            Process.Start(Application.ExecutablePath);

            ExitThread();
        }

        private void Exit()
        {
            _engine?.Dispose();
            if (_notifyIcon != null) _notifyIcon.Visible = false;
            _notifyIcon?.Dispose();

            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _engine?.Dispose();
                if (_notifyIcon != null) _notifyIcon.Visible = false;
                _notifyIcon?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
