using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;

namespace PulsePC;

/// <summary>
/// Язык интерфейса: русский (исходный) или английский. Исходные строки остаются в коде по-русски, английские лежат
/// в словарях Lang\en\*.json (встроены в программу): ключ — русская строка, значение — английская.
///   L.T("Текст")                 — перевод строки; без перевода возвращается сама строка;
///   L.F("Освобождено {0}", size)  — то же с подстановкой (string.Format, числа — в культуре языка);
///   Fmt.Plural(n, "игра", "игры", "игр") — во множественном числе по правилам языка.
/// Тексты, заданные в разметке (Text, Content, ToolTip, Header, Title), переводятся сами, когда элемент загружается:
/// см. HookWindows. Строки, которые программа хранит как ключи (названия настроек в settings.json), не переводятся —
/// переводится только то, что показывается.
/// Язык выбирается в «Система» → «Язык» и вступает в силу после перезапуска; по умолчанию — язык Windows
/// (русский, украинский, белорусский, казахский → русский, остальные → английский).
/// </summary>
public static class L
{
    public const string SettingKey = "ui.lang";

    public static bool En { get; private set; }
    public static string Code => En ? "en" : "ru";
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("ru-RU");

    private static Dictionary<string, string> exact = new(StringComparer.Ordinal);
    private static readonly List<Pattern> patterns = new();
    private static readonly Dictionary<string, List<Pattern>> byPrefix = new(StringComparer.Ordinal);
    private static readonly List<Pattern> noPrefix = new();
    private static readonly ConcurrentDictionary<string, string> cache = new(StringComparer.Ordinal);

    private sealed record Pattern(Regex Regex, string Template, int Slots);

