using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace PulsePC;

public class Notifier : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Цвета листа «Чертёж» (те же, что в App.xaml; светлые или тёмные — по теме, см. Theme). Бумага и тушь; красный — только выбор, выноски,
/// живой сигнал и главное действие; охра и кармин — предупреждение и ошибка.
/// Кисти заморожены, поэтому ими можно пользоваться из любого потока.
/// </summary>
public static class Palette
{
    public static readonly Brush Base = Theme.Brush("Base");
    public static readonly Brush Raised = Theme.Brush("Raised");
    public static readonly Brush Track = Theme.Brush("Track");
    public static readonly Brush Line = Theme.Brush("Line");
    public static readonly Brush LineStrong = Theme.Brush("LineStrong");
    public static readonly Brush GridMinor = Theme.Brush("GridMinor");
    public static readonly Brush GridMajor = Theme.Brush("GridMajor");
    public static readonly Brush Ink1 = Theme.Brush("Ink1");
    public static readonly Brush Ink2 = Theme.Brush("Ink2");
    public static readonly Brush Ink3 = Theme.Brush("Ink3");
    public static readonly Brush Ink4 = Theme.Brush("Ink4");
    public static readonly Brush Accent = Theme.Brush("Accent");
    public static readonly Brush Lime = Accent;
    public static readonly Brush RedHover = Theme.Brush("RedHover");
    public static readonly Brush RedTint = Theme.Brush("RedTint");
    public static readonly Brush Warn = Theme.Brush("Warn");
    public static readonly Brush WarnTint = Theme.Brush("WarnTint");
    public static readonly Brush Bad = Theme.Brush("Bad");
    public static readonly Brush None = Brushes.Transparent;

}

public class ProcVm : Notifier
{
    private string name = "", count = "", mem = "";
    public string Name { get => name; set => Set(ref name, value); }
    public string Count { get => count; set => Set(ref count, value); }
    public string Mem { get => mem; set => Set(ref mem, value); }
}

public class DriveVm : Notifier
{
    public string Letter { get; set; }
    private string freeText = "";
    public string FreeText { get => freeText; set => Set(ref freeText, value); }
    private double fraction;
    /// <summary>Занятая доля диска, 0..1.</summary>
    public double Fraction { get => fraction; set => Set(ref fraction, value); }
    private Brush barBrush = Palette.Ink2;
    public Brush BarBrush { get => barBrush; set => Set(ref barBrush, value); }
}

public enum Severity { Bad = 0, Warn = 1, Info = 2, Ok = 3 }

public class DiagItem
{
    public Severity Level { get; set; }
    public string Title { get; set; }
    public string Detail { get; set; }
    /// <summary>Число, вынесенное из заголовка (например, количество ошибок). Пусто, если его нет.</summary>
    public string Count { get; set; } = "";
    public Brush BarBrush => Level == Severity.Bad ? Palette.Bad : Level == Severity.Warn ? Palette.Warn : Palette.None;
    public Brush CountBrush => Level == Severity.Bad ? Palette.Bad : Level == Severity.Warn ? Palette.Warn : Palette.Ink1;
}

public class CleanItem : Notifier
{
    /// <summary>Название пункта — по-русски, служит ключом (ссылки «clean:…», плановая очистка). Показывается TitleText.</summary>
    public string Title { get; set; }
    public string TitleText => L.T(Title);
    public string Hint { get; set; }
    /// <summary>Подсчёт размера (ничего не удаляет).</summary>
    public Func<long> Scan { get; set; }
    /// <summary>Удаление; возвращает число освобождённых байт.</summary>
    public Func<long> Clean { get; set; }
    /// <summary>Размер заранее неизвестен (например, очистка старых обновлений Windows).</summary>
    public bool SizeUnknown { get; set; }
    /// <summary>Удалённое этим пунктом нельзя вернуть никаким способом.</summary>
    public bool Irreversible { get; set; }
    /// <summary>Безопасно для плановой очистки раз в неделю: только то, что Windows и программы создают заново сами.</summary>
    public bool Weekly { get; set; }

    private bool selected;
    public bool Selected { get => selected; set => Set(ref selected, value); }

