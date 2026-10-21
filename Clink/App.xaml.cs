using System.IO;
using System.Windows;
using Windows.Win32;
using Clink.Services;

namespace Clink;

public partial class App : System.Windows.Application
{
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string[] args = e.Args;
        if (args.Length > 0 && args[0].Equals("--apply-update", StringComparison.OrdinalIgnoreCase))
        {
            // Clink.exe --apply-update <targetExePath> <pid>: we are the freshly downloaded exe replacing an installed one.
            // No log init of the normal path and no single-instance mutex here; the old process is still shutting down.
            int code;
            try { code = Updater.RunApplyUpdate(args); }
            catch (Exception ex) { Log.Error("[update] apply crashed", ex); code = 1; }
            Shutdown(code);
            return;
        }

        Log.Init(Settings.Dir);
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error("Unhandled", (a.ExceptionObject as Exception) ?? new Exception(a.ExceptionObject?.ToString()));
        DispatcherUnhandledException += (_, a) => { Log.Error("UI exception", a.Exception); a.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error("Task exception", a.Exception); a.SetObserved(); };

        if (args.Length > 0 && args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
        {
            AttachParentConsole();
            int code = 1;
            var t = new Thread(() => { code = SelfTest.Run(args); }) { IsBackground = false };
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            t.Join();
            Shutdown(code);
            return;
        }
        if (args.Length > 1 && args[0].Equals("--probe", StringComparison.OrdinalIgnoreCase))
        {
            AttachParentConsole();
            int code = 0;
            var t = new Thread(() =>
            {
                try
                {
                    string report = Engine.MediaProbe.Report(args[1]);
                    Console.WriteLine(report);
                    Log.Info("[probe]\n" + report);
                    if (args.Length > 2)
                    {
                        long at = args.Length > 3 && double.TryParse(args[3], out double sec) ? (long)(sec * 10_000_000) : 0;
                        var frame = Engine.MediaProbe.DecodeFrame(args[1], at);
                        if (frame != null) { Engine.MediaProbe.SavePng(args[2], frame.Value.w, frame.Value.h, frame.Value.bgra); Log.Info($"[probe] frame saved to {args[2]}"); Console.WriteLine("frame saved to " + args[2]); }
                        else { Log.Warn("[probe] no frame decoded"); code = 4; }
                    }
                }
                catch (Exception ex) { Log.Error("[probe] failed", ex); Console.WriteLine(ex); code = 5; }
            });
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            t.Join();
            Shutdown(code);
            return;
        }
        if (args.Length > 1 && args[0].Equals("--trim", StringComparison.OrdinalIgnoreCase))
        {
            // Clink.exe --trim <in> <out> <startSec> <endSec>
            AttachParentConsole();
            int code = 0;
            var t = new Thread(() =>
            {
                try { Engine.Trimmer.Trim(args[1], args[2], double.Parse(args[3]), double.Parse(args[4]), null); Console.WriteLine("ok"); }
                catch (Exception ex) { Log.Error("[trim] failed", ex); Console.WriteLine(ex); code = 5; }
            });
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            t.Join();
            Shutdown(code);
            return;
        }

        // Normal run: one instance; "--clip" / "--show" talk to it.
        if (!AppController.AcquireSingleInstance(args)) { Shutdown(0); return; }

        var settings = Settings.Load();
        var updater = new Updater(settings);
        updater.LogSource();

        // A newer exe was downloaded on an earlier run: hand over to it and let it replace us ("--no-update" skips this).
        // Only an exe that knows its update repository does this: the updates folder is shared by every Clink exe on the
        // machine, and a local/debug build must never be overwritten by a release download it did not make.
        bool noUpdate = args.Any(a => a.Equals("--no-update", StringComparison.OrdinalIgnoreCase));
        if (updater.Repo != null && settings.AutoUpdate && !noUpdate && updater.TryGetPendingUpdate(out string pendingExe, out Version pendingVersion))
        {
            Log.Info($"Installing downloaded update v{pendingVersion} from {pendingExe}");
            try
            {
                // The restarted exe gets this command line back, so an update at login (--minimized from the Run key) stays in the tray.
                Updater.LaunchApply(pendingExe, Environment.ProcessPath!, args.Where(a => !a.Equals("--no-update", StringComparison.OrdinalIgnoreCase)));
                AppController.ReleaseSingleInstance();
                Shutdown(0);
                return;
            }
            catch (Exception ex)
            {
                Log.Error("Could not start the update, continuing with this version", ex);
                updater.ReportInstallFailure(ex, pendingVersion, pendingExe);
            }
        }

        _controller = new AppController(settings, updater);
        var main = new MainWindow();
        MainWindow = main;
        _controller.Start();
        bool show = args.Any(a => a.Equals("--show", StringComparison.OrdinalIgnoreCase));
        bool minimized = !show && (settings.StartMinimized || args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)));
        if (!minimized) main.Show();
        Log.Info($"Clink v{updater.VersionDisplay} started");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        base.OnExit(e);
    }

    private static void AttachParentConsole()
    {
        try
        {
            if (PInvoke.AttachConsole(PInvoke.ATTACH_PARENT_PROCESS))
            {
                var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                Console.SetOut(stdout);
                Console.SetError(stdout);
                Console.WriteLine();
            }
        }
        catch { }
    }
}
