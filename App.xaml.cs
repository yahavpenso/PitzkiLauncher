using System.Windows;

namespace PitzkiLauncher;

public partial class App : Application
{
    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        string suffix = Demo.On ? "_Demo" : "";      // the demo runs next to the real copy
        _mutex = new Mutex(true, "PitzkiLauncher_SingleInstance" + suffix, out bool isNew);
        if (!isNew)
        {
            // already running: ask the running copy to open the search box
            try { EventWaitHandle.OpenExisting("PitzkiLauncher_Show" + suffix).Set(); } catch { }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "PitzkiLauncher_Show" + suffix);
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
