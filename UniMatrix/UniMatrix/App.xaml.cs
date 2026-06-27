using System;
using System.IO;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace UniMatrix
{
    sealed partial class App : Application
    {
        public App()
        {
            LogStartup("App constructor");
            this.InitializeComponent();
            this.Suspending += OnSuspending;
            this.Resuming += OnResuming;
            this.UnhandledException += OnUnhandledException;

            // Catch background thread exceptions that bypass UnhandledException
            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                LogStartup($"UNOBSERVED TASK EXCEPTION: {args.Exception}");
                args.SetObserved();
            };

            try
            {
                SQLitePCL.Batteries_V2.Init();
                LogStartup("SQLite initialized");
            }
            catch (Exception ex)
            {
                LogStartup($"SQLite init error: {ex.Message}");
            }

            LogStartup("App constructor done");
        }

        private static void LogStartup(string message)
        {
            try
            {
                string path = Path.Combine(
                    Windows.Storage.ApplicationData.Current.LocalFolder.Path,
                    "startup.log");
                File.AppendAllText(path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}\r\n");
            }
            catch { }
        }

        /// <summary>Appends a diagnostic line to startup.log (shared app log).</summary>
        internal static void Log(string message)
        {
            LogStartup(message);
            try { LogLine?.Invoke(DateTime.Now.ToString("HH:mm:ss") + " " + message); }
            catch { }
        }

        /// <summary>Erases the on-disk startup.log so saving afterwards won't include old history.</summary>
        internal static void ClearLog()
        {
            try
            {
                string path = Path.Combine(
                    Windows.Storage.ApplicationData.Current.LocalFolder.Path,
                    "startup.log");
                if (File.Exists(path)) File.WriteAllText(path, string.Empty);
            }
            catch { }
        }

        /// <summary>
        /// Raised for every <see cref="Log"/> call so an on-screen debug overlay can
        /// display log lines live. Handlers must marshal to the UI thread themselves.
        /// </summary>
        internal static event Action<string> LogLine;

        private async void OnUnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            LogStartup($"UNHANDLED EXCEPTION: {e.Exception}");
            try
            {
                var dialog = new MessageDialog(
                    e.Exception?.ToString() ?? "Unknown error",
                    "UniMatrix Error");
                await dialog.ShowAsync();
            }
            catch { }
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            LogStartup("OnLaunched");
            Frame rootFrame = Window.Current.Content as Frame;

            if (rootFrame == null)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                Window.Current.Content = rootFrame;
            }

            if (e.PrelaunchActivated == false)
            {
                if (rootFrame.Content == null)
                {
                    LogStartup("Navigating to MainPage");
                    rootFrame.Navigate(typeof(MainPage), e.Arguments);
                    LogStartup("MainPage navigated");
                }
                else
                {
                    // Already running: if this launch came from a tapped toast, open that room now.
                    if (!string.IsNullOrEmpty(e.Arguments))
                        MainPage.Current?.HandleToastLaunch(e.Arguments);
                }
                Window.Current.Activate();
                LogStartup("Window activated");
            }
        }

        protected override async void OnBackgroundActivated(BackgroundActivatedEventArgs args)
        {
            base.OnBackgroundActivated(args);

            var taskInstance = args.TaskInstance;
            if (taskInstance == null || taskInstance.Task == null ||
                taskInstance.Task.Name != Services.NotificationTask.TaskName)
            {
                return;
            }

            var deferral = taskInstance.GetDeferral();
            try
            {
                await Services.NotificationTask.RunAsync();
            }
            catch
            {
                // Never crash the background host on a fetch error.
            }
            finally
            {
                deferral.Complete();
            }
        }

        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            try
            {
                // Flush the database WAL so cached chat data survives termination.
                MainPage.Current?.OnAppSuspending();
            }
            catch { }
            deferral.Complete();
        }

        private void OnResuming(object sender, object e)
        {
            // App came back from suspend without termination: resume sync + backfill.
            try { MainPage.Current?.OnAppResuming(); }
            catch { }
        }
    }
}
