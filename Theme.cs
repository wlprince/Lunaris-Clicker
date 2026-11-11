using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;

namespace MellowClick
{
    internal static class Theme
    {
        private static readonly HashSet<string> colors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> darkColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "White", "#202024" }, { "#FBFAFD", "#141416" }, { "#F1EFF5", "#101012" },
            { "#F9F7FD", "#141416" }, { "#FAFAFC", "#19191D" }, { "#F7F5FC", "#29272F" },
            { "#DCD9E4", "#38343F" }, { "#E5E2EC", "#2F2B35" }, { "#E7E5ED", "#3C3746" },
            { "#EAE8EF", "#35313D" }, { "#E7E4EF", "#3C3746" }, { "#E2DAED", "#3C3746" },
            { "#252535", "#F0EDF5" }, { "#302B40", "#F0EDF5" }, { "#424151", "#DDD7E8" },
            { "#686675", "#BFB6CF" }, { "#F2EEFF", "#30263F" }, { "#F0ECFC", "#28212F" },
            { "#F1EDFC", "#30263F" }, { "#E4DDF8", "#30263F" }, { "#EEE8FC", "#272130" },
            { "#EDEBF3", "#3A3345" }, { "#9E89EE", "#9B80F0" }, { "#D4D0DF", "#655B72" },
            { "#D3C4F6", "#695185" }, { "#B9A8F2", "#9B80F0" },
            { "#6B51D4", "#C3A8FF" }, { "#6C50CE", "#C3A8FF" }, { "#8064D7", "#C3A8FF" },
            { "#7860E8", "#9D7BEF" }, { "#9B8EAD", "#B0A0C3" }, { "#A09AA9", "#B3A9C0" },
            { "#96909F", "#B3A9C0" }, { "#928B9E", "#B3A9C0" }, { "#7E778A", "#B3A9C0" },
            { "#827A90", "#B3A9C0" }, { "#89749F", "#C3A8FF" }, { "#8C7BA2", "#BBA6D3" },
            { "#7A6A90", "#C3A8FF" }, { "#A099AD", "#A89BB8" }, { "#B3ACBC", "#A89BB8" },
            { "#9A94A7", "#A89BB8" }, { "#A39CAC", "#A89BB8" }, { "#ADA6B7", "#A89BB8" },
            { "#9D96A9", "#A89BB8" }, { "#B1A9BD", "#A89BB8" }, { "#C6C0D1", "#887E99" },
            { "#A097AD", "#A89BB8" }, { "#A18EBF", "#BBA6D3" }
        };
        private static bool IsBrushProperty(string property)
        {
            return property == "Background" || property == "Foreground" || property == "BorderBrush" || property == "Fill" || property == "Stroke";
        }
        private static bool IsColor(string value) { return value == "White" || (value.Length == 7 && value[0] == '#'); }
        private static string Key(string color) { return "Palette_" + color.Replace("#", "").ToUpperInvariant(); }
        internal static Window Load(string resource, bool dark)
        {
            XDocument document;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)) document = XDocument.Load(stream);
            foreach (var element in document.Descendants())
            {
                foreach (var attribute in element.Attributes())
                {
                    bool brush = IsBrushProperty(attribute.Name.LocalName);
                    if (attribute.Name.LocalName == "Value" && element.Name.LocalName == "Setter")
                    {
                        var property = element.Attribute("Property");
                        brush = property != null && IsBrushProperty(property.Value);
                    }
                    // Keep white cursor marks and toggle knobs white in either theme.
                    if (brush && IsColor(attribute.Value) && !(attribute.Name.LocalName == "Fill" && attribute.Value == "White"))
                    {
                        string value = attribute.Value; colors.Add(value);
                        attribute.Value = "{DynamicResource " + Key(value) + "}";
                    }
                }
            }
            var window = (Window)XamlReader.Parse(document.ToString());
            Apply(window, dark);
            return window;
        }
        internal static void Apply(Window window, bool dark)
        {
            foreach (string color in colors)
            {
                string value;
                if (!dark || !darkColors.TryGetValue(color, out value)) value = color;
                window.Resources[Key(color)] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            }
        }
    }
}