    private bool pending;
    /// <summary>Размер ещё считается или идёт удаление.</summary>
    public bool Pending { get => pending; set { if (Set(ref pending, value)) Raise(nameof(SizeText)); } }

    private long bytes = -1;
    public long Bytes { get => bytes; set { if (Set(ref bytes, value)) Raise(nameof(SizeText)); } }

    private Brush swatch = Brushes.Transparent;
    /// <summary>Образец штриховки категории — легенда к полосе-сечению.</summary>
    public Brush Swatch { get => swatch; set => Set(ref swatch, value); }

    public bool IsEmpty => !SizeUnknown && bytes == 0;
    public string SizeText => pending ? L.T("считаю…") : SizeUnknown ? L.T("заранее неизвестно") : bytes < 0 ? "—" : Fmt.Size(bytes);
}

public class TweakItem : Notifier
{
    /// <summary>Название — по-русски, хранится в настройках (KnownApplied, RevertedTweaks). Показывается TitleText.</summary>
    public string Title { get; set; }
    public string TitleText => L.T(Title);
    public string Hint { get; set; }
    public bool NeedsReboot { get; set; }
    /// <summary>Применение на несколько секунд обрывает сеть — не делать во время игры.</summary>
    public bool DropsNetwork { get; set; }
    public Func<bool> Check { get; set; }
    public Action Apply { get; set; }
    public Action Revert { get; set; }

    private bool desired;
    /// <summary>Положение переключателя: чего хочет пользователь.</summary>
    public bool Desired { get => desired; set => Set(ref desired, value); }

    private bool? applied;
    /// <summary>Фактическое состояние в системе (null — ещё не проверено).</summary>
    public bool? Applied { get => applied; set => Set(ref applied, value); }

    public bool IsChanged => applied != null && desired != applied;
}

public class GameAppItem : Notifier
{
    public string ProcessName { get; set; }
    public string Title { get; set; }
    public long Bytes { get; set; }
    public string Mem => Fmt.Size(Bytes);

    private bool selected = true;
    public bool Selected { get => selected; set => Set(ref selected, value); }
}

public static class Fmt
{
    public const double Gib = 1024.0 * 1024 * 1024;

    // Единицы на языке интерфейса: «ГБ» / «GB». Числа — в культуре языка (2,5 / 2.5).
    public static string GbUnit => L.En ? "GB" : "ГБ";
    public static string MbUnit => L.En ? "MB" : "МБ";
    public static string KbUnit => L.En ? "KB" : "КБ";

    public static string Size(long bytes)
    {
        if (bytes >= 1L << 30) return (bytes / Gib).ToString("0.0", L.Culture) + " " + GbUnit;
        if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0", L.Culture) + " " + MbUnit;
        if (bytes > 0) return "< 1 " + MbUnit;
        return L.En ? "empty" : "пусто";
    }

    public static string Gb(double gb) => gb.ToString("0.0", L.Culture) + " " + GbUnit;

    /// <summary>«3 ч 59 мин», «12 мин», «40 с» (по-английски «3 h 59 min», «12 min», «40 s»).</summary>
    public static string Duration(TimeSpan t)
    {
        string h = L.En ? "h" : "ч", min = L.En ? "min" : "мин", sec = L.En ? "s" : "с";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} {h} {t.Minutes} {min}";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes} {min}";
        return $"{Math.Max(0, (int)t.TotalSeconds)} {sec}";
    }

    /// <summary>
    /// «1 накопитель», «2 накопителя», «5 накопителей». По-английски — единственное число для 1 и множественное для
    /// остальных: перевод формы one — единственное число, перевод формы many — множественное (обе формы есть в словаре).
    /// </summary>
    public static string Plural(int n, string one, string few, string many)
    {
        if (L.En) return n + " " + L.T(n == 1 ? one : many);
        return n + " " + Word(n, one, few, many);
    }

    /// <summary>Только слово в нужной форме, без числа.</summary>
    public static string Word(int n, string one, string few, string many)
    {
        if (L.En) return L.T(n == 1 ? one : many);
        int m10 = n % 10, m100 = n % 100;
        return m10 == 1 && m100 != 11 ? one : m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14) ? few : many;
    }
}
