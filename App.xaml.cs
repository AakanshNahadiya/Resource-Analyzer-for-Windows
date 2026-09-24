using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;

using AccessibleTaskManager.Helpers;

namespace AccessibleTaskManager
{
    public partial class App : System.Windows.Application
    {
        private const string MutexName = "ResourceAnalyzer_SingleInstance_Mutex";
        private const string SignalEventName = "ResourceAnalyzer_ShowSignalEvent";

        private static Mutex? _mutex;
        private static EventWaitHandle? _signalEvent;
        private static bool _ownsMutex = false;
        private static bool _keepListening = true;

        protected override void OnStartup(StartupEventArgs e)
        {
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
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to signal running instance: {ex.Message}");
                    }

                    Shutdown();
                    return;
                }
                else
                {
                    _ownsMutex = true;
                }
            }

            try
            {
                _signalEvent = new EventWaitHandle(false, EventResetMode.AutoReset, SignalEventName);
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

            // Start background thread listening for show signal from new instances
            if (_signalEvent != null)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    while (_keepListening)
                    {
                        try
                        {
                            if (_signalEvent.WaitOne(1000))
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    mainWindow.RestoreFromTray();
                                });
                            }
                        }
                        catch
                        {
                            break;
                        }
                    }
                });
            }

            bool startMinimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
            mainWindow.InitializeApp(startMinimized);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _keepListening = false;
            _signalEvent?.Dispose();
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
        }
    }
}
