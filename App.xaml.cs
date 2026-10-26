using System;
using System.Threading;
using System.Windows;
using MultiSSH.Services;

namespace MultiSSH;

public partial class App : Application
{
    // A single Multi-SSH instance must own sessions.json. Two instances each hold the
    // whole session list in memory and Save() overwrites the file wholesale, so the last
    // one to save silently drops the other's entries. The named mutex enforces one
    // instance; a later launch signals the running one to surface, then exits.
    private const string MutexName = "MultiSSH.SingleInstance.9A5E4C2F-7B3D-4E6A-9C1F-2D8B6A0E5F71";
    private const string ShowEventName = MutexName + ".show";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        // The elevated helper for "Run as Administrator" sessions: no window, no
        // settings, and no single-instance guard (the main instance is its parent).
        if (e.Args.Length > 0 && e.Args[0] == ElevatedFrames.HelperArg)
        {
            int code = ElevatedShellHost.Run(e.Args);
            Shutdown(code);
            return;
        }

        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        if (!isFirstInstance)
        {
            // Already running: ask the primary to come forward, then quit without opening a
            // window. Starting a second copy is exactly what loses saved sessions.
            try { _showEvent.Set(); } catch { /* best effort */ }
            Shutdown();
            return;
        }

        StartShowListener();

        // Populate the theme brushes before any window loads so DynamicResource lookups resolve.
        ThemeManager.Apply(AppSettings.Current.Theme);
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("Unexpected error:\n\n" + args.Exception.Message,
                "Multi-SSH", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }

    /// <summary>Wait for a later launch to signal us, then bring the main window forward.</summary>
    private void StartShowListener()
    {
        var thread = new Thread(() =>
        {
            while (_showEvent!.WaitOne())
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (MainWindow is not { } w) return;
                    if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                    w.Show();
                    w.Activate();
                    w.Topmost = true;    // flash to the foreground, then drop topmost
                    w.Topmost = false;
                });
            }
        })
        {
            IsBackground = true,
            Name = "single-instance-listener",
        };
        thread.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceMutex?.Dispose();   // the OS releases the named mutex on process exit
        _showEvent?.Dispose();
        base.OnExit(e);
    }
}
