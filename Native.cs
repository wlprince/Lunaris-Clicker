using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MellowClick
{
    internal static class Native
    {
        internal const int WM_HOTKEY = 0x0312;
        internal const int WM_INPUT = 0x00FF;
        internal const uint Injected = 1;
        internal static readonly UIntPtr InputTag = new UIntPtr(0x4D434C4B);
        internal delegate IntPtr MouseProc(int code, IntPtr message, IntPtr data);
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct MouseHookData
        {
            public Point Point; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct MouseInput
        {
            public int X, Y; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput
        {
            public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr ExtraInfo;
        }
        [StructLayout(LayoutKind.Explicit)] internal struct InputUnion
        {
            [FieldOffset(0)] public MouseInput Mouse;
            [FieldOffset(0)] public KeyboardInput Keyboard;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Input
        {
            public uint Type; public InputUnion Data;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct RawInputDevice
        {
            public ushort UsagePage, Usage; public uint Flags; public IntPtr Target;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct RawInputHeader
        {
            public uint Type, Size; public IntPtr Device, WParam;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct GuiThreadInfo
        {
            public uint Size, Flags; public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret; public Rect CaretRect;
        }
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetWindowsHookEx(int id, MouseProc proc, IntPtr module, uint thread);
        [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint length);
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
        internal delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] internal static extern bool EnumChildWindows(IntPtr window, EnumWindowProc callback, IntPtr parameter);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint GetRawInputData(IntPtr input, uint command, IntPtr buffer, ref uint size, uint headerSize);
        [DllImport("user32.dll")] internal static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
        [DllImport("winmm.dll")] internal static extern uint timeBeginPeriod(uint period);
        [DllImport("winmm.dll")] internal static extern uint timeEndPeriod(uint period);

        internal static Input MouseEvent(uint flags)
        {
            return new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = flags, ExtraInfo = InputTag } } };
        }
        internal static bool SendButton(bool right, bool down)
        {
            uint flags = right ? (down ? 0x0008u : 0x0010u) : (down ? 0x0002u : 0x0004u);
            return SendInput(1, new[] { MouseEvent(flags) }, Marshal.SizeOf(typeof(Input))) == 1;
        }
        internal static ushort PhysicalRawButtons(IntPtr buffer, uint bytes)
        {
            int headerBytes = Marshal.SizeOf(typeof(RawInputHeader));
            if (bytes < headerBytes + 24) return 0;
            var header = (RawInputHeader)Marshal.PtrToStructure(buffer, typeof(RawInputHeader));
            if (header.Type != 0 || header.Device == IntPtr.Zero) return 0;
            if (unchecked((uint)Marshal.ReadInt32(buffer, headerBytes + 20)) == InputTag.ToUInt32()) return 0;
            return (ushort)(Marshal.ReadInt16(buffer, headerBytes + 4) & 0x0F);
        }
    }
}
