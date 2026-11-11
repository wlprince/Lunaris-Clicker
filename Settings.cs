using System;
using System.IO;
using System.Xml.Serialization;

namespace MellowClick
{
    public sealed class Settings
    {
        public int Minimum = 10;
        public int Maximum = 14;
        public bool RightButton = true;
        public int Hotkey = 0x75;
        public uint Modifiers = 0;
        public int HideHotkey = 0x79;
        public uint HideModifiers = 0;
        public bool DarkTheme = false;
        public bool RestrictToApplication = true;
        public string ApplicationName = "Minecraft.Windows.exe";
        public static string FilePath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MellowClick", "settings.xml"); } }
        public static Settings Load()
        {
            try
            {
                using (var stream = File.OpenRead(FilePath))
                {
                    var settings = (Settings)new XmlSerializer(typeof(Settings)).Deserialize(stream);
                    settings.Minimum = Math.Max(1, Math.Min(60, settings.Minimum));
                    settings.Maximum = Math.Max(settings.Minimum, Math.Min(60, settings.Maximum));
                    if (!ValidKey(settings.Hotkey) || settings.Modifiers > 7) { settings.Hotkey = 0x75; settings.Modifiers = 0; }
                    if (!ValidKey(settings.HideHotkey) || settings.HideModifiers > 7 || (settings.HideHotkey == settings.Hotkey && settings.HideModifiers == settings.Modifiers))
                    { settings.HideHotkey = settings.Hotkey == 0x79 && settings.Modifiers == 0 ? 0x78 : 0x79; settings.HideModifiers = 0; }
                    string normalized;
                    settings.ApplicationName = TargetFilter.TryNormalize(settings.ApplicationName, out normalized) ? normalized : "Minecraft.Windows.exe";
                    return settings;
                }
            }
            catch { return new Settings(); }
        }
        public static bool ValidKey(int key)
        {
            return (key >= 0x30 && key <= 0x39) || (key >= 0x41 && key <= 0x5A) || (key >= 0x60 && key <= 0x6F) || (key >= 0x70 && key <= 0x87 && key != 0x7B);
        }
        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            string temporary = FilePath + ".tmp";
            using (var stream = File.Create(temporary)) new XmlSerializer(typeof(Settings)).Serialize(stream, this);
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
            else File.Move(temporary, FilePath);
        }
    }
}
