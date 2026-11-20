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
/// Цвета листа «Чертёж» (те же, что в App.xaml). Бумага и тушь; красный — только выбор, выноски,
/// живой сигнал и главное действие; охра и кармин — предупреждение и ошибка.
/// Кисти заморожены, поэтому ими можно пользоваться из любого потока.
/// </summary>
public static class Palette
{
    public static readonly Brush Base = Make("#F2EEE5");
    public static readonly Brush Raised = Make("#F8F5EE");
    public static readonly Brush Track = Make("#E9E4D8");
    public static readonly Brush Line = Make("#CFC7B8");
    public static readonly Brush LineStrong = Make("#A39B8C");
    public static readonly Brush GridMinor = Make("#E4DED2");
    public static readonly Brush GridMajor = Make("#D3CBBB");
    public static readonly Brush Ink1 = Make("#1A1917");
    public static readonly Brush Ink2 = Make("#45423C");
    public static readonly Brush Ink3 = Make("#6F6A60");
    public static readonly Brush Ink4 = Make("#A8A194");
    public static readonly Brush Accent = Make("#C23B22");
    public static readonly Brush Lime = Accent;
    public static readonly Brush RedHover = Make("#A93219");
    public static readonly Brush RedTint = Make("#F3DFD6");
    public static readonly Brush Warn = Make("#95580F");
    public static readonly Brush WarnTint = Make("#F1E3C8");
    public static readonly Brush Bad = Make("#9E1B32");
    public static readonly Brush None = Brushes.Transparent;

    private static Brush Make(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
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
    public string Title { get; set; }
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
    public string SizeText => pending ? "считаю…" : SizeUnknown ? "заранее неизвестно" : bytes < 0 ? "—" : Fmt.Size(bytes);
}

public class TweakItem : Notifier
{
    public string Title { get; set; }
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

    public static string Size(long bytes)
    {
        if (bytes >= 1L << 30) return (bytes / Gib).ToString("0.0") + " ГБ";
        if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0") + " МБ";
        if (bytes > 0) return "< 1 МБ";
        return "пусто";
    }

    public static string Gb(double gb) => gb.ToString("0.0") + " ГБ";

    /// <summary>«3 ч 59 мин», «12 мин», «40 с».</summary>
    public static string Duration(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} ч {t.Minutes} мин";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes} мин";
        return $"{Math.Max(0, (int)t.TotalSeconds)} с";
    }

    /// <summary>«1 накопитель», «2 накопителя», «5 накопителей».</summary>
    public static string Plural(int n, string one, string few, string many)
    {
        int m10 = n % 10, m100 = n % 100;
        string word = m10 == 1 && m100 != 11 ? one : m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14) ? few : many;
        return n + " " + word;
    }
}
