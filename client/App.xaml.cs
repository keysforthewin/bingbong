using System;
using System.Windows;

namespace bingbong
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var window = new MainWindow();
            window.Show();  // fully initializes HWND, fires Loaded, etc.
            window.Hide();  // immediately hide — app lives in tray
        }

        protected override void OnExit(ExitEventArgs e)
        {
            base.OnExit(e);
        }
    }
}
