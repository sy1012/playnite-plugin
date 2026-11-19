using Playnite.SDK;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Threading;

namespace RomM.Saves
{
    /// <summary>
    /// A Windows notification for a save sync. Playnite's own notifications only collect behind
    /// its bell icon; a sync runs as a game starts or ends, when Playnite is not in view, so the
    /// notification has to come from Windows itself. A tray icon's balloon is the one route open
    /// to a .NET Framework plugin without WinRT, and Windows 10 and 11 show it as a toast. The icon
    /// lives only as long as the notification.
    /// </summary>
    internal sealed class SaveSyncToast
    {
        private static readonly TimeSpan Shown = TimeSpan.FromSeconds(8);

        private readonly Dispatcher _ui;
        private readonly ILogger _logger;

        public SaveSyncToast(Dispatcher ui, ILogger logger)
        {
            _ui = ui;
            _logger = logger;
        }

        public void Show(string title, string text, bool isError)
        {
            if (_ui == null || string.IsNullOrEmpty(text))
                return;

            _ui.BeginInvoke(new Action(() =>
            {
                try
                {
                    var icon = new NotifyIcon
                    {
                        Icon = PlayniteIcon(),
                        Text = "RomM save sync",
                        Visible = true,
                    };

                    var timer = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = Shown };
                    timer.Tick += (s, e) =>
                    {
                        timer.Stop();
                        icon.Visible = false;
                        icon.Dispose();
                    };
                    timer.Start();

                    icon.ShowBalloonTip((int)Shown.TotalMilliseconds, title, text,
                        isError ? ToolTipIcon.Error : ToolTipIcon.Info);
                }
                catch (Exception ex)
                {
                    _logger?.Warn($"[SaveSync] Could not show a notification: {ex.Message}");
                }
            }));
        }

        private static Icon PlayniteIcon()
        {
            try
            {
                return Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule.FileName) ?? SystemIcons.Information;
            }
            catch
            {
                return SystemIcons.Information;
            }
        }
    }
}
