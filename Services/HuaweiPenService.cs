using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WindowsNotesApp.Services
{
    /// <summary>
    /// Lightweight service for Huawei M-Pencil eraser toggle on MateBook devices.
    ///
    /// Signal paths handled:
    ///   1. Win+F19 / Win+F20 global hotkeys – emitted by Huawei PC Manager
    ///      (AcAppDaemon.exe) or community tools like MateBook-E-Pen when the
    ///      user double-clicks the M-Pencil.
    ///      https://github.com/eiyooooo/MateBook-E-Pen
    ///
    ///   2. Standard Windows Ink pen inversion (StylusDevice.Inverted / IsEraser)
    ///      is handled directly by PdfPageControl – nothing extra needed here.
    ///
    /// NOTE: We intentionally do NOT register for Raw HID Input (WM_INPUT +
    /// RIDEV_INPUTSINK).  Doing so floods the WPF message queue with thousands
    /// of digitizer messages per second from every touch/pen device on the
    /// system, causing multi-second UI lag.  The hotkey path is the reliable,
    /// zero-overhead signal for the M-Pencil double-click.
    /// </summary>
    public class HuaweiPenService : IDisposable
    {
        /// <summary>
        /// Raised when the user double-clicks the pen to toggle eraser mode.
        /// </summary>
        public event EventHandler ToolToggleRequested;

        private IntPtr _hwnd;
        private HwndSource _hwndSource;
        private bool _isInitialized;
        private bool _disposed;

        // ── Win32 constants ──────────────────────────────────────────────
        private const int WM_HOTKEY = 0x0312;

        private const int HOTKEY_ID_WIN_F19 = 9001;
        private const int HOTKEY_ID_WIN_F20 = 9002;

        private const uint VK_F19 = 0x82;
        private const uint VK_F20 = 0x83;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;

        // ── P/Invoke ─────────────────────────────────────────────────────
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // ── Public API ───────────────────────────────────────────────────

        public void Initialize(Window window)
        {
            if (_isInitialized)
            {
                Log("Initialize called but already initialized, skipping");
                return;
            }

            if (window == null)
            {
                Log("ERROR: Initialize called with null window!");
                return;
            }

            Log($"Initialize called, window type={window.GetType().Name}");

            var helper = new WindowInteropHelper(window);
            _hwnd = helper.Handle;

            if (_hwnd == IntPtr.Zero)
            {
                Log("HWND is zero – deferring to SourceInitialized");
                window.SourceInitialized += (s, e) =>
                {
                    _hwnd = new WindowInteropHelper(window).Handle;
                    Log($"SourceInitialized fired, HWND=0x{_hwnd:X}");
                    DoInitialize();
                };
            }
            else
            {
                Log($"HWND already available: 0x{_hwnd:X}");
                DoInitialize();
            }

            _isInitialized = true;
        }

        public void SimulateToggle()
        {
            OnToolToggleRequested();
        }

        // ── Initialization ───────────────────────────────────────────────

        private void DoInitialize()
        {
            // Register Win+F19 and Win+F20 hotkeys
            // MOD_NOREPEAT prevents repeated WM_HOTKEY while held
            bool f19 = RegisterHotKey(_hwnd, HOTKEY_ID_WIN_F19, MOD_WIN | MOD_NOREPEAT, VK_F19);
            int f19err = f19 ? 0 : Marshal.GetLastWin32Error();
            bool f20 = RegisterHotKey(_hwnd, HOTKEY_ID_WIN_F20, MOD_WIN | MOD_NOREPEAT, VK_F20);
            int f20err = f20 ? 0 : Marshal.GetLastWin32Error();

            Log($"RegisterHotKey Win+F19: {(f19 ? "OK" : $"FAILED err={f19err}")}");
            Log($"RegisterHotKey Win+F20: {(f20 ? "OK" : $"FAILED err={f20err}")}");

            // Use HwndSource.AddHook – the proper WPF way to intercept window messages.
            _hwndSource = HwndSource.FromHwnd(_hwnd);
            if (_hwndSource != null)
            {
                _hwndSource.AddHook(WndProc);
                Log("HwndSource.AddHook installed successfully");
            }
            else
            {
                Log("ERROR: HwndSource.FromHwnd returned null! Message hook NOT installed.");
            }

            Log($"Initialization complete – HWND=0x{_hwnd:X}");
        }

        // ── Message handler (only fires for WM_HOTKEY, zero overhead) ────

        private IntPtr WndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int hotkeyId = wParam.ToInt32();
                Log($"WM_HOTKEY received, id={hotkeyId}");
                if (hotkeyId == HOTKEY_ID_WIN_F19 || hotkeyId == HOTKEY_ID_WIN_F20)
                {
                    string name = hotkeyId == HOTKEY_ID_WIN_F19 ? "Win+F19" : "Win+F20";
                    Log($">>> MATCHED {name} – firing ToolToggleRequested");
                    OnToolToggleRequested();
                    handled = true;
                }
                else
                {
                    Log($"Hotkey id={hotkeyId} does not match ours (9001/9002), ignoring");
                }
            }

            return IntPtr.Zero;
        }

        private void OnToolToggleRequested()
        {
            int subscribers = ToolToggleRequested?.GetInvocationList().Length ?? 0;
            Log($"OnToolToggleRequested – {subscribers} subscriber(s)");
            ToolToggleRequested?.Invoke(this, EventArgs.Empty);
        }

        private static void Log(string message)
        {
            string line = $"[HuaweiPen] {DateTime.Now:HH:mm:ss.fff} {message}";
            Console.WriteLine(line);
            System.Diagnostics.Debug.WriteLine(line);
        }

        // ── Cleanup ──────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _hwndSource?.RemoveHook(WndProc);

            if (_hwnd != IntPtr.Zero)
            {
                try { UnregisterHotKey(_hwnd, HOTKEY_ID_WIN_F19); } catch { }
                try { UnregisterHotKey(_hwnd, HOTKEY_ID_WIN_F20); } catch { }
            }
        }
    }
}
