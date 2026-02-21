using Microsoft.UI.Xaml;
using System;
using System.IO;

namespace WindowsNotesApp
{
    public static class Program
    {
        [global::System.STAThreadAttribute]
        static void Main(string[] args)
        {
            string logFile = Path.Combine(AppContext.BaseDirectory, "startup.log");
            try
            {
                File.AppendAllText(logFile, $"{DateTime.Now}: Main() entered\n");
                global::WinRT.ComWrappersSupport.InitializeComWrappers();
                File.AppendAllText(logFile, $"{DateTime.Now}: ComWrappers initialized\n");
                global::Microsoft.UI.Xaml.Application.Start((p) =>
                {
                    File.AppendAllText(logFile, $"{DateTime.Now}: Application.Start callback\n");
                    var context = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                        global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                    global::System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                    File.AppendAllText(logFile, $"{DateTime.Now}: Creating App instance\n");
                    new App();
                    File.AppendAllText(logFile, $"{DateTime.Now}: App instance created\n");
                });
                File.AppendAllText(logFile, $"{DateTime.Now}: Application.Start returned\n");
            }
            catch (Exception ex)
            {
                File.AppendAllText(logFile, $"{DateTime.Now}: FATAL in Main: {ex}\n");
            }
        }
    }

    public partial class App : Application
    {
        public Window m_window;

        public App()
        {
            string logFile = Path.Combine(AppContext.BaseDirectory, "startup.log");
            try
            {
                File.AppendAllText(logFile, $"{DateTime.Now}: App() constructor start\n");
                this.InitializeComponent();
                File.AppendAllText(logFile, $"{DateTime.Now}: InitializeComponent done\n");
            }
            catch (Exception ex)
            {
                File.AppendAllText(logFile, $"{DateTime.Now}: App.InitializeComponent FAILED: {ex}\n");
            }
            this.UnhandledException += App_UnhandledException;
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            string logFile = Path.Combine(AppContext.BaseDirectory, "startup.log");
            File.AppendAllText(logFile, $"{DateTime.Now}: UNHANDLED: {e.Exception}\n{e.Exception.StackTrace}\n");
            e.Handled = true;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            string logFile = Path.Combine(AppContext.BaseDirectory, "startup.log");
            try
            {
                File.AppendAllText(logFile, $"{DateTime.Now}: OnLaunched start\n");
                m_window = new MainWindow();
                File.AppendAllText(logFile, $"{DateTime.Now}: MainWindow created\n");
                m_window.Activate();
                File.AppendAllText(logFile, $"{DateTime.Now}: Window activated\n");
            }
            catch (Exception ex)
            {
                File.AppendAllText(logFile, $"{DateTime.Now}: OnLaunched FAILED: {ex}\n");
            }
        }
    }
}
