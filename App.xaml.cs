using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Caelum.Services;

namespace Caelum
{
    public partial class App : Application
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            LocalizationService.ApplyLanguage(AppSettingsService.Load().Language);
            EnsurePdfiumDllPath();

            var mainWindow = new MainWindow();
            mainWindow.Show();
        }

        private static void EnsurePdfiumDllPath()
        {
            string baseDir = AppContext.BaseDirectory;
            string[] candidates = new[]
            {
                Path.Combine(baseDir, "x64"),
                Path.Combine(baseDir, "x86"),
                baseDir,
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "x64"),
                Path.GetDirectoryName(Environment.ProcessPath) ?? baseDir,
                Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? baseDir, "x64"),
            };

            foreach (var dir in candidates)
            {
                string dllPath = Path.Combine(dir, "pdfium.dll");
                if (File.Exists(dllPath))
                {
                    SetDllDirectory(dir);
                    System.Diagnostics.Debug.WriteLine($"[App] SetDllDirectory -> {dir}");
                    return;
                }
            }

            System.Diagnostics.Debug.WriteLine("[App] WARNING: pdfium.dll not found in any candidate directory");
        }
    }
}
