using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using PulsePC.Blueprint;

namespace PulsePC;

/// <summary>
/// Тема оформления: светлый лист «Чертёж» (бумага и тушь) или тёмный (графитовый лист и светлая тушь).
/// Выбирается кнопкой в заголовке окна и вступает в силу после перезапуска; по умолчанию — как в Windows
/// («Параметры» → «Персонализация» → «Цвета» → режим приложений).
/// Все цвета программы идут из одного места: кисти App.xaml (меняется их цвет до первого окна), Palette (код)
/// и BlueprintPalette (чертежи). Красный остаётся главным акцентом в обеих темах, в тёмной он чуть светлее для контраста.
/// </summary>
public static class Theme
{
    public const string SettingKey = "ui.theme";

    public static bool Dark { get; private set; }
    public static string Code => Dark ? "dark" : "light";

    private static readonly Dictionary<string, (string Light, string Dark)> Colors = new()
    {
        ["Base"] = ("#F2EEE5", "#1C1B19"),
        ["Raised"] = ("#F8F5EE", "#24231F"),
        ["Track"] = ("#E9E4D8", "#2E2C28"),
        ["Line"] = ("#CFC7B8", "#3E3B35"),
        ["LineStrong"] = ("#A39B8C", "#625D54"),
        ["GridMinor"] = ("#E4DED2", "#282622"),
        ["GridMajor"] = ("#D3CBBB", "#36332E"),
        ["Ink1"] = ("#1A1917", "#EEE9DF"),
        ["Ink2"] = ("#45423C", "#CBC4B7"),
        ["Ink3"] = ("#6F6A60", "#9E978B"),
        ["Ink4"] = ("#A8A194", "#6E685E"),
        ["Accent"] = ("#C23B22", "#E2583B"),
        ["Lime"] = ("#C23B22", "#E2583B"),
        ["RedHover"] = ("#A93219", "#EE6D51"),
        ["RedPressed"] = ("#8F2914", "#C9472D"),
        ["RedTint"] = ("#F3DFD6", "#3D2620"),
        ["Warn"] = ("#95580F", "#DDA24B"),
        ["WarnTint"] = ("#F1E3C8", "#3A2F1D"),
        ["Bad"] = ("#9E1B32", "#EC6479"),
        ["BadTint"] = ("#F1D9DC", "#3E2329"),
        ["OnRed"] = ("#FBF8F2", "#FFF9F2"),
        ["WindowEdge"] = ("#BDB5A6", "#4A463F"),
        // Чертежи: грани и глубина.
        ["BpSide"] = ("#ECE7DC", "#22211E"),
        ["BpSide2"] = ("#E4DED2", "#292724"),
        ["BpRecess"] = ("#D8D1C2", "#35322D"),
        ["BpDark"] = ("#45423C", "#0E0E0D"),
    };

    /// <summary>Тема из настройки ("light", "dark" или пусто — как в Windows). Один раз при запуске, до первого окна.</summary>
    public static void Init(string setting)
    {
        if (Environment.GetEnvironmentVariable("PULSEPC_THEME") is "light" or "dark") setting = Environment.GetEnvironmentVariable("PULSEPC_THEME");
        Dark = Resolve(setting) == "dark";
    }

    /// <summary>"light" или "dark": явный выбор или режим приложений Windows.</summary>
    public static string Resolve(string setting)
    {
        if (setting is "light" or "dark") return setting;
        return WindowsDark() ? "dark" : "light";
    }

    public static bool WindowsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public static Color ColorOf(string key) =>
        (Color)ColorConverter.ConvertFromString(Colors.TryGetValue(key, out var c) ? (Dark ? c.Dark : c.Light) : "#FF00FF");

    /// <summary>Цвет тёмной темы независимо от выбранной (оверлей поверх игры всегда тёмный).</summary>
    public static Color DarkColorOf(string key) =>
        (Color)ColorConverter.ConvertFromString(Colors.TryGetValue(key, out var c) ? c.Dark : "#FF00FF");

    /// <summary>Замороженная кисть цвета темы (для кода; можно пользоваться из любого потока).</summary>
    public static Brush Brush(string key)
    {
        var b = new SolidColorBrush(ColorOf(key));
        b.Freeze();
        return b;
    }

    /// <summary>
    /// Перекрасить кисти App.xaml под тему. Меняется цвет самих кистей, а не записи словаря: стили App.xaml уже держат
    /// ссылки на эти кисти, поэтому и они получают новый цвет. Плюс палитра чертежей.
    /// </summary>
    public static void Apply(ResourceDictionary resources)
    {
        if (!Dark) return;
        foreach (var (key, _) in Colors)
        {
            if (!resources.Contains(key))
            {
                if (!key.StartsWith("Bp")) resources[key] = new SolidColorBrush(ColorOf(key));
                continue;
            }
            if (resources[key] is SolidColorBrush brush)
            {
                if (brush.IsFrozen) resources[key] = new SolidColorBrush(ColorOf(key));
                else brush.Color = ColorOf(key);
            }
        }
        // Стиль пиктограмм берёт цвет из привязки с запасным значением — запасное в разметке не может быть «живым»,
        // поэтому стиль пересобирается с кистью темы.
        if (resources["Icon"] is Style icon)
        {
            var fresh = new Style(icon.TargetType, icon.BasedOn);
            foreach (var setter in icon.Setters.OfType<Setter>())
            {
                if (setter.Property == System.Windows.Shapes.Shape.StrokeProperty && setter.Value is System.Windows.Data.Binding b)
                {
                    var nb = new System.Windows.Data.Binding(b.Path.Path) { RelativeSource = b.RelativeSource, FallbackValue = resources["Ink1"] };
                    fresh.Setters.Add(new Setter(setter.Property, nb));
                }
                else fresh.Setters.Add(new Setter(setter.Property, setter.Value, setter.TargetName));
            }
            foreach (var t in icon.Triggers) fresh.Triggers.Add(t);
            resources["Icon"] = fresh;
        }

        BlueprintPalette.Paper = ColorOf("Base");
        BlueprintPalette.Side = ColorOf("BpSide");
        BlueprintPalette.Side2 = ColorOf("BpSide2");
        BlueprintPalette.Recess = ColorOf("BpRecess");
        BlueprintPalette.Dark = ColorOf("BpDark");
        BlueprintPalette.Ink1 = ColorOf("Ink1");
        BlueprintPalette.Ink2 = ColorOf("Ink2");
        BlueprintPalette.Ink3 = ColorOf("Ink3");
        BlueprintPalette.Ink4 = ColorOf("Ink4");
        BlueprintPalette.Red = ColorOf("Accent");
        BlueprintPalette.RedTint = ColorOf("RedTint");
        BlueprintPalette.Warn = ColorOf("Warn");
        BlueprintPalette.Apply();
    }

    /// <summary>Сколько кистей App.xaml оказались замороженными (их стили не перекрасятся) — для самопроверки.</summary>
    public static int FrozenCount(ResourceDictionary resources) =>
        Colors.Keys.Count(k => resources.Contains(k) && resources[k] is Freezable { IsFrozen: true });
}
