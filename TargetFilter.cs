using System;
using System.IO;
using System.Text;

namespace MellowClick
{
    internal sealed class TargetFilter
    {
        private volatile bool enabled = true;
        private volatile string expectedName = "Minecraft.Windows.exe";
        private volatile string lastProcessName = "ещё не проверено";
        internal string LastProcessName { get { return lastProcessName; } }
        internal bool Enabled { get { return enabled; } set { enabled = value; } }
        internal string ExpectedName { get { return expectedName; } set { expectedName = value; } }

        internal static bool TryNormalize(string input, out string name)
        {
            name = null;
            if (string.IsNullOrWhiteSpace(input)) return false;
            try
            {
                name = Path.GetFileName(input.Trim().Trim('"'));
                if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name == "." || name == "..") return false;
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
                return name.Length > 4 && name.Length <= 255;
            }
            catch { return false; }
        }
        internal static bool Matches(string expected, string actual)
        {
            return !string.IsNullOrEmpty(expected) && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
        }
        internal static string WindowProcessName(IntPtr window)
        {
            uint pid;
            if (window == IntPtr.Zero || Native.GetWindowThreadProcessId(window, out pid) == 0) return null;
            IntPtr process = Native.OpenProcess(0x1000, false, pid);
            if (process == IntPtr.Zero) return null;
            try
            {
                var buffer = new StringBuilder(1024); uint length = (uint)buffer.Capacity;
                if (Native.QueryFullProcessImageName(process, 0, buffer, ref length)) return Path.GetFileName(buffer.ToString());
                if (System.Runtime.InteropServices.Marshal.GetLastWin32Error() != 122) return null;
                buffer = new StringBuilder(32768); length = (uint)buffer.Capacity;
                return Native.QueryFullProcessImageName(process, 0, buffer, ref length) ? Path.GetFileName(buffer.ToString()) : null;
            }
            finally { Native.CloseHandle(process); }
        }
        private static bool WindowMatches(IntPtr window, string expected)
        {
            string name = WindowProcessName(window);
            if (Matches(expected, name)) return true;
            // Windows 10 UWP windows can be owned by ApplicationFrameHost while
            // their content belongs to Minecraft.Windows.exe.
            if (!Matches("ApplicationFrameHost.exe", name)) return false;
            bool found = false;
            Native.EnumWindowProc callback = delegate(IntPtr child, IntPtr parameter)
            {
                if (Native.IsWindowVisible(child) && Matches(expected, WindowProcessName(child))) { found = true; return false; }
                return true;
            };
            Native.EnumChildWindows(window, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return found;
        }
        internal bool Allows(IntPtr pointerWindow, IntPtr foregroundWindow)
        {
            if (!enabled) return true;
            if (pointerWindow == IntPtr.Zero || foregroundWindow == IntPtr.Zero) return false;
            IntPtr pointerRoot = Native.GetAncestor(pointerWindow, 2);
            IntPtr foregroundRoot = Native.GetAncestor(foregroundWindow, 2);
            lastProcessName = WindowProcessName(foregroundRoot) ?? "не удалось определить";
            // A game can capture the mouse while the system cursor no longer
            // identifies its content window. Respect its actual capture owner.
            if (pointerRoot != foregroundRoot)
            {
                var info = new Native.GuiThreadInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.GuiThreadInfo)) };
                if (!Native.GetGUIThreadInfo(0, ref info) || info.Capture == IntPtr.Zero || Native.GetAncestor(info.Capture, 2) != foregroundRoot) return false;
                return WindowMatches(foregroundRoot, expectedName);
            }
            return WindowMatches(pointerWindow, expectedName) || WindowMatches(foregroundRoot, expectedName);
        }
    }
}
