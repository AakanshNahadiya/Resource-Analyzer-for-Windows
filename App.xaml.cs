using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;

using ResourceAnalyzer.Helpers;

namespace ResourceAnalyzer
{
    public partial class App : System.Windows.Application
    {
        private const string MutexName = "ResourceAnalyzer_SingleInstance_Mutex";
        private const string SignalEventName = "ResourceAnalyzer_ShowSignalEvent";
        private const string ExitSignalEventName = "ResourceAnalyzer_ExitSignalEvent";

        private static Mutex? _mutex;
        private static EventWaitHandle? _signalEvent;
        private static EventWaitHandle? _exitSignalEvent;
        private static bool _ownsMutex = false;
        private static RegisteredWaitHandle? _registeredWait;
        private static RegisteredWaitHandle? _registeredExitWait;

        protected override void OnStartup(StartupEventArgs e)
        {
            // Initialize process-level DLL security hardening (Microsoft SDL / CWE-427)
            NativeMethods.InitializeProcessSecurity();

            DispatcherUnhandledException += (s, args) =>
            {
                try
                {
                    string logPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ResourceAnalyzer_crash.log");
                    System.IO.File.AppendAllText(logPath, $"[{DateTime.Now}] Dispatcher Exception: {args.Exception}\n");
                }
                catch { }
            };

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                try
                {
                    string logPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ResourceAnalyzer_crash.log");
                    System.IO.File.AppendAllText(logPath, $"[{DateTime.Now}] AppDomain Exception: {args.ExceptionObject}\n");
                }
                catch { }
            };

            bool isNewInstance = false;
            try
            {
                _mutex = new Mutex(true, MutexName, out isNewInstance);
                _ownsMutex = isNewInstance;
            }
            catch (AbandonedMutexException)
            {
                _ownsMutex = true;
                isNewInstance = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Mutex creation error: {ex.Message}");
                _ownsMutex = false;
                isNewInstance = true;
            }

            if (!isNewInstance)
            {
                int currentPid = Process.GetCurrentProcess().Id;
                bool reallyRunning = Process.GetProcessesByName("ResourceAnalyzer").Any(p => p.Id != currentPid);
                if (reallyRunning)
                {
                    try
                    {
                        NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
                        if (EventWaitHandle.TryOpenExisting(SignalEventName, out var signalHandle))
                        {
                            signalHandle.Set();
                            signalHandle.Dispose();
                            Thread.Sleep(100);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to signal running instance: {ex.Message}");
                    }

                    Shutdown();
                    Environment.Exit(0);
                    return;
                }
                else
                {
                    _ownsMutex = true;
                }
            }

            try
            {
                _signalEvent = NativeMethods.CreateCrossIntegrityEvent(SignalEventName, false, false);
                _exitSignalEvent = NativeMethods.CreateCrossIntegrityEvent(ExitSignalEventName, false, false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to create signal event handle: {ex.Message}");
            }

            base.OnStartup(e);

            // Force software rendering to prevent loading massive DirectX and GPU driver DLLs (igc64.dll, etc.)
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var mainWindow = new MainWindow();
            MainWindow = mainWindow;

            // Register wait handle listening for show signal from new instances using Windows thread pool
            if (_signalEvent != null)
            {
                _registeredWait = ThreadPool.RegisterWaitForSingleObject(
                    _signalEvent,
                    (state, timedOut) =>
                    {
                        if (!timedOut)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                mainWindow.RestoreFromTray();
                            });
                        }
                    },
                    null,
                    -1,
                    false);
            }

            // Register wait handle listening for clean exit signal (e.g. from setup or uninstaller)
            if (_exitSignalEvent != null)
            {
                _registeredExitWait = ThreadPool.RegisterWaitForSingleObject(
                    _exitSignalEvent,
                    (state, timedOut) =>
                    {
                        if (!timedOut)
                        {
                            try
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    mainWindow.ExitApplication();
                                });
                            }
                            catch { }

                            // Immediate process termination guarantee to release file locks on clrjit.dll etc.
                            _ = System.Threading.Tasks.Task.Delay(300).ContinueWith(_ => Environment.Exit(0));
                        }
                    },
                    null,
                    -1,
                    false);
            }

            bool startMinimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
            mainWindow.InitializeApp(startMinimized);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _registeredWait?.Unregister(null);
            _registeredExitWait?.Unregister(null);
            _signalEvent?.Dispose();
            _exitSignalEvent?.Dispose();
            if (_ownsMutex)
            {
                try
                {
                    _mutex?.ReleaseMutex();
                }
                catch { }
            }
            _mutex?.Dispose();
            base.OnExit(e);
            Environment.Exit(0);
        }
    }
}
