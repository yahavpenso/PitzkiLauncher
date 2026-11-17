using System.Windows;

namespace PitzkiLauncher;

public partial class App : Application
{
    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "PitzkiLauncher_SingleInstance", out bool isNew);
        if (!isNew)
        {
            // already running: ask the running copy to open the search box
            try { EventWaitHandle.OpenExisting("PitzkiLauncher_Show").Set(); } catch { }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "PitzkiLauncher_Show");
        base.OnStartup(e);

        var w = new MainWindow(e.Args.Contains("--tray"));
        var ev = _showEvent;
        var t = new Thread(() =>
        {
            while (true)
            {
                ev.WaitOne();
                w.Dispatcher.BeginInvoke(() => w.ShowLauncher(false));
            }
        }) { IsBackground = true };
        t.Start();
    }
}
