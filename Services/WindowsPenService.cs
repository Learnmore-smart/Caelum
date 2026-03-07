using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Caelum.Services
{
    /// <summary>
    /// Unified service for all Windows-compatible pens (Surface Pen, Wacom,
    /// Huawei M-Pencil, HP Tilt Pen, Dell Active Pen, Lenovo Precision Pen, etc.).
    ///
    /// Capabilities detected per-device:
    ///   • Pressure sensitivity (StylusPoint.PressureFactor)
    ///   • X/Y tilt (Surface Pen, Wacom Bamboo Ink, etc.)
    ///   • Barrel / side button
    ///   • Eraser tail (Stylus.Inverted)
    ///   • Huawei M-Pencil double-tap toggle (Win+F19/F20 hotkey)
    ///
    /// Design: one instance per MainWindow lifetime.  EditorPage subscribes to
    /// events; PdfPageControl reads <see cref="PenCapabilities"/> to adapt its
    /// ink rendering (e.g. pressure-to-width curve).
    /// </summary>
    public sealed class WindowsPenService : IDisposable
    {
        // ── Public events ────────────────────────────────────────────────

        /// <summary>
        /// Fired when the user double-clicks (Huawei) or otherwise requests
        /// an eraser toggle from the pen itself.
        /// </summary>
        public event EventHandler ToolToggleRequested;

        /// <summary>
        /// Fired when a new stylus device is detected for the first time,
        /// allowing the UI to display a toast or update settings.
        /// </summary>
        public event EventHandler<PenDeviceInfo> PenDeviceDetected;

        // ── Public state ─────────────────────────────────────────────────

        /// <summary>
        /// Returns the aggregate capabilities of all detected pen devices.
        /// </summary>
        public PenCapabilities Capabilities { get; private set; } = new PenCapabilities();

        /// <summary>
        /// All detected pen/stylus devices, keyed by StylusDevice.Id.
        /// </summary>
        public IReadOnlyDictionary<int, PenDeviceInfo> DetectedDevices => _detectedDevices;

        /// <summary>
        /// Whether pressure-to-width mapping is enabled (user preference).
        /// Default: true – will be applied only if the hardware supports it.
        /// </summary>
        public bool PressureEnabled { get; set; } = true;

        /// <summary>
        /// Whether tilt-to-angle mapping is enabled (user preference).
        /// Default: true – will be applied only if the hardware supports it.
        /// </summary>
        public bool TiltEnabled { get; set; } = true;

        // ── Internal state ───────────────────────────────────────────────

        private readonly Dictionary<int, PenDeviceInfo> _detectedDevices = new Dictionary<int, PenDeviceInfo>();
        private IntPtr _hwnd;
        private HwndSource _hwndSource;
        private bool _isInitialized;
        private bool _disposed;

        // Huawei M-Pencil hotkey support
        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_ID_WIN_F19 = 9001;
        private const int HOTKEY_ID_WIN_F20 = 9002;
        private const uint VK_F19 = 0x82;
        private const uint VK_F20 = 0x83;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // ── Initialisation ───────────────────────────────────────────────

        /// <summary>
        /// Attach to the given <paramref name="window"/> so that global
        /// hotkeys (Huawei) are registered and stylus devices can be probed.
        /// Safe to call multiple times – subsequent calls are no-ops.
        /// </summary>
        public void Initialize(Window window)
        {
            if (_isInitialized) return;
            if (window == null) { Log("Initialize called with null window"); return; }

            var helper = new WindowInteropHelper(window);
            _hwnd = helper.Handle;

            if (_hwnd == IntPtr.Zero)
            {
                window.SourceInitialized += (s, e) =>
                {
                    _hwnd = new WindowInteropHelper(window).Handle;
                    AttachHooks();
                };
            }
            else
            {
                AttachHooks();
            }

            _isInitialized = true;
            Log("Initialized");
        }

        private void AttachHooks()
        {
            // Register Huawei M-Pencil hotkeys (harmless on non-Huawei devices)
            RegisterHotKey(_hwnd, HOTKEY_ID_WIN_F19, MOD_WIN | MOD_NOREPEAT, VK_F19);
            RegisterHotKey(_hwnd, HOTKEY_ID_WIN_F20, MOD_WIN | MOD_NOREPEAT, VK_F20);

            _hwndSource = HwndSource.FromHwnd(_hwnd);
            _hwndSource?.AddHook(WndProc);
            Log($"Hooks attached – HWND=0x{_hwnd:X}");
        }

        // ── Stylus device probing ────────────────────────────────────────

        /// <summary>
        /// Call from StylusDown / StylusInAirMove to register the device and
        /// its capabilities.  Very cheap after first call per device.
        /// </summary>
        public PenDeviceInfo ProbeDevice(StylusDevice device)
        {
            if (device == null) return null;

            int id = device.Id;
            if (_detectedDevices.TryGetValue(id, out var existing))
                return existing;

            var info = BuildDeviceInfo(device);
            _detectedDevices[id] = info;
            MergeCapabilities();

            Log($"New device: {info}");
            PenDeviceDetected?.Invoke(this, info);
            return info;
        }

        private static PenDeviceInfo BuildDeviceInfo(StylusDevice device)
        {
            var info = new PenDeviceInfo
            {
                DeviceId = device.Id,
                DeviceName = device.Name ?? "Unknown Stylus",
                IsInverted = device.Inverted,
            };

            // Probe capabilities via TabletDevice.SupportedStylusPointProperties.
            // This is the WPF way to discover what the digitiser reports.
            try
            {
                var tablet = device.TabletDevice;
                if (tablet != null)
                {
                    var supportedProps = tablet.SupportedStylusPointProperties;
                    if (supportedProps != null)
                    {
                        foreach (var prop in supportedProps)
                        {
                            if (prop.Id == StylusPointProperties.NormalPressure.Id)
                                info.SupportsPressure = true;
                            else if (prop.Id == StylusPointProperties.XTiltOrientation.Id)
                                info.SupportsXTilt = true;
                            else if (prop.Id == StylusPointProperties.YTiltOrientation.Id)
                                info.SupportsYTilt = true;
                            else if (prop.Id == StylusPointProperties.BarrelButton.Id)
                                info.SupportsBarrelButton = true;
                            else if (prop.Id == StylusPointProperties.SecondaryTipButton.Id)
                                info.SupportsSecondaryTip = true;
                            else if (prop.Id == StylusPointProperties.TwistOrientation.Id)
                                info.SupportsTwist = true;
                        }

                        // Pressure and tilt ranges: WPF normalises pressure to
                        // 0.0–1.0 via StylusPoint.PressureFactor so raw ranges
                        // are informational only.  Keep sensible defaults.
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error probing device {device.Name}: {ex.Message}");
            }

            // Heuristic classification
            info.PenBrand = ClassifyBrand(info.DeviceName);

            return info;
        }

        private static PenBrand ClassifyBrand(string name)
        {
            if (string.IsNullOrEmpty(name)) return PenBrand.Generic;

            string lower = name.ToLowerInvariant();

            if (lower.Contains("surface")) return PenBrand.Surface;
            if (lower.Contains("wacom")) return PenBrand.Wacom;
            if (lower.Contains("huawei") || lower.Contains("m-pencil") || lower.Contains("matebook"))
                return PenBrand.Huawei;
            if (lower.Contains("dell")) return PenBrand.Dell;
            if (lower.Contains("hp") || lower.Contains("hewlett")) return PenBrand.HP;
            if (lower.Contains("lenovo") || lower.Contains("thinkpad")) return PenBrand.Lenovo;
            if (lower.Contains("samsung") || lower.Contains("s pen")) return PenBrand.Samsung;
            if (lower.Contains("asus")) return PenBrand.Asus;
            if (lower.Contains("acer")) return PenBrand.Acer;
            if (lower.Contains("n-trig")) return PenBrand.NTrig;
            if (lower.Contains("synaptics")) return PenBrand.Synaptics;
            if (lower.Contains("elan")) return PenBrand.Elan;
            if (lower.Contains("xp-pen") || lower.Contains("xppen")) return PenBrand.XPPen;
            if (lower.Contains("huion")) return PenBrand.Huion;
            if (lower.Contains("gaomon")) return PenBrand.Gaomon;

            return PenBrand.Generic;
        }

        private void MergeCapabilities()
        {
            var caps = new PenCapabilities();
            foreach (var d in _detectedDevices.Values)
            {
                caps.HasPressure |= d.SupportsPressure;
                caps.HasTilt |= d.SupportsXTilt || d.SupportsYTilt;
                caps.HasBarrelButton |= d.SupportsBarrelButton;
                caps.HasEraserTail |= d.IsInverted; // at least one device can invert
                caps.HasTwist |= d.SupportsTwist;
            }
            Capabilities = caps;
        }

        // ── Pressure helpers (used by PdfPageControl) ────────────────────

        /// <summary>
        /// Map a raw stylus pressure value (0.0 – 1.0 from
        /// <see cref="StylusPoint.PressureFactor"/>) to a pen width multiplier.
        ///
        /// Returns a value between <paramref name="minMultiplier"/> and
        /// <paramref name="maxMultiplier"/>.  The curve is slightly concave
        /// to feel more natural on most digitisers.
        /// </summary>
        public static double PressureToWidthMultiplier(
            double pressureFactor,
            double minMultiplier = 0.3,
            double maxMultiplier = 1.8)
        {
            // Clamp input
            double p = Math.Max(0.0, Math.Min(1.0, pressureFactor));

            // Apply a mild power curve (γ = 0.7) so light strokes stay visible
            // while heavy pressure produces a satisfying thick line.
            double curved = Math.Pow(p, 0.7);

            return minMultiplier + curved * (maxMultiplier - minMultiplier);
        }

        /// <summary>
        /// Returns an opacity multiplier (0.0–1.0) for highlighter mode,
        /// allowing lighter touches to produce lighter highlights.
        /// </summary>
        public static double PressureToHighlighterOpacity(
            double pressureFactor,
            double minOpacity = 0.3,
            double maxOpacity = 0.8)
        {
            double p = Math.Max(0.0, Math.Min(1.0, pressureFactor));
            return minOpacity + p * (maxOpacity - minOpacity);
        }

        /// <summary>
        /// Returns the tilt angle (degrees, 0 = perpendicular) from raw
        /// X-tilt and Y-tilt stylus properties.
        /// </summary>
        public static double ComputeTiltAngle(double xTilt, double yTilt)
        {
            return Math.Sqrt(xTilt * xTilt + yTilt * yTilt);
        }

        /// <summary>
        /// Returns a width multiplier based on tilt, simulating a calligraphy
        /// effect: the more you tilt, the wider the stroke becomes.
        /// </summary>
        public static double TiltToWidthMultiplier(
            double tiltAngle,
            double maxTilt = 90.0,
            double minMultiplier = 1.0,
            double maxMultiplier = 2.5)
        {
            double t = Math.Max(0.0, Math.Min(maxTilt, tiltAngle)) / maxTilt;
            return minMultiplier + t * (maxMultiplier - minMultiplier);
        }

        // ── Window message handler (Huawei hotkeys) ─────────────────────

        private IntPtr WndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int hotkeyId = wParam.ToInt32();
                if (hotkeyId == HOTKEY_ID_WIN_F19 || hotkeyId == HOTKEY_ID_WIN_F20)
                {
                    Log($"Huawei hotkey {(hotkeyId == HOTKEY_ID_WIN_F19 ? "Win+F19" : "Win+F20")} → ToolToggleRequested");
                    ToolToggleRequested?.Invoke(this, EventArgs.Empty);
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        // ── Simulate (for testing / keyboard shortcut) ───────────────────

        public void SimulateToggle()
        {
            ToolToggleRequested?.Invoke(this, EventArgs.Empty);
        }

        // ── Logging ──────────────────────────────────────────────────────

        private static void Log(string message)
        {
            string line = $"[WindowsPen] {DateTime.Now:HH:mm:ss.fff} {message}";
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

    // ── Supporting types ─────────────────────────────────────────────────

    /// <summary>
    /// Aggregate capability flags across all connected pen devices.
    /// </summary>
    public class PenCapabilities
    {
        public bool HasPressure { get; set; }
        public bool HasTilt { get; set; }
        public bool HasBarrelButton { get; set; }
        public bool HasEraserTail { get; set; }
        public bool HasTwist { get; set; }

        public override string ToString() =>
            $"Pressure={HasPressure} Tilt={HasTilt} Barrel={HasBarrelButton} Eraser={HasEraserTail} Twist={HasTwist}";
    }

    /// <summary>
    /// Information about a specific pen/stylus device discovered at runtime.
    /// </summary>
    public class PenDeviceInfo
    {
        public int DeviceId { get; set; }
        public string DeviceName { get; set; }
        public PenBrand PenBrand { get; set; }

        // Capabilities
        public bool SupportsPressure { get; set; }
        public bool SupportsXTilt { get; set; }
        public bool SupportsYTilt { get; set; }
        public bool SupportsBarrelButton { get; set; }
        public bool SupportsSecondaryTip { get; set; }
        public bool SupportsTwist { get; set; }
        public bool IsInverted { get; set; }

        // Pressure range (raw digitiser values)
        public int PressureMin { get; set; }
        public int PressureMax { get; set; } = 1024;

        // Tilt range (degrees)
        public int TiltMin { get; set; }
        public int TiltMax { get; set; } = 90;

        public override string ToString() =>
            $"{PenBrand} \"{DeviceName}\" id={DeviceId} " +
            $"pressure={SupportsPressure}({PressureMin}–{PressureMax}) " +
            $"tilt={SupportsXTilt}/{SupportsYTilt} " +
            $"barrel={SupportsBarrelButton} inverted={IsInverted}";
    }

    /// <summary>
    /// Known pen brands for device-specific tuning or UI hints.
    /// </summary>
    public enum PenBrand
    {
        Generic,
        Surface,
        Wacom,
        Huawei,
        Dell,
        HP,
        Lenovo,
        Samsung,
        Asus,
        Acer,
        NTrig,
        Synaptics,
        Elan,
        XPPen,
        Huion,
        Gaomon
    }
}
