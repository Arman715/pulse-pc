using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PulsePC.Services;

/// <summary>
/// Карточка сессии для соцсетей и чатов: лист 1080×1350 в стиле «Чертёж» — бумага в клетку, штамп «ПРОТОКОЛ»,
/// игра, дата, длительность, средний FPS и худший 1 %, «что ограничивало FPS» тремя полосами, главные причины просадок
/// и знак «Пульс ПК» внизу. Рисуется без окна (DrawingVisual → RenderTargetBitmap).
/// На карточке нет имени пользователя, железа и серийных номеров. Температуры процессора нет и быть не должно.
/// </summary>
public static class ShareCard
{
    public const int Width = 1080, Height = 1350;
    private const double M = 84;                 // поле листа до содержимого
    private const double Frame = 36;             // рамка листа

    private const string Nb = " ";

    private static readonly FontFamily Din = new("Bahnschrift");
    private static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");
    private static readonly FontFamily Text = new("Segoe UI Variable Text, Segoe UI");
    private static readonly FontFamily Small = new("Segoe UI Variable Small, Segoe UI");
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    // ================================================================ общие с отчётом тексты

    /// <summary>Название причины просадки, как в отчёте.</summary>
    public static string CauseName(string cause) => cause switch
    {
        "ram" => L.T("Не хватило памяти"),
        "gpu-heat" => L.T("Нагрев видеокарты"),
        "disk" => L.T("Диск не успевал"),
        "cpu" => L.T("Процессор не успевал"),
        "gpu" => L.T("Видеокарта на пределе"),
        "net" => L.T("Сеть"),
        _ => L.T("Причина не найдена"),
    };

    /// <summary>Вывод «что ограничивало FPS» и совет; null, если у сессии нет разбора.</summary>
    public static (string Verdict, string Advice)? Bottleneck(GameSession s)
    {
        if (s.BoundGpuPct == null) return null;
        double gpuPct = s.BoundGpuPct ?? 0, cpuPct = s.BoundCpuPct ?? 0, otherPct = s.BoundOtherPct ?? 0;
        if (gpuPct >= 50)
            return (L.T("Ограничивает видеокарта"),
                L.T("Процессор справляется с запасом. Поднять FPS можно настройками графики: тени, сглаживание, разрешение рендера, DLSS в режиме «Качество»."));
        if (cpuPct >= 40 && cpuPct >= gpuPct)
            return (L.T("Ограничивает процессор"),
                L.T("Если снизить графику, FPS почти не вырастет. Помогает другое: закрыть фоновые программы, уменьшить дальность прорисовки, число объектов и теней — их считает процессор. DLSS здесь почти бесполезен."));
        if (otherPct >= 50)
            return (L.T("Запас есть и у процессора, и у видеокарты"),
                L.T("FPS сдерживает ограничитель: лимит FPS в игре или в драйвере, V-Sync или сама игра (меню, ожидание сервера). Нужно больше FPS — проверьте эти настройки."));
        return (L.T("Упор менялся по ходу игры"),
            L.T("В тяжёлых сценах на пределе была видеокарта, в остальное время — процессор. Общий совет: средние тени и дальность прорисовки."));
    }

    /// <summary>Три полосы «что ограничивало FPS»: подпись, доля 0..1.</summary>
    public static (string Label, double Pct)[] BoundBars(GameSession s) => new[]
    {
        (L.T("видеокарта загружена полностью"), s.BoundGpuPct ?? 0),
        (L.T("процессор не успевает"), s.BoundCpuPct ?? 0),
        (L.T("запас у обоих (лимит FPS, меню)"), s.BoundOtherPct ?? 0),
    };

    // ================================================================ сохранение

