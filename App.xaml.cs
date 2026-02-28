using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace WindowsNotesApp
{
    public partial class App : Application
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Ensure pdfium.dll can be found in published / installed scenarios.
            // PdfiumViewer.Native places it under x64/ – we need to add that
            // folder to the DLL search path so LoadLibrary finds it.
            EnsurePdfiumDllPath();

            var mainWindow = new MainWindow();
            mainWindow.Show();
        }

        private static void EnsurePdfiumDllPath()
        {
            // Candidate directories to search for pdfium.dll
            string baseDir = AppContext.BaseDirectory;
            string[] candidates = new[]
            {
                Path.Combine(baseDir, "x64"),
                Path.Combine(baseDir, "x86"),
                baseDir,
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "x64"),
                // For single-file publish, native libs extract next to the exe
                Path.GetDirectoryName(Environment.ProcessPath) ?? baseDir,
                Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? baseDir, "x64"),
            };

            foreach (var dir in candidates)
            {
                string dllPath = Path.Combine(dir, "pdfium.dll");
                if (File.Exists(dllPath))
                {
                    SetDllDirectory(dir);
                    System.Diagnostics.Debug.WriteLine($"[App] SetDllDirectory → {dir}");
                    return;
                }
            }

            System.Diagnostics.Debug.WriteLine("[App] WARNING: pdfium.dll not found in any candidate directory");
        }
    }
}
