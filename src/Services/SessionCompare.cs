namespace PulsePC.Services;

/// <summary>Строка сравнения двух сессий: показатель, значения в этой и в другой сессии, разница.</summary>
public class CompareLine
{
    public string Label { get; set; } = "";
    public string This { get; set; } = "";
    public string Other { get; set; } = "";
    public string Delta { get; set; } = "";
    /// <summary>+1 — в этой сессии лучше, −1 — хуже, 0 — не сравнивается или одинаково.</summary>
    public int Better { get; set; }
    /// <summary>Значение — текст (схема питания, заметка), а не число.</summary>
    public bool IsText { get; set; }
}

/// <summary>Итог «Лучшие условия для игры».</summary>
public class BestConditions
{
    public GameSession Best { get; set; }
    /// <summary>Сессий этой игры с оценкой плавности и всего.</summary>
    public int Rated { get; set; }
    public int Total { get; set; }
    public bool Enough => Rated >= 3;
    public string Headline { get; set; } = "";
    public List<string> Differences { get; set; } = new();
}

/// <summary>
/// Сравнение сессий одной игры: две рядом (что изменилось — драйвер, схема питания, заметки, свободная память) и
/// «лучшие условия» — самая плавная из ваших сессий этой игры и чем она отличалась.
/// </summary>
public static class SessionCompare
{
    public static string When(GameSession s) => s.Start.ToString(L.Pick("d MMM, HH:mm", "MMM d, HH:mm"), L.Culture);

    /// <summary>Две сессии рядом: числа и условия.</summary>
    public static List<CompareLine> Lines(GameSession cur, GameSession other)
    {
        var list = new List<CompareLine>
        {
            new() { Label = L.T("когда"), This = When(cur), Other = When(other) },
            new() { Label = L.T("длилась"), This = Fmt.Duration(cur.Duration), Other = Fmt.Duration(other.Duration) },
        };
        void Num(string label, double? a, double? b, string format, string unit, bool higherBetter, double min)
        {
            bool okA = a is double x && x > 0 && !double.IsNaN(x), okB = b is double y && y > 0 && !double.IsNaN(y);
            var line = new CompareLine
            {
                Label = label,
                This = okA ? a.Value.ToString(format, L.Culture) + unit : "—",
                Other = okB ? b.Value.ToString(format, L.Culture) + unit : "—",
            };
            if (okA && okB)
            {
                double d = a.Value - b.Value;
                if (Math.Abs(d) >= min)
                {
                    line.Delta = (d > 0 ? "+" : "−") + Math.Abs(d).ToString(format, L.Culture) + unit;
                    line.Better = (d > 0) == higherBetter ? 1 : -1;
                }
            }
            list.Add(line);
        }
        string pct = L.Pick(" %", "%"), gb = " " + Fmt.GbUnit;
        Num(L.T("FPS в среднем"), cur.AvgFps, other.AvgFps, "0", "", true, 1);
        Num(L.T("худший 1 % кадров, FPS"), cur.Low1Fps, other.Low1Fps, "0", "", true, 1);
        Num(L.T("плавность из 100"), cur.Smoothness, other.Smoothness, "0", "", true, 1);
        Num(L.T("рывков в минуту"), cur.StutterPerMin, other.StutterPerMin, "0.#", "", false, 0.1);
        Num(L.T("макс. температура видеокарты"), cur.MaxTemp, other.MaxTemp, "0", " °C", false, 1);
        Num(L.T("видеокарта в среднем"), cur.AvgGpu, other.AvgGpu, "0", pct, true, 2);
        Num(L.T("минимум свободной памяти"), cur.MinRamGb, other.MinRamGb, "0.0", gb, true, 0.2);
        Num(L.T("свободно памяти в начале"), cur.StartFreeRamGb, other.StartFreeRamGb, "0.0", gb, true, 0.3);
        list.Add(new CompareLine { Label = L.T("драйвер видеокарты"), This = cur.Driver ?? "—", Other = other.Driver ?? "—",
                                   Delta = Changed(cur.Driver, other.Driver) ? L.T("сменился") : "" });
        list.Add(new CompareLine { Label = L.T("схема питания"), This = Plan(cur.PowerPlan), Other = Plan(other.PowerPlan),
                                   Delta = Changed(cur.PowerPlan, other.PowerPlan) ? L.T("сменилась") : "", IsText = true });
        list.Add(new CompareLine { Label = L.T("заметка"), This = Note(cur), Other = Note(other), IsText = true });
        return list;
    }