    /// <summary>Папка «Изображения\Пульс ПК» (по-английски — «Pictures\Pulse PC»).</summary>
    public static string Folder
    {
        get
        {
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrEmpty(pictures)) pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
            return Path.Combine(pictures, L.T("Пульс ПК"));
        }
    }

    /// <summary>Нарисовать карточку и сохранить PNG: «Изображения\Пульс ПК\&lt;игра&gt;-&lt;дата&gt;.png». Возвращает путь и картинку.</summary>
    public static (string Path, BitmapSource Image) Save(GameSession s)
    {
        var bmp = Render(s);
        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, FileName(s));
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(path)) enc.Save(fs);
        return (path, bmp);
    }

    public static string FileName(GameSession s)
    {
        string game = string.IsNullOrWhiteSpace(s.Game) ? L.T("Игра") : s.Game.Trim();
        foreach (char c in Path.GetInvalidFileNameChars()) game = game.Replace(c, ' ');
        game = string.Join(" ", game.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (game.Length > 60) game = game[..60].TrimEnd();
        return $"{game}-{s.Start:yyyy-MM-dd-HHmm}.png";
    }

    // ================================================================ рисование

    public static BitmapSource Render(GameSession s)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) Draw(dc, s);
        var bmp = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    private static void Draw(DrawingContext dc, GameSession s)
    {
        double right = Width - M, w = right - M;
        dc.DrawRectangle(Palette.Base, null, new Rect(0, 0, Width, Height));
        Paper(dc);

        // ---- шапка листа: ПРОТОКОЛ ╱╱╱ ¦ ИГРОВАЯ СЕССИЯ / ОТЧЁТ О ЗАПИСИ ──── ⌖
        double y = M + 4;
        var title = Txt(L.T("ПРОТОКОЛ"), Din, 84, Palette.Ink1, FontWeights.Bold);
        double titleTop = y;
        dc.DrawText(title, new Point(M, titleTop));
        double hy = titleTop + (title.Height - 64) / 2 + 4;
        double x = M + title.Width + 26;
        Hatch(dc, new Rect(x, hy, 104, 64), Palette.Ink3, 13);
        x += 104 + 26;
        dc.DrawLine(Ink.Pen(Palette.LineStrong, 2), new Point(x + 1, hy), new Point(x + 1, hy + 64));
        x += 28;
        var l1 = Txt(Ink.Track("ИГРОВАЯ СЕССИЯ", 2), Din, 21, Palette.Ink2);
        var l2 = Txt(Ink.Track("ОТЧЁТ О ЗАПИСИ", 2), Din, 21, Palette.Ink2);
        dc.DrawText(l1, new Point(x, hy + 2));
        dc.DrawText(l2, new Point(x, hy + 34));
        double lineFrom = x + Math.Max(l1.Width, l2.Width) + 24, cross = 46;
        double cy = hy + 32;
        if (right - cross - 8 > lineFrom + 24)
            dc.DrawLine(Ink.Pen(Palette.LineStrong, 2), new Point(lineFrom, cy), new Point(right - cross - 8, cy));
        Crosshair(dc, new Point(right - cross / 2, cy), cross, Palette.Ink2);
        y = titleTop + title.Height + 36;

        // ---- игра и время
        var game = Txt(string.IsNullOrWhiteSpace(s.Game) ? L.T("Игра") : s.Game, Display, 58, Palette.Ink1, FontWeights.SemiBold, w, 2);
        dc.DrawText(game, new Point(M, y));
        y += game.Height + 10;
        string when = $"{s.Start.ToString(L.En ? "MMMM d, yyyy" : "d MMMM yyyy", L.Culture)}, {s.Start:HH:mm}–{s.End:HH:mm}  ·  {Duration(s.Duration)}";
        var whenText = Txt(when, Text, 28, Palette.Ink3);
        dc.DrawText(whenText, new Point(M, y));
        y += whenText.Height + 32;
        Hairline(dc, y, Palette.LineStrong);
        y += 26;

        // ---- кадры
        if (s.AvgFps > 0)
        {
            double baseY = y + 136;                                    // общая базовая линия чисел
            double heroW = Number(dc, $"{s.AvgFps:0}", "FPS", Din, 176, 46, new Point(M, baseY));
            double col2 = M + Math.Max(w * 0.46, heroW + 64);
            if (s.Low1Fps > 0) Number(dc, $"{s.Low1Fps:0}", "FPS", Din, 96, 30, new Point(col2, baseY));
            var c1 = Txt(L.T("в среднем"), Small, 27, Palette.Ink3);
            dc.DrawText(c1, new Point(M + 4, baseY + 24));
            if (s.Low1Fps > 0)
            {
                var c2 = Txt(L.T("в худшем 1 % кадров"), Small, 27, Palette.Ink3);
                dc.DrawText(c2, new Point(col2 + 2, baseY + 24));
            }
            y = baseY + 24 + c1.Height + 38;
        }
        else
        {
            var none = Txt(L.T("FPS в этой сессии не записывался"), Display, 40, Palette.Ink2, FontWeights.SemiBold, w);
            dc.DrawText(none, new Point(M, y));
            y += none.Height + 10;
            var why = Txt(L.T("Без кадров на листе — то, что записалось: нагрузка и видеопамять."), Text, 26, Palette.Ink3, null, w);
            dc.DrawText(why, new Point(M, y));
            y += why.Height + 40;
            // Три показания в ряд, как в отчёте программы. Температуры здесь нет намеренно.
            var cols = new List<(string Value, string Unit, string Caption)>
            {
                ($"{s.AvgGpu:0}", "%", L.T("видеокарта в среднем")),
                ($"{s.AvgCpu:0}", "%", L.T("процессор в среднем")),
            };
            if (s.MaxVramGb > 0) cols.Add((s.MaxVramGb.ToString("0.0", L.Culture), Fmt.GbUnit, L.T("макс. видеопамять")));
            double colW = w / 3, baseY = y + 70;
            for (int i = 0; i < cols.Count; i++)
            {
                double cx = M + i * colW;
                Number(dc, cols[i].Value, cols[i].Unit, Din, 88, 30, new Point(cx, baseY));
                dc.DrawText(Txt(cols[i].Caption, Small, 25, Palette.Ink3, null, colW - 16), new Point(cx + 2, baseY + 18));
            }
            y = baseY + 18 + 34 + 40;
        }

        // ---- что ограничивало FPS
        if (Bottleneck(s) is { } verdict)
        {
            y = Label(dc, L.T("ЧТО ОГРАНИЧИВАЛО FPS"), y);
            var v = Txt(verdict.Verdict, Text, 38, Palette.Ink1, FontWeights.SemiBold, w, 2);
            dc.DrawText(v, new Point(M, y));
            y += v.Height + 14;
            var bars = BoundBars(s);
            double max = bars.Max(b => b.Pct);
            double labelW = 470, valueW = 120, barX = M + labelW, barW = w - labelW - valueW;
            foreach (var (label, pct) in bars)
            {
                Brush ink = pct == max ? Palette.Accent : Palette.Ink2;
                var lt = Txt(label, Small, 27, Palette.Ink2, null, labelW - 20, 1);
                double rowMid = y + 26;
                dc.DrawText(lt, new Point(M, rowMid - lt.Height / 2));
                dc.DrawLine(Ink.Pen(Palette.LineStrong, 1), new Point(barX, rowMid), new Point(barX + barW, rowMid));
                double fill = Math.Clamp(pct / 100, 0, 1) * barW;
                if (fill > 0) dc.DrawRectangle(ink, null, new Rect(barX, rowMid - 5, Math.Max(2, fill), 10));
                var vt = Txt($"{pct:0}{Nb}%", Mono, 30, ink);
                dc.DrawText(vt, new Point(right - vt.Width, rowMid - vt.Height / 2));
                y += 48;
            }
            y += 30;
        }

        // ---- просадки: одна-две главные причины
        if (s.AvgFps > 0 && s.Dips != null)
        {
            y = Label(dc, s.Dips.Count == 0 ? L.T("ПРОСАДКИ") : L.T("ПРОСАДКИ") + " · " + s.Dips.Count, y);
            if (s.Dips.Count == 0)
            {
                var calm = Txt(L.T("Резких просадок не было: FPS держался ровно."), Text, 30, Palette.Ink1, null, w);
                dc.DrawText(calm, new Point(M, y));
                y += calm.Height + 10;
            }
            else
            {
                var top = s.Dips.GroupBy(d => d.Cause).OrderByDescending(g => g.Count()).ThenBy(g => g.Key == "unknown" ? 1 : 0).Take(2).ToList();
                int no = 1;
                double footerTop = Height - M - 40;
                foreach (var g in top)
                {
                    // Вторая причина — только если помещается над низом листа.
                    if (no > 1 && y + 96 > footerTop) break;
                    var worst = g.OrderBy(d => d.ToFps).First();
                    var num = Txt(no.ToString(), Mono, 32, Palette.Accent);
                    dc.DrawText(num, new Point(M, y + 2));
                    Brush ink = g.Key is "ram" or "gpu-heat" ? Palette.Warn : g.Key == "unknown" ? Palette.Ink3 : Palette.Ink1;
                    var name = Txt(CauseName(g.Key), Text, 34, ink, FontWeights.SemiBold, w - 300, 1);
                    dc.DrawText(name, new Point(M + 52, y));
                    var count = Txt(SessionAnalysis.Times(g.Count()).Replace(" ", Nb), Mono, 28, Palette.Ink1);
                    dc.DrawText(count, new Point(right - count.Width, y + 6));
                    var detail = Txt(L.F("сильнее всего: {0:0} → {1:0} FPS", worst.FromFps, worst.ToFps), Small, 25, Palette.Ink3);
                    dc.DrawText(detail, new Point(M + 52, y + name.Height + 2));
                    y += name.Height + detail.Height + 22;
                    if (no < top.Count && y + 96 <= footerTop) Hairline(dc, y - 12, Palette.LineStrong);
                    no++;
                }
            }
        }

        // ---- низ листа: знак программы, линия, штамп, перекрестие
        Footer(dc, s);
    }

    /// <summary>Бумага в клетку внутри рамки листа: мелкая клетка 30, каждая пятая линия крупнее.</summary>
    private static void Paper(DrawingContext dc)
    {
        var area = new Rect(Frame, Frame, Width - 2 * Frame, Height - 2 * Frame);
        var minor = Ink.Pen(Palette.GridMinor, 1);
        var major = Ink.Pen(Palette.GridMajor, 1);
        dc.PushClip(new RectangleGeometry(area));
        const double step = 30;
        int i = 0;
        for (double x = area.Left; x <= area.Right; x += step, i++)
            dc.DrawLine(i % 5 == 0 ? major : minor, new Point(Math.Round(x) + 0.5, area.Top), new Point(Math.Round(x) + 0.5, area.Bottom));
        i = 0;
        for (double y = area.Top; y <= area.Bottom; y += step, i++)
            dc.DrawLine(i % 5 == 0 ? major : minor, new Point(area.Left, Math.Round(y) + 0.5), new Point(area.Right, Math.Round(y) + 0.5));
        dc.Pop();
        dc.DrawRectangle(null, Ink.Pen(Palette.Ink1, 2), new Rect(Frame + 1, Frame + 1, area.Width - 2, area.Height - 2));
    }

    private static void Footer(DrawingContext dc, GameSession s)
    {
        double right = Width - M, cy = Height - M + 8;
        // Знак: тот же пульс, что у логотипа в заголовке окна, красным пером.
        var logo = Geometry.Parse("M0,8 H5 L7.5,2 L10.5,13 L13,6 L14.5,8 H18").Clone();
        logo.Transform = new MatrixTransform(2.2, 0, 0, 2.2, M, cy - 8 * 2.2);
        var pen = new Pen(Palette.Accent, 3.2) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square, LineJoin = PenLineJoin.Miter };
        dc.DrawGeometry(null, pen, logo);
        var brand = Txt(L.T("Пульс ПК"), Display, 30, Palette.Ink1, FontWeights.SemiBold);
        double bx = M + 18 * 2.2 + 16;
        dc.DrawText(brand, new Point(bx, cy - brand.Height / 2 - 1));

        // Штамп: ячейки в рамке, текст моноширинный с разрядкой.
        double cross = 40, cellH = 40;
        string[] cells = { Ink.Track("ПРОТОКОЛ", 2), s.Start.ToString(L.En ? "MM/dd/yyyy" : "dd.MM.yyyy", CultureInfo.InvariantCulture) };
        var texts = cells.Select(c => Txt(c, Mono, 19, Palette.Ink2)).ToArray();
        double stampW = texts.Sum(t => t.Width + 28);
        double sx = right - cross - 16 - stampW;
        var stampPen = Ink.Pen(Palette.Ink2, 2);
        dc.DrawRectangle(null, stampPen, new Rect(sx, cy - cellH / 2, stampW, cellH));
        double cx = sx;
        for (int i = 0; i < texts.Length; i++)
        {
            dc.DrawText(texts[i], new Point(cx + 14, cy - texts[i].Height / 2));
            cx += texts[i].Width + 28;
            if (i < texts.Length - 1) dc.DrawLine(stampPen, new Point(cx, cy - cellH / 2), new Point(cx, cy + cellH / 2));
        }
        double from = bx + brand.Width + 24, to = sx - 24;
        if (to > from) dc.DrawLine(Ink.Pen(Palette.LineStrong, 2), new Point(from, cy), new Point(to, cy));
        Crosshair(dc, new Point(right - cross / 2, cy), cross, Palette.Ink2);
    }

    // ================================================================ мелочи

    private static FormattedText Txt(string text, FontFamily family, double size, Brush brush, FontWeight? weight = null, double maxWidth = 0, int maxLines = 0)
    {
        var t = new FormattedText(text ?? "", L.Culture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal), size, brush, 1.0);
        if (maxWidth > 0) t.MaxTextWidth = maxWidth;
        if (maxLines > 0) { t.MaxLineCount = maxLines; t.Trimming = TextTrimming.CharacterEllipsis; }
        return t;
    }

    /// <summary>Число и единица на одной базовой линии (единица мельче, как у NumberText в программе).</summary>
    private static double Number(DrawingContext dc, string value, string unit, FontFamily family, double size, double unitSize, Point baseline)
    {
        var n = Txt(value, family, size, Palette.Ink1, FontWeights.SemiBold);
        var u = Txt(unit, family, unitSize, Palette.Ink1, FontWeights.SemiBold);
        dc.DrawText(n, new Point(baseline.X, baseline.Y - n.Baseline));
        dc.DrawText(u, new Point(baseline.X + n.Width + size * 0.08, baseline.Y - u.Baseline));
        return n.Width + size * 0.08 + u.Width;
    }

    /// <summary>Метка группы с разрядкой и тонкая линия под ней. Возвращает высоту для следующей строки.</summary>
    private static double Label(DrawingContext dc, string text, double y)
    {
        var t = Txt(Ink.Track(text, 1), Din, 22, Palette.Ink3, FontWeights.SemiBold);
        dc.DrawText(t, new Point(M, y));
        y += t.Height + 12;
        Hairline(dc, y, Palette.LineStrong);
        return y + 22;
    }

    private static void Hairline(DrawingContext dc, double y, Brush brush) =>
        dc.DrawLine(Ink.Pen(brush, 2), new Point(M, Math.Round(y) + 1), new Point(Width - M, Math.Round(y) + 1));

    private static void Hatch(DrawingContext dc, Rect r, Brush brush, double step)
    {
        var pen = Ink.Pen(brush, 2);
        dc.PushClip(new RectangleGeometry(r));
        for (double x = r.Left - r.Height; x < r.Right; x += step) dc.DrawLine(pen, new Point(x, r.Bottom), new Point(x + r.Height, r.Top));
        dc.Pop();
    }

    private static void Crosshair(DrawingContext dc, Point c, double size, Brush brush)
    {
        var pen = Ink.Pen(brush, 2);
        double r = size * 0.27;
        dc.DrawEllipse(null, pen, c, r, r);
        dc.DrawLine(pen, new Point(c.X, c.Y - size / 2), new Point(c.X, c.Y + size / 2));
        dc.DrawLine(pen, new Point(c.X - size / 2, c.Y), new Point(c.X + size / 2, c.Y));
    }

    /// <summary>«1 ч 34 мин» («1 h 34 min») с неразрывными пробелами перед единицами.</summary>
    private static string Duration(TimeSpan t) => L.En
        ? Fmt.Duration(t).Replace(" h", Nb + "h").Replace(" min", Nb + "min").Replace(" s", Nb + "s")
        : Fmt.Duration(t).Replace(" ч", Nb + "ч").Replace(" мин", Nb + "мин").Replace(" с", Nb + "с");
}
