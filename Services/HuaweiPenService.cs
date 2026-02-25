using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WindowsNotesApp.Services
{
    public class HuaweiPenService
    {
        public event EventHandler ToolToggleRequested;

        private IntPtr _hwnd;
        private const int WM_INPUT = 0x00FF;
        private const int RID_INPUT = 0x10000003;
        private const int RIM_TYPEHID = 2;

        // Huawei M-Pencil 3 / Standard Digitizer Usage
        private const ushort HID_USAGE_PAGE_DIGITIZER = 0x0D;
        private const ushort HID_USAGE_PEN = 0x02;

        // P/Invoke
        [DllImport("User32.dll")]
        static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

        [DllImport("User32.dll")]
        static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

        [DllImport("comctl32.dll", SetLastError = true)]
        static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, uint uIdSubclass, IntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, uint uIdSubclass);

        [DllImport("comctl32.dll", SetLastError = true)]
        static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, IntPtr dwRefData);

        private SubclassProc _subclassProc;

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTHEADER
        {
            public uint dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Explicit)]
        struct RAWINPUT
        {
            [FieldOffset(0)]
            public RAWINPUTHEADER header;

            [FieldOffset(24)] // 24 bytes for 64-bit header, but we should calculate later safely, or use struct. Let's assume 64-bit.
            public RAWHID hid;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RAWHID
        {
            public uint dwSizeHid;
            public uint dwCount;
            // byte bRawData[1] // Variable length
        }

        public void Initialize(Window window)
        {
            var helper = new WindowInteropHelper(window);
            _hwnd = helper.EnsureHandle();

            // Register for Raw Input (Digitizer)
            var rid = new RAWINPUTDEVICE[1];
            rid[0].usUsagePage = HID_USAGE_PAGE_DIGITIZER;
            rid[0].usUsage = HID_USAGE_PEN;
            rid[0].dwFlags = 0x00000100; // RIDEV_INPUTSINK
            rid[0].hwndTarget = _hwnd;

            if (!RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
            {
                System.Diagnostics.Debug.WriteLine("Failed to register raw input devices.");
            }

            // Subclass window to intercept WM_INPUT
            _subclassProc = new SubclassProc(WndProc);
            SetWindowSubclass(_hwnd, _subclassProc, 101, IntPtr.Zero);
        }

        private IntPtr WndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, IntPtr dwRefData)
        {
            if (uMsg == WM_INPUT)
            {
                ProcessRawInput(lParam);
            }
            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        private DateTime _lastDoubleTapTime = DateTime.MinValue;

        private void ProcessRawInput(IntPtr hRawInput)
        {
            uint dwSize = 0;
            GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref dwSize, (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER)));

            if (dwSize == 0) return;

            IntPtr buffer = Marshal.AllocHGlobal((int)dwSize);
            try
            {
                if (GetRawInputData(hRawInput, RID_INPUT, buffer, ref dwSize, (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER))) == dwSize)
                {
                    // To handle both 32-bit and 64-bit safely:
                    int headerSize = Marshal.SizeOf(typeof(RAWINPUTHEADER));
                    RAWINPUTHEADER header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);

                    if (header.dwType == RIM_TYPEHID)
                    {
                        IntPtr hidPtr = new IntPtr(buffer.ToInt64() + headerSize);
                        RAWHID hid = Marshal.PtrToStructure<RAWHID>(hidPtr);

                        uint dataSize = hid.dwSizeHid * hid.dwCount;
                        if (dataSize > 0 && dataSize < 1024)
                        {
                            IntPtr dataPtr = new IntPtr(hidPtr.ToInt64() + Marshal.SizeOf(typeof(RAWHID)));
                            byte[] rawData = new byte[dataSize];
                            Marshal.Copy(dataPtr, rawData, 0, (int)dataSize);

                            // Huawei M-Pencil double tap heuristic:
                            // Often it's sent as a specific report on the digitizer usage page.
                            // If Huawei uses standard pen barrel button for double tap, it would be bits in the raw data.
                            // However, since Huawei's exact format is closed-source for Windows, we check for rapid successive reports
                            // or specific byte patterns.
                            // For now, if we match common barrel button presses or a specific report size/pattern, we toggle.
                            string hex = BitConverter.ToString(rawData);
                            System.Diagnostics.Debug.WriteLine($"HuaweiPenService HID Data: {hex}");

                            // Very crude heuristic for unknown proprietary double tap:
                            // If it's a specific report that only occurs on double tap, it might have a unique length or Report ID.
                            // For some pens, Report ID 0x02 or 0x07 with specific payload length signifies it.
                            // We will emit the event if we detect a specific Huawei-like M-Pencil double tap signature.
                            // Since we lack the exact signature, we leave this logged so developers can find the payload from debugger.
                            // E.g., if rawData[0] == 0xXYZ ...

                            // Let's implement a fallback software double-tap detection for normal tip taps just in case?
                            // No, user explicitly asked about the pen's hardware double-tap feature.

                            // Let's assume Huawei maps it to a standard barrel button for Windows generic compatibility.
                            // If so, the WinUI InkCanvas might already handle right-click = erase.
                            // Our EditorPage has `OnHuaweiPenDoubleTap` which toggles the tool.

                            // Placeholder heuristic: if we see a report with a typical barrel switch bit flip that isn't tip switch.
                            // Not enough info to implement the precise byte check without a device, but we provide the hook.

                            // Example of triggering it (disabled by default until pattern is known):
                            // if (rawData.Length == X && rawData[0] == Y) { SimulateDoubleTap(); }
                        }
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void SimulateDoubleTap()
        {
             ToolToggleRequested?.Invoke(this, EventArgs.Empty);
        }

        public void Uninitialize()
        {
            if (_hwnd != IntPtr.Zero && _subclassProc != null)
            {
                RemoveWindowSubclass(_hwnd, _subclassProc, 101);
                _subclassProc = null;
                _hwnd = IntPtr.Zero;
            }
        }
    }
}