    private static string Plan(string p) => string.IsNullOrEmpty(p) ? "—" : L.T(p);
    private static string Note(GameSession s) => string.IsNullOrWhiteSpace(s.UserNote) ? "—" : s.NoteLine;
    private static bool Changed(string a, string b) => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && !a.Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Чем сессия «then» отличалась по условиям от «now»: драйвер, схема питания, свободная память в начале, адаптер,
    /// заметка, фоновые программы. Пусто — заметных отличий нет (или условия у старых сессий не записывались).
    /// </summary>
    public static List<string> Differences(GameSession then, GameSession now)
    {
        var d = new List<string>();
        if (Changed(then.Driver, now.Driver))
            d.Add(L.F("Драйвер видеокарты: тогда {0}, в этой — {1}.", then.Driver, now.Driver));
        if (Changed(then.PowerPlan, now.PowerPlan))
            d.Add(L.F("Схема питания: тогда «{0}», в этой — «{1}».", L.T(then.PowerPlan), L.T(now.PowerPlan)));
        if (then.StartFreeRamGb is double a && now.StartFreeRamGb is double b && Math.Abs(a - b) >= 1)
            d.Add(L.F("Свободной памяти в начале: тогда {0:0.0} ГБ, в этой — {1:0.0} ГБ.", a, b));
        if (!string.IsNullOrEmpty(then.GpuUsed) && !string.IsNullOrEmpty(now.GpuUsed) && then.GpuUsed != now.GpuUsed)
            d.Add(L.F("Игру рисовал другой видеоадаптер: тогда {0}, в этой — {1}.", then.GpuUsed, now.GpuUsed));
        if (then.MaxTemp > 0 && now.MaxTemp > 0 && Math.Abs(then.MaxTemp - now.MaxTemp) >= 8)
            d.Add(L.F("Видеокарта грелась до {0:0} °C, в этой — до {1:0} °C.", then.MaxTemp, now.MaxTemp));
        var heavyNow = Heavy(now).Where(n => !Heavy(then).Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        if (heavyNow.Count > 0 && then.Background != null)
            d.Add(L.F("В этой сессии фоном работали {0} — тогда их не было видно.", string.Join(", ", heavyNow.Select(SessionCulprits.Pretty))));
        if (!string.IsNullOrWhiteSpace(then.UserNote))
            d.Add(L.F("Заметка к той сессии: «{0}».", then.NoteLine));
        return d;
    }

    /// <summary>Фоновые программы, которые заметно нагружали компьютер (в среднем от 3 % процессора или 3 МБ/с диска).</summary>
    private static List<string> Heavy(GameSession s) =>
        s.Background?.Where(c => c.Cpu >= 3 || c.DiskMBs >= 3).Select(c => c.Name).ToList() ?? new List<string>();

    /// <summary>
    /// Лучшие условия: самая плавная сессия этой игры и чем она отличалась от выбранной. Честно «сессий мало», если сессий
    /// с оценкой плавности меньше трёх (оценка есть у сессий с версии 1.5 и только там, где записывались кадры).
    /// </summary>
    public static BestConditions Best(IEnumerable<GameSession> sameGame, GameSession current)
    {
        var all = sameGame.ToList();
        var rated = all.Where(x => x.Smoothness != null).ToList();
        var r = new BestConditions { Rated = rated.Count, Total = all.Count };
        if (rated.Count < 3)
        {
            r.Headline = rated.Count == 0
                ? L.F("Сессий мало: у сессий этой игры ({0}) нет оценки плавности. Она появляется там, где записывались кадры (с версии 1.5); нужно хотя бы 3 такие сессии.", all.Count)
                : L.F("Сессий мало: оценка плавности есть у {0} из {1}, а сравнивать условия имеет смысл хотя бы по трём.", rated.Count, all.Count);
            return r;
        }
        var best = rated.OrderByDescending(x => x.Smoothness).ThenByDescending(x => x.Low1Fps ?? 0).First();
        r.Best = best;
        if (ReferenceEquals(best, current) || current?.Smoothness == null)
        {
            r.Headline = ReferenceEquals(best, current)
                ? L.F("Эта сессия — самая плавная из всех с оценкой ({0}): плавность {1:0}.", rated.Count, best.Smoothness)
                : L.F("Плавнее всего игра шла {0}: плавность {1:0}.", When(best), best.Smoothness);
            // Чем лучшая отличалась от обычной: сравнение с самой неровной из остальных. «Тогда» — та, неровная, «в этой» — эта
            // (самая плавная). Если эта сессия без оценки, слова «в этой» были бы неправдой — тогда только заголовок.
            if (!ReferenceEquals(best, current)) return r;
            var worst = rated.Where(x => !ReferenceEquals(x, best)).OrderBy(x => x.Smoothness).First();
            if (worst.Smoothness >= best.Smoothness) return r;                // все одинаково плавные — «самой неровной» нет
            r.Differences = Differences(worst, best);
            if (r.Differences.Count > 0)
                r.Differences.Insert(0, L.F("Для сравнения — самая неровная, {0} (плавность {1:0}):", When(worst), worst.Smoothness));
            return r;
        }
        r.Headline = L.F("Плавнее всего игра шла {0}: плавность {1:0}, в этой сессии — {2:0}.", When(best), best.Smoothness, current.Smoothness);
        r.Differences = Differences(best, current);
        if (r.Differences.Count == 0)
            r.Differences.Add(L.T("Заметных отличий в условиях нет: драйвер, схема питания и свободная память те же. Разница — скорее в самой игре (карта, режим, сервер)."));
        return r;
    }
}
