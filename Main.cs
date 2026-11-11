using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

[assembly: AssemblyTitle("Lunaris")]
[assembly: AssemblyDescription("Hold-to-click desktop autoclicker")]
[assembly: AssemblyCompany("Lunaris")]
[assembly: AssemblyProduct("Lunaris")]
[assembly: AssemblyVersion("1.2.1.0")]

namespace MellowClick
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && (args[0] == "--self-test" || args[0] == "--self-test-passive")) return SelfTests.Run(args.Length > 1 ? args[1] : "test-results.txt", args[0] == "--self-test-passive");
            if (!CloseOlderInstance()) return 0;
            bool created;
            using (var mutex = new Mutex(true, @"Local\MellowClick.Desktop", out created))
            {
                if (!created)
                {
                    try { using (var restore = EventWaitHandle.OpenExisting(@"Local\MellowClick.Show")) restore.Set(); }
                    catch { MessageBox.Show("Lunaris уже запущен. Нажми бинд скрытия для возврата окна.", "Lunaris"); }
                    return 0;
                }
                try
                {
                    using (var restore = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\MellowClick.Show"))
                    {
                        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                        var controller = new MainController();
                        var registration = ThreadPool.RegisterWaitForSingleObject(restore, delegate { controller.Window.Dispatcher.BeginInvoke(new Action(controller.RestoreWindow)); }, null, Timeout.Infinite, false);
                        app.Run(controller.Window);
                        registration.Unregister(null);
                        GC.KeepAlive(controller);
                    }
                    return 0;
                }
                catch (Exception error)
                {
                    MessageBox.Show("Не удалось запустить Lunaris.\n\n" + error.Message, "Mellow Click", MessageBoxButton.OK, MessageBoxImage.Error);
                    return 1;
                }
            }
        }
        private static bool CloseOlderInstance()
        {
            var current = Process.GetCurrentProcess();
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (process.Id == current.Id || process.SessionId != current.SessionId || !(process.ProcessName.StartsWith("MellowClick", StringComparison.OrdinalIgnoreCase) || process.ProcessName.StartsWith("Lunaris", StringComparison.OrdinalIgnoreCase))) continue;
                        var info = FileVersionInfo.GetVersionInfo(process.MainModule.FileName);
                        Version existing;
                        if (!((info.ProductName == "Mellow Click" && info.CompanyName == "Mellow") || (info.ProductName == "Lunaris" && info.CompanyName == "Lunaris")) || !Version.TryParse(info.FileVersion, out existing) || existing >= version) continue;
                        try { using (var restore = EventWaitHandle.OpenExisting(@"Local\MellowClick.Show")) restore.Set(); } catch { }
                        for (int i = 0; i < 10 && process.MainWindowHandle == IntPtr.Zero; i++) { Thread.Sleep(50); process.Refresh(); }
                        if (process.HasExited) continue;
                        if (!process.CloseMainWindow() || !process.WaitForExit(3000))
                        {
                            MessageBox.Show("Уже запущена старая версия " + existing + ". Закрой её и снова запусти этот .exe, чтобы использовать " + version + ".", "Lunaris — обновление");
                            return false;
                        }
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
            }
            return true;
        }
    }

    internal sealed class MainController
    {
        internal readonly Window Window;
        private readonly Settings settings;
        private ClickEngine engine;
        private readonly Native.MouseProc mouseProc;
        private IntPtr hook, handle;
        private HwndSource source;
        private readonly Slider minSlider, maxSlider;
        private readonly TextBox minBox, maxBox;
        private readonly RadioButton leftRadio, rightRadio;
        private readonly Button toggle, bind;
        private readonly Button hideBind;
        private readonly CheckBox appCheck;
        private readonly TextBox appBox;
        private readonly TextBlock appHint, hideHint;
        private readonly TargetFilter targetFilter = new TargetFilter();
        private readonly TextBlock statusTitle, statusHint, rangeLabel, bindHint, saveLabel;
        private readonly System.Windows.Shapes.Ellipse statusDot;
        private readonly DispatcherTimer refresh, saveTimer;
        private bool syncing, capturing, closed, hookReady, hotkeyReady;
        private bool captureHide, hideReady, hidden, testWasVisible;
        private IntPtr testHandle;
        private IntPtr rawBuffer;
        private bool rawReady;
        private string inputError;
        private TestWindow testWindow;
        internal const int ToggleId = 401, HideId = 403;

        private T Find<T>(string name) where T : class { return (T)Window.FindName(name); }
        private static Brush Brush(string hex) { return (Brush)new BrushConverter().ConvertFromString(hex); }

        internal MainController()
        {
            settings = Settings.Load();
            Window = Theme.Load("MainWindow.xaml", settings.DarkTheme);
            // Fit smaller displays while keeping every control accessible.
            double scale = Math.Min(1, Math.Min((SystemParameters.WorkArea.Width - 10) / Window.Width, (SystemParameters.WorkArea.Height - 10) / Window.Height));
            if (scale < 1)
            {
                var content = (FrameworkElement)Window.Content; Window.Content = null;
                content.Width = Window.Width; content.Height = Window.Height;
                Window.Content = new Viewbox { Child = content, Stretch = Stretch.Uniform };
                Window.Width *= scale; Window.Height *= scale;
            }
            minSlider = Find<Slider>("MinSlider"); maxSlider = Find<Slider>("MaxSlider");
            minBox = Find<TextBox>("MinBox"); maxBox = Find<TextBox>("MaxBox");
            leftRadio = Find<RadioButton>("LeftRadio"); rightRadio = Find<RadioButton>("RightRadio");
            toggle = Find<Button>("ToggleButton"); bind = Find<Button>("BindButton");
            hideBind = Find<Button>("HideBindButton"); hideHint = Find<TextBlock>("HideHint");
            appCheck = Find<CheckBox>("AppCheck"); appBox = Find<TextBox>("AppBox"); appHint = Find<TextBlock>("AppHint");
            appCheck.IsChecked = settings.RestrictToApplication; appBox.Text = settings.ApplicationName;
            targetFilter.Enabled = settings.RestrictToApplication; targetFilter.ExpectedName = settings.ApplicationName;
            statusTitle = Find<TextBlock>("StatusTitle"); statusHint = Find<TextBlock>("StatusHint");
            rangeLabel = Find<TextBlock>("RangeLabel"); bindHint = Find<TextBlock>("BindHint"); saveLabel = Find<TextBlock>("SaveLabel");
            statusDot = Find<System.Windows.Shapes.Ellipse>("StatusDot");
            mouseProc = OnMouse;
            engine = new ClickEngine(TargetAllowed, new ClickPulse(Native.SendButton).Emit);
            engine.Right = settings.RightButton;
            engine.SetRange(settings.Minimum, settings.Maximum);
            engine.Failed += delegate { Window.Dispatcher.BeginInvoke(new Action(delegate { if (!closed) { SetActive(false); inputError = "Windows не принимает клики в этом окне."; UpdateStatus(); } })); };
            saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            saveTimer.Tick += delegate { saveTimer.Stop(); Save(); };
            refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            refresh.Tick += delegate { UpdateStatus(); };
            SyncRange();
            rightRadio.IsChecked = settings.RightButton; leftRadio.IsChecked = !settings.RightButton;
            UpdateBind();
            UpdateThemeIcons();
            minSlider.ValueChanged += delegate { if (!syncing) SetRange((int)minSlider.Value, Math.Max((int)minSlider.Value, settings.Maximum)); };
            maxSlider.ValueChanged += delegate { if (!syncing) SetRange(Math.Min(settings.Minimum, (int)maxSlider.Value), (int)maxSlider.Value); };
            WireBox(minBox, true); WireBox(maxBox, false);
            leftRadio.Checked += delegate { SelectButton(false); };
            rightRadio.Checked += delegate { SelectButton(true); };
            toggle.Click += delegate { CommitBox(minBox, true); CommitBox(maxBox, false); SetActive(!engine.Active); };
            bind.Click += delegate { if (capturing) CancelCapture(); else BeginCapture(false); };
            hideBind.Click += delegate { if (capturing) CancelCapture(); else BeginCapture(true); };
            Find<Button>("ThemeButton").Click += delegate { settings.DarkTheme = !settings.DarkTheme; ApplyTheme(); ScheduleSave(); };
            appCheck.Checked += delegate { SelectApplicationRestriction(true); };
            appCheck.Unchecked += delegate { SelectApplicationRestriction(false); };
            appBox.TextChanged += delegate { if (!syncing) { engine.Active = false; targetFilter.ExpectedName = ""; appHint.Text = "Enter — применить имя"; UpdateStatus(); } };
            appBox.LostKeyboardFocus += delegate { CommitApplication(); };
            appBox.PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { CommitApplication(); Keyboard.ClearFocus(); e.Handled = true; } };
            Find<Button>("CloseButton").Click += delegate { Window.Close(); };
            Find<Button>("MinimizeButton").Click += delegate { Window.WindowState = WindowState.Minimized; };
            Find<Button>("PinButton").Click += delegate { Window.Topmost = !Window.Topmost; Find<Button>("PinButton").ToolTip = Window.Topmost ? "Открепить окно" : "Поверх всех окон"; };
            Find<Border>("DragArea").MouseLeftButtonDown += delegate { Window.DragMove(); };
            Find<Button>("TestButton").Click += delegate { OpenTest(); };
            Window.PreviewKeyDown += OnKey;
            Window.Deactivated += delegate { if (capturing) CancelCapture(); };
            Window.SourceInitialized += OnReady;
            Window.Closed += OnClosed;
            Window.Loaded += delegate { refresh.Start(); UpdateStatus(); };
        }
        private bool TargetAllowed()
        {
            if (handle == IntPtr.Zero) return false;
            Native.Point point;
            if (!Native.GetCursorPos(out point)) return false;
            IntPtr target = Native.WindowFromPoint(point);
            if (target == IntPtr.Zero || Native.GetAncestor(target, 2) == handle) return false;
            IntPtr foreground = Native.GetForegroundWindow();
            if (testHandle != IntPtr.Zero && Native.GetAncestor(target, 2) == testHandle && Native.GetAncestor(foreground, 2) == testHandle) return true;
            return targetFilter.Allows(target, foreground);
        }
        private void OnReady(object sender, EventArgs e)
        {
            handle = new WindowInteropHelper(Window).Handle;
            source = HwndSource.FromHwnd(handle); source.AddHook(OnMessage);
            hook = Native.SetWindowsHookEx(14, mouseProc, Native.GetModuleHandle(null), 0);
            rawBuffer = Marshal.AllocHGlobal(256);
            rawReady = Native.RegisterRawInputDevices(new[] { new Native.RawInputDevice { UsagePage = 1, Usage = 2, Flags = 0x100, Target = handle } }, 1, (uint)Marshal.SizeOf(typeof(Native.RawInputDevice)));
            hookReady = hook != IntPtr.Zero || rawReady;
            hotkeyReady = RegisterToggle();
            hideReady = RegisterHide();
            if (!hookReady) { toggle.IsEnabled = false; statusHint.Text = "Не удалось подключить мышь. Перезапусти приложение."; }
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerMode;
        }
        private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0)
            {
                int msg = message.ToInt32();
                if (msg == 0x201 || msg == 0x202 || msg == 0x204 || msg == 0x205)
                {
                    var info = (Native.MouseHookData)Marshal.PtrToStructure(data, typeof(Native.MouseHookData));
                    engine.ObserveMouse(msg, info.Flags);
                }
            }
            return Native.CallNextHookEx(hook, code, message, data);
        }
        private bool RegisterToggle() { return Native.RegisterHotKey(handle, ToggleId, settings.Modifiers | 0x4000, (uint)settings.Hotkey); }
        private bool RegisterHide() { return Native.RegisterHotKey(handle, HideId, settings.HideModifiers | 0x4000, (uint)settings.HideHotkey); }
        private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_INPUT && rawReady)
            {
                uint capacity = 256;
                uint bytes = Native.GetRawInputData(lParam, 0x10000003, rawBuffer, ref capacity, (uint)Marshal.SizeOf(typeof(Native.RawInputHeader)));
                if (bytes != uint.MaxValue && bytes <= 256)
                {
                    ushort buttons = Native.PhysicalRawButtons(rawBuffer, bytes);
                    if ((buttons & 1) != 0) engine.ObserveMouse(0x201, 0);
                    if ((buttons & 2) != 0) engine.ObserveMouse(0x202, 0);
                    if ((buttons & 4) != 0) engine.ObserveMouse(0x204, 0);
                    if ((buttons & 8) != 0) engine.ObserveMouse(0x205, 0);
                }
                // Leave WM_INPUT unhandled so DefWindowProc performs its cleanup.
            }
            if (msg == Native.WM_HOTKEY)
            {
                if (wParam.ToInt32() == ToggleId && !capturing) { CommitPendingBox(); SetActive(!engine.Active); handled = true; }
                if (wParam.ToInt32() == HideId && !capturing) { ToggleWindowVisibility(); handled = true; }
            }
            return IntPtr.Zero;
        }
        private void SetActive(bool value)
        {
            if (capturing) CancelCapture();
            inputError = null;
            engine.Active = value && hookReady && (!settings.RestrictToApplication || CommitApplication());
            UpdateStatus();
        }
        private void SelectButton(bool right)
        {
            SetActive(false); engine.Right = right; settings.RightButton = right; ScheduleSave(); UpdateStatus();
        }
        private static string KeyName(int key, uint modifiers)
        {
            string name = new KeyConverter().ConvertToString(KeyInterop.KeyFromVirtualKey(key));
            if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) name = name.Substring(1);
            return ((modifiers & 2) != 0 ? "Ctrl + " : "") + ((modifiers & 1) != 0 ? "Alt + " : "") + ((modifiers & 4) != 0 ? "Shift + " : "") + name;
        }
        private string KeyName() { return KeyName(settings.Hotkey, settings.Modifiers); }
        private void UpdateBind()
        {
            bind.Content = capturing && !captureHide ? "Нажми клавишу…" : KeyName() + "   ⌨";
            hideBind.Content = capturing && captureHide ? "Нажми клавишу…" : KeyName(settings.HideHotkey, settings.HideModifiers) + "   ⌨";
        }
        private void BeginCapture(bool hide)
        {
            engine.Active = false;
            inputError = null;
            captureHide = hide;
            Native.UnregisterHotKey(handle, hide ? HideId : ToggleId); capturing = true;
            (hide ? hideBind : bind).Focus(); UpdateBind(); UpdateStatus();
        }
        private void CancelCapture()
        {
            if (captureHide) hideReady = RegisterHide(); else hotkeyReady = RegisterToggle();
            capturing = false; UpdateBind(); UpdateStatus();
        }
        private void OnKey(object sender, KeyEventArgs e)
        {
            if (!capturing) return;
            e.Handled = true;
            if (e.IsRepeat) return;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { CancelCapture(); return; }
            if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftShift || key == Key.RightShift || key == Key.LeftAlt || key == Key.RightAlt) return;
            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (!Settings.ValidKey(vk)) { statusHint.Text = "Выбери букву, цифру или F-клавишу. Esc — отмена."; return; }
            var modifiers = Keyboard.Modifiers;
            if ((modifiers & ModifierKeys.Windows) != 0) { statusHint.Text = "Используй Ctrl, Alt или Shift вместо Win."; return; }
            uint mods = ((modifiers & ModifierKeys.Alt) != 0 ? 1u : 0) | ((modifiers & ModifierKeys.Control) != 0 ? 2u : 0) | ((modifiers & ModifierKeys.Shift) != 0 ? 4u : 0);
            if ((captureHide ? settings.Hotkey == vk && settings.Modifiers == mods : settings.HideHotkey == vk && settings.HideModifiers == mods))
            { statusHint.Text = "Этот бинд уже используется кликером. Esc — отмена."; return; }
            if (!Native.RegisterHotKey(handle, captureHide ? HideId : ToggleId, mods | 0x4000, (uint)vk)) { statusHint.Text = "Эта клавиша занята. Выбери другую. Esc — отмена."; return; }
            if (captureHide) { settings.HideHotkey = vk; settings.HideModifiers = mods; hideReady = true; }
            else { settings.Hotkey = vk; settings.Modifiers = mods; hotkeyReady = true; }
            capturing = false;
            UpdateBind(); UpdateStatus(); ScheduleSave();
        }
        private void WireBox(TextBox box, bool min)
        {
            box.PreviewTextInput += delegate(object sender, TextCompositionEventArgs e) { foreach (char c in e.Text) if (c < '0' || c > '9') e.Handled = true; };
            box.LostKeyboardFocus += delegate { CommitBox(box, min); };
            box.PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { CommitBox(box, min); Keyboard.ClearFocus(); e.Handled = true; } };
            DataObject.AddPastingHandler(box, delegate(object sender, DataObjectPastingEventArgs e) { int n; if (!e.DataObject.GetDataPresent(typeof(string)) || !int.TryParse((string)e.DataObject.GetData(typeof(string)), out n)) e.CancelCommand(); });
        }
        private void CommitBox(TextBox box, bool min)
        {
            int value;
            if (!int.TryParse(box.Text, out value)) { SyncRange(); return; }
            value = Math.Max(1, Math.Min(60, value));
            if (min) SetRange(value, Math.Max(value, settings.Maximum)); else SetRange(Math.Min(value, settings.Minimum), value);
        }
        private void CommitPendingBox()
        {
            if (maxBox.IsKeyboardFocusWithin) CommitBox(maxBox, false);
            else if (minBox.IsKeyboardFocusWithin) CommitBox(minBox, true);
            else if (appBox.IsKeyboardFocusWithin) CommitApplication();
        }
        private bool CommitApplication()
        {
            string name;
            if (!TargetFilter.TryNormalize(appBox.Text, out name))
            {
                targetFilter.ExpectedName = "";
                appHint.Text = "Укажи имя .exe процесса";
                return false;
            }
            syncing = true; appBox.Text = name; syncing = false;
            targetFilter.ExpectedName = name;
            if (settings.ApplicationName != name) { settings.ApplicationName = name; ScheduleSave(); }
            appHint.Text = settings.RestrictToApplication ? "Только в активном окне" : "Клики в любом приложении";
            return true;
        }
        private void SelectApplicationRestriction(bool value)
        {
            SetActive(false); settings.RestrictToApplication = value; targetFilter.Enabled = value;
            CommitApplication(); ScheduleSave(); UpdateStatus();
        }
        private void UpdateThemeIcons()
        {
            var sun = Find<TextBlock>("SunIcon"); var moon = Find<TextBlock>("MoonIcon");
            sun.Foreground = Brush(settings.DarkTheme ? "#7C728A" : "#7860E8");
            moon.Foreground = Brush(settings.DarkTheme ? "#C3A8FF" : "#B3ACBC");
            sun.FontWeight = settings.DarkTheme ? FontWeights.Normal : FontWeights.Bold;
            moon.FontWeight = settings.DarkTheme ? FontWeights.Bold : FontWeights.Normal;
            Find<Button>("ThemeButton").ToolTip = settings.DarkTheme ? "Тёмная тема · нажми для светлой" : "Светлая тема · нажми для тёмной";
        }
        private void ApplyTheme()
        {
            Theme.Apply(Window, settings.DarkTheme);
            if (testWindow != null) Theme.Apply(testWindow.Window, settings.DarkTheme);
            UpdateThemeIcons(); UpdateStatus();
        }
        internal void ToggleWindowVisibility()
        {
            if (hidden) { RestoreWindow(); return; }
            if (!hideReady) return;
            if (capturing) CancelCapture();
            CommitPendingBox(); Save();
            testWasVisible = testWindow != null && testWindow.Window.IsVisible;
            if (testWasVisible) testWindow.Window.Hide();
            hidden = true;
            // A hidden top-level window is absent from the taskbar and Alt+Tab.
            // Keep its HWND alive so global hotkeys and mouse hooks stay registered.
            Window.Hide();
        }
        internal void RestoreWindow()
        {
            if (closed) return;
            hidden = false; Window.Show(); Window.WindowState = WindowState.Normal; Window.Activate();
            if (testWasVisible && testWindow != null) testWindow.Window.Show();
            testWasVisible = false;
        }
        private void SetRange(int min, int max)
        {
            settings.Minimum = min; settings.Maximum = max;
            engine.SetRange(min, max); SyncRange(); ScheduleSave();
        }
        private void SyncRange()
        {
            syncing = true;
            minSlider.Value = settings.Minimum; maxSlider.Value = settings.Maximum;
            minBox.Text = settings.Minimum.ToString(); maxBox.Text = settings.Maximum.ToString();
            rangeLabel.Text = settings.Minimum + "–" + settings.Maximum + " CPS";
            syncing = false;
        }
        private void ScheduleSave() { saveLabel.Text = "Сохраняем…"; saveTimer.Stop(); saveTimer.Start(); }
        private void Save()
        {
            try { settings.Save(); saveLabel.Text = "Настройки сохранены"; }
            catch { saveLabel.Text = "Не удалось сохранить настройки"; }
        }
        private void UpdateStatus()
        {
            bool active = engine.Active;
            bindHint.Text = KeyName() + " — включить / выключить";
            bool targetAllowed = active && TargetAllowed();
            bool clicking = active && engine.Held && targetAllowed;
            statusTitle.Text = capturing ? "Новый бинд" : active ? (clicking ? "Клики активны" : "Готов к удержанию") : "Выключен";
            statusDot.Fill = Brush(active ? "#42B78B" : "#B0A5C4");
            if (!capturing)
            {
                if (!hookReady && handle != IntPtr.Zero) statusHint.Text = "Не удалось подключить мышь. Перезапусти приложение.";
                else if (!hotkeyReady && handle != IntPtr.Zero) statusHint.Text = "Бинд занят. Измени его ниже или включи переключатель.";
                else if (inputError != null) statusHint.Text = inputError;
                else statusHint.Text = active ? (clicking ? "Отпусти кнопку — клики остановятся" : "Зажми " + (settings.RightButton ? "ПКМ" : "ЛКМ") + " в нужном окне") : KeyName() + " — включить, затем зажми " + (settings.RightButton ? "ПКМ" : "ЛКМ");
            }
            else if (!statusHint.Text.Contains("Выбери") && !statusHint.Text.Contains("Esc") && !statusHint.Text.Contains("Используй")) statusHint.Text = "Нажми клавишу. Ctrl / Alt / Shift + клавиша тоже работают.";
            hideHint.Text = !hideReady && handle != IntPtr.Zero ? "Бинд занят — выбери другую клавишу" : "Продолжает работать без окна на панели задач";
            Find<TextBlock>("InputState").Text = "Последняя цель: " + targetFilter.LastProcessName + "\nУдержание: " + (engine.Held ? "да" : "нет") + " · кликов: " + engine.Count;
            if (active && settings.RestrictToApplication && !targetAllowed && !capturing) statusHint.Text = "Ожидание окна: " + settings.ApplicationName;
            toggle.Background = Brush(active ? "#7860E8" : settings.DarkTheme ? "#51445E" : "#C8BFD8");
            toggle.ApplyTemplate();
            var knob = toggle.Template.FindName("SwitchKnob", toggle) as FrameworkElement;
            if (knob != null) knob.HorizontalAlignment = active ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            System.Windows.Automation.AutomationProperties.SetName(toggle, active ? "Выключить автокликер" : "Включить автокликер");
            if (testWindow != null) testWindow.Update(active, settings.RightButton, KeyName(), engine.Count);
        }
        private void OpenTest()
        {
            if (capturing) CancelCapture();
            if (testWindow != null) { testWindow.Window.Activate(); return; }
            testWindow = new TestWindow(settings.DarkTheme); testWindow.Window.Owner = Window;
            testWindow.Window.Closed += delegate { SetActive(false); testWindow = null; testHandle = IntPtr.Zero; };
            testWindow.Window.Show(); testHandle = new WindowInteropHelper(testWindow.Window).Handle; UpdateStatus();
        }
        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e) { Window.Dispatcher.BeginInvoke(new Action(delegate { if (!closed) { engine.ResetPhysicalState(); UpdateStatus(); } })); }
        private void OnPowerMode(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Suspend) OnSessionSwitch(sender, null); }
        private void OnClosed(object sender, EventArgs e)
        {
            closed = true; refresh.Stop(); saveTimer.Stop();
            engine.ResetPhysicalState();
            if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
            if (rawReady) Native.RegisterRawInputDevices(new[] { new Native.RawInputDevice { UsagePage = 1, Usage = 2, Flags = 1, Target = IntPtr.Zero } }, 1, (uint)Marshal.SizeOf(typeof(Native.RawInputDevice)));
            if (rawBuffer != IntPtr.Zero) { Marshal.FreeHGlobal(rawBuffer); rawBuffer = IntPtr.Zero; }
            Native.UnregisterHotKey(handle, ToggleId); Native.UnregisterHotKey(handle, HideId);
            SystemEvents.SessionSwitch -= OnSessionSwitch; SystemEvents.PowerModeChanged -= OnPowerMode;
            if (source != null) source.RemoveHook(OnMessage);
            engine.StopAfterUnhook(); Save();
        }
    }

    internal sealed class TestWindow
    {
        internal readonly Window Window;
        private readonly TextBlock number, hint, details;
        private long count, baseline = -1;
        private readonly System.Diagnostics.Stopwatch watch = new System.Diagnostics.Stopwatch();
        internal TestWindow(bool dark)
        {
            Window = Theme.Load("TestWindow.xaml", dark);
            number = (TextBlock)Window.FindName("ClickCount"); hint = (TextBlock)Window.FindName("Hint"); details = (TextBlock)Window.FindName("Details");
            var pad = (Border)Window.FindName("Pad");
            pad.PreviewMouseDown += delegate(object sender, MouseButtonEventArgs e) { if (e.ChangedButton != MouseButton.Left && e.ChangedButton != MouseButton.Right) return; if (!watch.IsRunning) watch.Start(); count++; number.Text = count.ToString(); e.Handled = true; };
            ((Button)Window.FindName("ResetButton")).Click += delegate { count = 0; number.Text = "0"; watch.Reset(); baseline = -1; };
        }
        internal void Update(bool active, bool right, string key, long sent)
        {
            if (baseline < 0) baseline = sent;
            hint.Text = active ? "Зажми " + (right ? "ПКМ" : "ЛКМ") + " на площадке · " + key + " — выключить" : "Нажми " + key + ", затем зажми " + (right ? "ПКМ" : "ЛКМ");
            details.Text = "Получено на площадке · отправлено автокликером: " + (sent - baseline);
        }
    }
}