    /// <summary>Язык из настройки ("ru", "en" или пусто — по языку Windows). Вызывается один раз при запуске, до первого окна.</summary>
    public static void Init(string setting)
    {
        // Проверочный запуск может задать язык сам: PULSEPC_LANG=en|ru (снимки разделов, самопроверка).
        if (Environment.GetEnvironmentVariable("PULSEPC_LANG") is "ru" or "en") setting = Environment.GetEnvironmentVariable("PULSEPC_LANG");
        En = Resolve(setting) == "en";
        Culture = CultureInfo.GetCultureInfo(En ? "en-US" : "ru-RU");
        if (!En) return;
        Load();
        // Числа, даты и единицы — в культуре языка, в том числе в привязках разметки.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = Culture;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = Culture;
        try
        {
            FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
                new FrameworkPropertyMetadata(System.Windows.Markup.XmlLanguage.GetLanguage(Culture.IetfLanguageTag)));
        }
        catch { }
    }

    /// <summary>"ru" или "en": явный выбор или язык Windows.</summary>
    public static string Resolve(string setting)
    {
        if (setting is "ru" or "en") return setting;
        string ui = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName;
        return ui is "ru" or "uk" or "be" or "kk" ? "ru" : "en";
    }

    /// <summary>Перевод строки (или сама строка, если перевода нет или язык русский).</summary>
    public static string T(string ru)
    {
        if (!En || string.IsNullOrEmpty(ru) || !HasCyrillic(ru)) return ru;
        if (WindowsNames.TryGetValue(ru.Trim(), out var win))
            return ru.Trim() == ru.Trim().ToUpperInvariant() ? win.ToUpperInvariant() : win;   // подпись заглавными — и перевод заглавными
        if (exact.TryGetValue(ru, out var en)) return en;
        if (cache.TryGetValue(ru, out en)) return en;
        en = Match(ru) ?? TrimmedMatch(ru) ?? ru;
        if (cache.Count < 20000) cache[ru] = en;
        return en;
    }

    /// <summary>string.Format по переведённому шаблону: L.F("Свободно {0} из {1}", a, b).</summary>
    public static string F(string ru, params object[] args)
    {
        string template = En && exact.TryGetValue(ru, out var en) ? en : ru;
        if (En && args != null)
            for (int i = 0; i < args.Length; i++)
                if (args[i] is string s) args[i] = Data(s);
        try { return string.Format(Culture, template, args); }
        catch (FormatException) { return string.Format(Culture, ru, args); }
    }

    /// <summary>
    /// Названия стандартных схем питания Windows: на русской Windows они приходят по-русски, а в английском интерфейсе программы
    /// должны быть английскими (так их зовёт английская Windows). Только точные стандартные названия — свои схемы человека
    /// («Игровая», «My plan») остаются как есть. Логика программы сравнивает исходные названия; подмена — только для показа.
    /// </summary>
    private static readonly Dictionary<string, string> WindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Сбалансированная"] = "Balanced",
        ["Высокая производительность"] = "High performance",
        ["Экономия энергии"] = "Power saver",
        ["Максимальная производительность"] = "Ultimate Performance",
    };

    /// <summary>Данные из Windows для показа: в английском интерфейсе стандартные названия схем питания — по-английски.</summary>
    public static string Data(string s) => En && s != null && WindowsNames.TryGetValue(s.Trim(), out var e) ? e : s;

    /// <summary>Выбор между русским и английским текстом прямо в коде — для мест, где словарь неудобен (длинная разметка, HTML).</summary>
    public static string Pick(string ru, string en) => En ? en : ru;

    private static bool HasCyrillic(string s)
    {
        foreach (char c in s) if (c is >= 'А' and <= 'я' or 'Ё' or 'ё') return true;
        return false;
    }

    /// <summary>Строка с пробелами или переводом строки по краям: переводится середина.</summary>
    private static string TrimmedMatch(string ru)
    {
        string core = ru.Trim();
        if (core.Length == ru.Length || core.Length == 0) return null;
        string en = exact.TryGetValue(core, out var v) ? v : Match(core);
        if (en == null) return null;
        int start = ru.IndexOf(core, StringComparison.Ordinal);
        return ru[..start] + en + ru[(start + core.Length)..];
    }

    /// <summary>Строка, собранная из шаблона: «Освобождено 2,1 ГБ» по ключу «Освобождено {0}». Подставленные части тоже переводятся.</summary>
    private static string Match(string ru)
    {
        IEnumerable<Pattern> candidates = noPrefix;
        string head = Head(ru);
        if (head != null && byPrefix.TryGetValue(head, out var list)) candidates = list.Concat(noPrefix);
        foreach (var p in candidates)
        {
            var m = p.Regex.Match(ru);
            if (!m.Success) continue;
            var args = new object[p.Slots];
            for (int i = 0; i < p.Slots; i++) args[i] = Units(T(m.Groups["a" + i].Value));
            try { return string.Format(Culture, p.Template, args); } catch { return null; }
        }
        return null;
    }

    private static string Head(string s) => s.Length >= 3 ? s[..3] : null;

    private static readonly (Regex From, string To)[] UnitFixes =
    {
        (new Regex(@"(\d),(\d)", RegexOptions.Compiled), "$1.$2"),
        (new Regex(@"(\d[\s ])(ГБ|МБ|КБ|ГГц|МГц|Гц|мс|Вт|мин|ч|с)(?![А-Яа-яЁё])", RegexOptions.Compiled), "$1§$2"),
        (new Regex(@"Яндекс Браузер", RegexOptions.Compiled), "Yandex Browser"),
    };
    private static readonly Dictionary<string, string> UnitNames = new()
    {
        ["ГБ"] = "GB", ["МБ"] = "MB", ["КБ"] = "KB", ["ГГц"] = "GHz", ["МГц"] = "MHz", ["Гц"] = "Hz", ["мс"] = "ms", ["Вт"] = "W", ["мин"] = "min", ["ч"] = "h", ["с"] = "s",
    };

    /// <summary>
    /// Подставленная часть старого русского текста (сессии, записанные до английского): «Claude 2,8 ГБ» → «Claude 2.8 GB».
    /// Названия программ и игр не трогаются — только числа и единицы.
    /// </summary>
    private static string Units(string s)
    {
        if (string.IsNullOrEmpty(s) || !HasCyrillic(s)) return s;
        s = UnitFixes[0].From.Replace(s, UnitFixes[0].To);
        s = UnitFixes[1].From.Replace(s, m => m.Groups[1].Value + UnitNames[m.Groups[2].Value]);
        return UnitFixes[2].From.Replace(s, UnitFixes[2].To);
    }

    // ------------------------------------------------------------------ словари

    private static void Load()
    {
        var asm = typeof(L).Assembly;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in asm.GetManifestResourceNames().Where(n => n.StartsWith("lang.en.", StringComparison.Ordinal)).OrderBy(n => n))
        {
            try
            {
                using var s = asm.GetManifestResourceStream(name);
                var part = JsonSerializer.Deserialize<Dictionary<string, string>>(s);
                if (part == null) continue;
                foreach (var (k, v) in part) if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(v)) map[k] = v;
            }
            catch (Exception ex) { Services.Log.Error("словарь " + name, ex); }
        }
        exact = map;
        foreach (var (k, v) in map)
        {
            if (!Regex.IsMatch(k, @"\{\d+(:[^}]*)?\}")) continue;
            var p = Compile(k, v);
            if (p == null) continue;
            patterns.Add(p);
            int brace = k.IndexOf('{');
            string head = brace >= 3 ? k[..3] : null;
            if (head != null) (byPrefix.TryGetValue(head, out var l) ? l : byPrefix[head] = new()).Add(p);
            else noPrefix.Add(p);
        }
        // Длинные шаблоны точнее: «Свободно {0} из {1}» раньше, чем «Свободно {0}».
        foreach (var l in byPrefix.Values) l.Sort((a, b) => b.Template.Length.CompareTo(a.Template.Length));
        noPrefix.Sort((a, b) => b.Template.Length.CompareTo(a.Template.Length));
    }

    /// <summary>Шаблон «Свободно {0:0.0} ГБ» → регулярное выражение ^Свободно (?&lt;a0&gt;.+?) ГБ$.</summary>
    private static Pattern Compile(string ru, string en)
    {
        try
        {
            var sb = new StringBuilder("^");
            int last = 0, slots = 0;
            var seen = new HashSet<int>();
            // Обычный и неразрывный пробел считаются одним: сессии старых версий (и тексты, набранные по-разному) записаны
            // то с тем, то с другим, а перевод должен находиться в обоих случаях.
            static string Lit(string s) => Regex.Escape(s).Replace(" ", "\\ ").Replace("\\ ", "[  ]");
            foreach (Match m in Regex.Matches(ru, @"\{(\d+)(:[^}]*)?\}"))
            {
                sb.Append(Lit(ru[last..m.Index]));
                int n = int.Parse(m.Groups[1].Value);
                sb.Append(seen.Add(n) ? $"(?<a{n}>.+?)" : $"\\k<a{n}>");
                slots = Math.Max(slots, n + 1);
                last = m.Index + m.Length;
            }
            sb.Append(Lit(ru[last..])).Append('$');
            // В английском шаблоне форматы чисел уже не нужны: подставляется готовый текст.
            string template = Regex.Replace(en, @"\{(\d+):[^}]*\}", "{$1}");
            return new Pattern(new Regex(sb.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant), template, slots);
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ разметка

    /// <summary>
    /// Переводить тексты разметки при загрузке каждого элемента: окна, разделы, шаблоны списков. Привязки не трогаются
    /// (их тексты переводит код, который их отдаёт).
    /// </summary>
    public static void HookWindows()
    {
        if (!En) return;
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => Translate(s as DependencyObject)), true);
        // Loaded до обработчиков класса доходит не всегда (рассылается окном напрямую), а SizeChanged приходит каждому элементу
        // при первой раскладке — в том числе строкам списков и разделам, которые показываются позже.
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler((s, e) =>
        {
            if (e.PreviousSize.Width == 0 && e.PreviousSize.Height == 0) Translate(s as DependencyObject);
        }), true);
        EventManager.RegisterClassHandler(typeof(Window), Window.LoadedEvent, new RoutedEventHandler((s, _) => TranslateTree(s as DependencyObject)), true);
        EventManager.RegisterClassHandler(typeof(FrameworkContentElement), FrameworkContentElement.LoadedEvent, new RoutedEventHandler((s, _) => Translate(s as DependencyObject)), true);
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler((s, _) => Translate(s as DependencyObject)), true);
    }

    /// <summary>Текстовые свойства и тип элементов, у которых они есть (ToolTip и подписи для экранного диктора — присоединённые).</summary>
    private static readonly (DependencyProperty Dp, Type On)[] TextProps =
    {
        (TextBlock.TextProperty, typeof(TextBlock)), (ContentControl.ContentProperty, typeof(ContentControl)),
        (FrameworkElement.ToolTipProperty, typeof(FrameworkElement)), (HeaderedContentControl.HeaderProperty, typeof(HeaderedContentControl)),
        (HeaderedItemsControl.HeaderProperty, typeof(HeaderedItemsControl)), (Window.TitleProperty, typeof(Window)), (Run.TextProperty, typeof(Run)),
        (TextBox.TextProperty, typeof(TextBox)),
        (System.Windows.Automation.AutomationProperties.NameProperty, typeof(DependencyObject)),
        (System.Windows.Automation.AutomationProperties.HelpTextProperty, typeof(DependencyObject)),
    };

    /// <summary>
    /// Перевести строковые свойства одного элемента (и строки в TextBlock.Inlines). Значение может быть задано в самом элементе,
    /// в шаблоне (строки списков) или в стиле; привязки и вычисленные значения не трогаются.
    /// </summary>
    public static void Translate(DependencyObject d)
    {
        if (d == null || !En) return;
        if (d is TrackedText) return;                       // переводит сам, по Source
        if (d is TextBox { IsReadOnly: false }) return;     // поле ввода — текст пользователя
        foreach (var (dp, on) in TextProps)
        {
            if (!on.IsInstanceOfType(d)) continue;
            ValueSource src;
            try { src = DependencyPropertyHelper.GetValueSource(d, dp); } catch { continue; }
            if (src.IsExpression || src.IsAnimated) continue;
            if (src.BaseValueSource is not (BaseValueSource.Local or BaseValueSource.ParentTemplate or BaseValueSource.ParentTemplateTrigger
                or BaseValueSource.Style or BaseValueSource.StyleTrigger or BaseValueSource.TemplateTrigger)) continue;
            if (d.GetValue(dp) is not string s || s.Length == 0) continue;
            string t = T(s);
            if (!ReferenceEquals(t, s) && t != s) d.SetCurrentValue(dp, t);
        }
        // Текст из нескольких кусков (<Run>, <Hyperlink>) — переводится каждый кусок.
        if (d is TextBlock tb && DependencyPropertyHelper.GetValueSource(tb, TextBlock.TextProperty).BaseValueSource == BaseValueSource.Default)
            TranslateInlines(tb.Inlines);
    }

    /// <summary>Перевести элемент и всё, что в нём (логическое и визуальное дерево).</summary>
    public static void TranslateTree(DependencyObject d)
    {
        if (d == null || !En) return;
        var seen = new HashSet<DependencyObject>();
        void Walk(DependencyObject x)
        {
            if (x == null || !seen.Add(x)) return;
            Translate(x);
            foreach (var child in LogicalTreeHelper.GetChildren(x).OfType<DependencyObject>()) Walk(child);
            if (x is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D)
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(x); i++) Walk(System.Windows.Media.VisualTreeHelper.GetChild(x, i));
        }
        Walk(d);
    }

    private static void TranslateInlines(InlineCollection inlines)
    {
        foreach (var inline in inlines.ToList())
        {
            if (inline is Run run && !BindingOperations.IsDataBound(run, Run.TextProperty) && run.Text is { Length: > 0 } rt && T(rt) is var rtt && rtt != rt) run.Text = rtt;
            else if (inline is Span span) TranslateInlines(span.Inlines);
        }
    }
}
