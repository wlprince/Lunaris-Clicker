using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MellowClick
{
    internal static class SelfTests
    {
        private static readonly List<string> results = new List<string>();
        private static void Assert(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            results.Add("PASS " + name);
        }
        private static void WaitUntil(Func<bool> condition, int timeout = 1500)
        {
            var watch = Stopwatch.StartNew();
            while (!condition() && watch.ElapsedMilliseconds < timeout) Thread.Sleep(5);
            if (!condition()) throw new Exception("Timed out waiting for scheduler.");
        }
        internal static int Run(string path, bool passive = false)
        {
            int exit = 0;
            try
            {
                Assert(Marshal.SizeOf(typeof(Native.Input)) == (IntPtr.Size == 8 ? 40 : 28), "Win32 INPUT ABI size");
                for (int i = 0; i <= 1000; i++)
                {
                    double interval = ClickEngine.IntervalMilliseconds(8, 14, i / 1000.0);
                    if (interval < 1000.0 / 14 || interval > 125) throw new Exception("CPS bounds");
                }
                Assert(ClickEngine.IntervalMilliseconds(12, 12, .5) == 1000.0 / 12, "Equal CPS endpoints and random bounds");
                TestPulses();
                TestRawInput();
                int left = 0, rightCount = 0; bool allowed = true;
                using (var engine = new ClickEngine(delegate { return Volatile.Read(ref allowed); }, delegate(bool r) { if (r) Interlocked.Increment(ref rightCount); else Interlocked.Increment(ref left); return true; }))
                {
                    engine.SetRange(60, 60);
                    engine.ObserveMouse(0x201, 0); Thread.Sleep(80);
                    Assert(left == 0, "Physical hold alone does not click");
                    engine.Active = true; WaitUntil(delegate { return Volatile.Read(ref left) >= 3; });
                    engine.ObserveMouse(0x202, Native.Injected);
                    int before = Volatile.Read(ref left); WaitUntil(delegate { return Volatile.Read(ref left) >= before + 2; });
                    Assert(engine.Held, "Injected release cannot cancel physical hold");
                    engine.ObserveMouse(0x202, 0); Thread.Sleep(40); before = left; Thread.Sleep(80);
                    Assert(left == before, "Physical release stops scheduler");
                    engine.ObserveMouse(0x201, Native.Injected); Thread.Sleep(60);
                    Assert(left == before && !engine.Held, "Injected press cannot activate scheduler");
                    engine.Right = true; engine.ObserveMouse(0x201, 0); Thread.Sleep(60);
                    Assert(rightCount == 0, "Wrong physical button does not trigger");
                    engine.ObserveMouse(0x204, 0); WaitUntil(delegate { return rightCount >= 2; });
                    allowed = false; Thread.Sleep(40); before = rightCount; Thread.Sleep(80);
                    Assert(rightCount == before, "Settings window target exclusion pauses clicks");
                    allowed = true; WaitUntil(delegate { return rightCount > before; });
                    engine.Active = false; Thread.Sleep(40); before = rightCount; Thread.Sleep(80);
                    Assert(before == rightCount, "Toggle off stops during a physical hold");
                    engine.Active = true; WaitUntil(delegate { return rightCount > before; });
                    engine.ResetPhysicalState(); Thread.Sleep(40); before = rightCount; Thread.Sleep(60);
                    Assert(!engine.Active && !engine.Held && rightCount == before, "Session reset clears active and physical states");
                    bool rejected = false; try { engine.SetRange(30, 10); } catch (ArgumentOutOfRangeException) { rejected = true; }
                    Assert(rejected, "Crossed CPS range rejected by core");
                    rejected = false; try { engine.SetRange(0, 100); } catch (ArgumentOutOfRangeException) { rejected = true; }
                    Assert(rejected, "Out-of-bounds CPS rejected by core");
                }
                int failures = 0;
                using (var engine = new ClickEngine(delegate { return true; }, delegate { return false; }))
                {
                    engine.Failed += delegate { Interlocked.Increment(ref failures); };
                    engine.SetRange(60, 60); engine.Active = true; engine.ObserveMouse(0x201, 0);
                    WaitUntil(delegate { return failures > 0; });
                    Assert(!engine.Active && engine.Count == 0, "SendInput failure disarms engine");
                }
                Assert(Settings.ValidKey(0x75) && Settings.ValidKey(0x77) && !Settings.ValidKey(0x7B), "F6 and F8 available for toggle binding; Windows-reserved F12 rejected");
                string normalized;
                Assert(TargetFilter.TryNormalize("  minecraft.windows  ", out normalized) && normalized == "minecraft.windows.exe", "Application name accepts extension-less input and trims spaces");
                Assert(TargetFilter.TryNormalize("\"C:\\Games\\Minecraft.Windows.exe\"", out normalized) && normalized == "Minecraft.Windows.exe", "Application path becomes executable file name");
                Assert(!TargetFilter.TryNormalize("", out normalized) && !TargetFilter.TryNormalize("mine*.exe", out normalized), "Empty and wildcard process filters rejected");
                Assert(TargetFilter.Matches("minecraft.windows.exe", "Minecraft.Windows.EXE") && !TargetFilter.Matches("minecraft.windows.exe", "FakeMinecraft.Windows.exe"), "Process matching is exact and case-insensitive");
                TestThemes();
                TestWindowsPipeline(passive);
            }
            catch (Exception error) { results.Add("FAIL " + error); exit = 1; }
            File.WriteAllLines(path, results);
            return exit;
        }

        private static void TestPulses()
        {
            foreach (bool right in new[] { false, true })
            {
                var trace = new List<string>();
                var pulse = new ClickPulse(delegate(bool r, bool down) { trace.Add((r ? "R" : "L") + (down ? "D" : "U")); return true; });
                var waits = new List<int>();
                var result = pulse.Emit(right, 18, 30, delegate(int ms) { waits.Add(ms); return true; });
                string side = right ? "R" : "L";
                Assert(result == PulseResult.Sent && string.Join(",", trace) == side + "U," + side + "D," + side + "U" && waits.Count == 2 && waits[0] == 18 && waits[1] == 30, "Timed, balanced " + side + " pulse exposes UP and DOWN separately");
            }
            int presses = 0, releases = 0;
            var cancelled = new ClickPulse(delegate(bool r, bool down) { if (down) presses++; else releases++; return true; });
            var cancellation = cancelled.Emit(false, 18, 30, delegate { return false; });
            Assert(cancellation == PulseResult.Cancelled && presses == 0 && releases == 1, "Cancel during release gap never presses a button");
            int phase = 0; presses = 0; releases = 0;
            cancellation = cancelled.Emit(false, 18, 30, delegate { return ++phase == 1; });
            Assert(cancellation == PulseResult.Sent && presses == 1 && releases == 2, "Cancel during hold always sends final UP");
            releases = 0;
            var failed = new ClickPulse(delegate(bool r, bool down) { if (!down) releases++; return !down; });
            Assert(failed.Emit(false, 18, 30, delegate { return true; }) == PulseResult.Failed && releases == 2, "Failed DOWN still attempts final UP");

            int buttonDown = 0, downCount = 0;
            var livePulse = new ClickPulse(delegate(bool r, bool down) { Interlocked.Exchange(ref buttonDown, down ? 1 : 0); if (down) Interlocked.Increment(ref downCount); return true; });
            using (var engine = new ClickEngine(delegate { return true; }, livePulse.Emit))
            {
                engine.SetRange(12, 12); engine.Active = true; engine.ObserveMouse(0x201, 0);
                WaitUntil(delegate { return Volatile.Read(ref buttonDown) == 1; });
                engine.ObserveMouse(0x202, 0);
                WaitUntil(delegate { return Volatile.Read(ref buttonDown) == 0; });
                int stoppedAt = downCount; Thread.Sleep(120);
                Assert(buttonDown == 0 && downCount == stoppedAt, "Physical release interrupts a timed hold without a stuck button");
            }
            using (var engine = new ClickEngine(delegate { return true; }, livePulse.Emit))
            {
                engine.SetRange(12, 12); engine.Active = true; engine.ObserveMouse(0x201, 0);
                WaitUntil(delegate { return Volatile.Read(ref buttonDown) == 1; });
                engine.StopAfterUnhook();
                Assert(buttonDown == 0, "Shutdown waits for final synthetic button release");
            }
        }

        private static void TestRawInput()
        {
            int headerBytes = Marshal.SizeOf(typeof(Native.RawInputHeader));
            int bytes = headerBytes + 24;
            IntPtr buffer = Marshal.AllocHGlobal(bytes);
            try
            {
                Marshal.Copy(new byte[bytes], 0, buffer, bytes);
                var header = new Native.RawInputHeader { Type = 0, Size = (uint)bytes, Device = new IntPtr(1) };
                Marshal.StructureToPtr(header, buffer, false);
                Marshal.WriteInt16(buffer, headerBytes + 4, 1);
                Assert(Native.PhysicalRawButtons(buffer, (uint)bytes) == 1, "Raw mouse physical LEFT DOWN decoded");
                Marshal.WriteInt16(buffer, headerBytes + 4, 8);
                Assert(Native.PhysicalRawButtons(buffer, (uint)bytes) == 8, "Raw mouse physical RIGHT UP decoded");
                header.Device = IntPtr.Zero; Marshal.StructureToPtr(header, buffer, false);
                Assert(Native.PhysicalRawButtons(buffer, (uint)bytes) == 0, "Device-less synthetic raw input cannot activate hold");
                header.Device = new IntPtr(1); Marshal.StructureToPtr(header, buffer, false);
                Marshal.WriteInt32(buffer, headerBytes + 20, unchecked((int)Native.InputTag.ToUInt32()));
                Assert(Native.PhysicalRawButtons(buffer, (uint)bytes) == 0, "Own tagged raw input cannot cancel or activate physical hold");
                Assert(Native.PhysicalRawButtons(buffer, (uint)headerBytes) == 0, "Truncated raw input packet ignored");
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private static void TestThemes()
        {
            var window = Theme.Load("MainWindow.xaml", false);
            try
            {
                Assert(((System.Windows.Media.SolidColorBrush)window.Foreground).Color == (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#252535"), "Light theme resolves dynamic text resources");
                Theme.Apply(window, true);
                Assert(((System.Windows.Media.SolidColorBrush)window.Foreground).Color == (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F0EDF5"), "Dark theme updates existing text resources");
                Assert(((System.Windows.Media.SolidColorBrush)((TextBox)window.FindName("AppBox")).Foreground).Color == (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F0EDF5"), "Application input text remains readable in dark theme");
                Assert(((System.Windows.Media.SolidColorBrush)((CheckBox)window.FindName("AppCheck")).Foreground).Color == (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F0EDF5"), "Application checkbox text remains readable in dark theme");
                Theme.Apply(window, false);
                Assert(((System.Windows.Media.SolidColorBrush)window.Foreground).Color == (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#252535"), "Theme round trip restores light appearance");
            }
            finally { window.Close(); }
            var test = Theme.Load("TestWindow.xaml", true); test.Close();
            Assert(true, "Test pad loads with dark theme resources");
        }

        private static void TestWindowsPipeline(bool passive)
        {
            // A message-only receiver observes injected events through the real global hook.
            // A pair of opposing one-unit moves is needed: Windows discards zero-distance input.
            // This test sends no button events and restores the pointer immediately.
            var parameters = new HwndSourceParameters("MellowClick API test") { ParentWindow = new IntPtr(-3) };
            using (var source = new HwndSource(parameters))
            {
                string executable = Path.GetFileName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                Assert(TargetFilter.Matches(executable, TargetFilter.WindowProcessName(source.Handle)), "Real HWND resolves to exact executable name");
                var filter = new TargetFilter { ExpectedName = executable, Enabled = true };
                Assert(filter.Allows(source.Handle, source.Handle), "Matching foreground application is allowed");
                filter.ExpectedName = "AnotherApplication.exe";
                Assert(!filter.Allows(source.Handle, source.Handle), "Different application is blocked");
                filter.Enabled = false;
                Assert(filter.Allows(source.Handle, source.Handle), "Application restriction can be disabled");
                int observed = 0;
                Native.MouseProc callback = delegate(int code, IntPtr message, IntPtr data)
                {
                    if (code >= 0)
                    {
                        var value = (Native.MouseHookData)Marshal.PtrToStructure(data, typeof(Native.MouseHookData));
                        if (value.ExtraInfo == Native.InputTag && (value.Flags & Native.Injected) != 0) observed++;
                    }
                    return Native.CallNextHookEx(IntPtr.Zero, code, message, data);
                };
                IntPtr hook = Native.SetWindowsHookEx(14, callback, Native.GetModuleHandle(null), 0);
                Assert(hook != IntPtr.Zero, "Real WH_MOUSE_LL installation");
                try
                {
                    if (!passive)
                    {
                    uint inserted = 0;
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        var forward = Native.MouseEvent(0x2001); forward.Data.Mouse.X = 1;
                        var backward = Native.MouseEvent(0x2001); backward.Data.Mouse.X = -1;
                        inserted = Native.SendInput(2, new[] { forward, backward }, Marshal.SizeOf(typeof(Native.Input)));
                    });
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                    var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew();
                    timer.Tick += delegate { if (observed > 0 || watch.ElapsedMilliseconds > 2000) frame.Continue = false; };
                    timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
                    // Allow SendInput's worker to return after the hook callback finishes.
                    WaitUntil(delegate { return Volatile.Read(ref inserted) == 2; });
                    Assert(observed > 0, "Real SendInput -> low-level hook; injected flag preserved");
                    }
                    else results.Add("SKIP SendInput movement (passive test mode)");
                    bool registered = Native.RegisterHotKey(source.Handle, 912, 0x4000, 0x86);
                    Assert(registered, "Real global hotkey registration");
                    Native.UnregisterHotKey(source.Handle, 912);
                }
                finally { Native.UnhookWindowsHookEx(hook); GC.KeepAlive(callback); }
            }
        }
    }
}
