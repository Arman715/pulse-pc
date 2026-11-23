using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PulsePC.Services;

namespace PulsePC;

/// <summary>
/// «Сообщить о проблеме» и «Оставить отзыв»: описание, снимки экрана (окно программы, весь экран, файл, Ctrl+V),
/// сведения о программе — и письмо разработчику через почту пользователя (см. Services\Feedback).
/// </summary>
public partial class FeedbackWindow : Window
{
    private readonly Feedback.Kind kind;
    private readonly Window appWindow;
    private readonly bool admin;
    private readonly AppSettings settings;
    private readonly List<(string Name, byte[] Png)> images = new();
    private Feedback.Result result;
    private int stars;

    public FeedbackWindow(Feedback.Kind kind, Window appWindow, bool admin, AppSettings settings = null)
    {
        this.settings = settings;
        InitializeComponent();
        WindowFit.Attach(this);
        this.kind = kind;
        this.appWindow = appWindow;
        this.admin = admin;
        Owner = appWindow;
        Motion.Init(this);

        bool review = kind == Feedback.Kind.Review;
        CaptionText.Text = review ? L.T("Отзыв о программе") : L.T("Сообщить о проблеме");
        Title = L.T("Пульс ПК") + " — " + CaptionText.Text;
        Heading.Text = review ? L.T("Как вам Пульс ПК?") : L.T("Что пошло не так?");
        Intro.Text = review
            ? L.T("Оценка и пара слов помогают понять, что делать дальше: что оставить как есть, а что исправить в первую очередь.")
            : L.T("Опишите, что случилось, — письмо придёт разработчику. Чем подробнее, тем быстрее получится найти и исправить причину.");
        ProblemKind.Visibility = review ? Visibility.Collapsed : Visibility.Visible;
        ReviewStars.Visibility = review ? Visibility.Visible : Visibility.Collapsed;
        ReportBlock.Visibility = review ? Visibility.Collapsed : Visibility.Visible;
        ShotsBlock.Visibility = review ? Visibility.Collapsed : Visibility.Visible;
        TextLabel.Source = review ? "ОТЗЫВ" : "ОПИСАНИЕ";
        TextHint.Text = review
            ? L.T("Что понравилось, чего не хватает, что раздражает. Можно коротко.")
            : L.T("Что вы делали, что ожидали увидеть и что произошло. Если была ошибка — её текст.");
        BtnSend.Content = L.T(Feedback.HasAddress ? "Подготовить письмо" : "Сохранить архив");
        Footer.Text = Feedback.HasAddress
            ? L.T("Откроется письмо в вашей почте — отправку подтверждаете вы. Без вашего действия ничего не уходит.")
            : L.T("Адрес разработчика пока не указан в программе: архив сохранится в «Документы», его можно отправить вручную.");

        if (review) BuildStars();
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) =>
        {
            Motion.Reveal(FormPanel, 0, 6);
            TextInput.Focus();
        };
        UpdateSend();
        TextInput.TextChanged += (_, _) => UpdateSend();
    }

    // ================================================================== оценка

    private void BuildStars()
    {
        for (int i = 1; i <= 5; i++)
        {
            int n = i;
            var b = new Button { Style = (Style)FindResource("Star"), Content = "", Tag = n, ToolTip = Fmt.Plural(n, "звезда", "звезды", "звёзд") };
            System.Windows.Automation.AutomationProperties.SetName(b, Fmt.Plural(n, "звезда", "звезды", "звёзд"));
            b.Click += (_, _) => { stars = n; PaintStars(n); UpdateSend(); };
            b.MouseEnter += (_, _) => PaintStars(n);
            b.MouseLeave += (_, _) => PaintStars(stars);
            Stars.Children.Add(b);
        }
        PaintStars(0);
    }

    private void PaintStars(int lit)
    {
        foreach (Button b in Stars.Children)
        {
            bool on = (int)b.Tag <= lit;
            b.Content = on ? "" : "";
            b.Foreground = on ? (Brush)FindResource("Accent") : (Brush)FindResource("Ink4");
        }
        StarsText.Text = lit switch
        {
            1 => L.T("Плохо"),
            2 => L.T("Так себе"),
            3 => L.T("Нормально"),
            4 => L.T("Хорошо"),
            5 => L.T("Отлично"),
            _ => L.T("Нажмите на звезду"),
        };
    }

    private void UpdateSend()
    {
        BtnSend.IsEnabled = kind == Feedback.Kind.Review ? stars > 0 : TextInput.Text.Trim().Length >= 5 || images.Count > 0;
    }

    // ================================================================== снимки

    private void ShotApp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Окно программы рисуется само в картинку: другие программы и рабочий стол на снимок не попадают.
            if (appWindow?.Content is not FrameworkElement root || root.ActualWidth < 10) { Note(L.T("Окно программы сейчас не на экране.")); return; }
            var dpi = VisualTreeHelper.GetDpi(root);
            var bmp = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
                96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bmp.Render(root);
            bmp.Freeze();
            AddImage(L.T("Окно программы"), bmp);
        }
        catch (Exception ex) { Note(L.T("Не удалось снять окно: ") + ex.Message); }
    }

    private async void ShotScreen_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Окно отзыва на снимке не нужно: прячем на время снимка.
            Opacity = 0;
            await Task.Delay(350);
            var area = System.Windows.Forms.SystemInformation.VirtualScreen;
            using var bmp = new System.Drawing.Bitmap(area.Width, area.Height);
            using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(area.Left, area.Top, 0, 0, bmp.Size);
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            AddPng(L.T("Весь экран"), ms.ToArray());
        }
        catch (Exception ex) { Note(L.T("Не удалось снять экран: ") + ex.Message); }
        finally { Opacity = 1; Activate(); }
    }

    private void ShotFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("Снимки экрана"),
            Filter = L.T("Картинки") + " (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        foreach (string file in dialog.FileNames)
        {
            if (images.Count >= Feedback.MaxImages) break;
            try
            {
                if (new FileInfo(file).Length > 15L * 1024 * 1024) { Note(L.F("«{0}» больше 15 МБ — не добавлен.", Path.GetFileName(file))); continue; }
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.UriSource = new Uri(file);
                img.EndInit();
                img.Freeze();
                AddImage(Path.GetFileName(file), img);
            }
            catch { Note(L.F("«{0}» не открывается как картинка.", Path.GetFileName(file))); }
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && kind == Feedback.Kind.Problem && Clipboard.ContainsImage())
        {
            try
            {
                var img = Clipboard.GetImage();
                if (img != null) { AddImage(L.T("Из буфера обмена"), img); e.Handled = true; }
            }
            catch { }
        }
    }

    private void AddImage(string name, BitmapSource image) => AddPng(name, Feedback.Png(image));

    private void AddPng(string name, byte[] png)
    {
        if (images.Count >= Feedback.MaxImages) { Note(L.T("Не больше шести снимков.")); return; }
        images.Add((name, png));
        RenderShots();
        UpdateSend();
    }

    private void RenderShots()
    {
        Shots.Children.Clear();
        foreach (var item in images.ToList())
        {
            var thumb = new BitmapImage();
            thumb.BeginInit();
            thumb.CacheOption = BitmapCacheOption.OnLoad;
            thumb.DecodePixelWidth = 320;
            thumb.StreamSource = new MemoryStream(item.Png);
            thumb.EndInit();
            thumb.Freeze();

            var remove = new Button { Style = (Style)FindResource("Link"), Content = L.T("убрать"), HorizontalAlignment = HorizontalAlignment.Right };
            remove.Click += (_, _) => { images.Remove(item); RenderShots(); UpdateSend(); };
            var caption = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            DockPanel.SetDock(remove, Dock.Right);
            caption.Children.Add(remove);
            caption.Children.Add(new TextBlock { Text = item.Name, Style = (Style)FindResource("Caption"), TextTrimming = TextTrimming.CharacterEllipsis });
            var box = new StackPanel { Width = 196, Margin = new Thickness(0, 0, 12, 12) };
            box.Children.Add(new Border
            {
                Height = 118, BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1), Background = (Brush)FindResource("Raised"),
                Child = new Image { Source = thumb, Stretch = Stretch.Uniform },
            });
            box.Children.Add(caption);
            Shots.Children.Add(box);
        }
    }

    private void Note(string text)
    {
        Footer.Text = text;
        Footer.Foreground = (Brush)FindResource("Warn");
        Motion.Reveal(Footer, 0, 4);
    }

    // ================================================================== сведения о программе

    private void ReportLabel_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParentCheckBox(d) == ChkReport) return;   // щелчок по самому флажку он обработает сам
        ChkReport.IsChecked = ChkReport.IsChecked != true;
    }

    private static CheckBox FindParentCheckBox(DependencyObject d)
    {
        while (d != null && d is not CheckBox) d = VisualTreeHelper.GetParent(d);
        return d as CheckBox;
    }

    private async void ReportPreview_Click(object sender, RoutedEventArgs e)
    {
        if (ReportPreview.Visibility == Visibility.Visible) { ReportPreview.Visibility = Visibility.Collapsed; return; }
        ReportPreview.Visibility = Visibility.Visible;
        ReportPreview.Text = L.T("Собираю…");
        bool adm = admin;
        ReportPreview.Text = await Task.Run(() => Feedback.SafeReport(adm));
    }

    // ================================================================== отправка

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var draft = new Feedback.Draft
        {
            Kind = kind,
            Category = KindError.IsChecked == true ? "Ошибка или сбой" : KindWrong.IsChecked == true ? "Работает не так" : "Неудобно, есть идея",
            Text = TextInput.Text,
            Contact = ContactBox.Text,
            Stars = stars,
            IncludeReport = ChkReport.IsChecked == true,
            Images = images.ToList(),
        };
        BtnSend.IsEnabled = BtnCancel.IsEnabled = false;
        Footer.Foreground = (Brush)FindResource("Ink3");
        Footer.Text = L.T("Готовлю письмо…");
        bool adm = admin;
        result = await Task.Run(() => Feedback.Prepare(draft, adm));
        BtnCancel.IsEnabled = true;
        if (result.Error != null)
        {
            BtnSend.IsEnabled = true;
            Note(L.T("Не получилось подготовить письмо: ") + result.Error);
            return;
        }
        ShowDone(draft);
    }

    private void ShowDone(Feedback.Draft draft)
    {
        FormPanel.Visibility = Visibility.Collapsed;
        DonePanel.Visibility = Visibility.Visible;
        BtnSend.Visibility = Visibility.Collapsed;
        BtnCancel.Content = L.T("Закрыть");
        BtnCancel.Style = (Style)FindResource("Primary");
        Footer.Text = "";
        bool archive = result.Archive != null;
        DoneTitle.Text = Feedback.HasAddress ? L.T("Готово — как отправить?") : L.T("Архив сохранён");
        DoneText.Text = Feedback.HasAddress
            ? (archive
                ? L.F("Быстрее всего — в Telegram: нажмите «Написать в Telegram», вставьте текст (он будет в буфере обмена) и перетащите в чат архив {0} из открывшейся папки. Или нажмите свою почту ниже — откроется новое письмо с адресом и текстом, архив перетащите в него.", Path.GetFileName(result.Archive))
                : L.T("Быстрее всего — в Telegram: нажмите «Написать в Telegram» и вставьте текст — он будет в буфере обмена. Или нажмите свою почту ниже — откроется новое письмо с адресом и текстом."))
            : L.F("Архив {0} лежит в папке «{1}». Отправить его можно в Telegram — кнопкой ниже.", Path.GetFileName(result.Archive ?? ""), Feedback.Folder);
        BuildMailButtons();
        BtnOpenFolder.Visibility = archive ? Visibility.Visible : Visibility.Collapsed;
        BtnCopyAddress.Visibility = Feedback.HasAddress ? Visibility.Visible : Visibility.Collapsed;
        StarAsk.Visibility = kind == Feedback.Kind.Review && draft.Stars >= 4 ? Visibility.Visible : Visibility.Collapsed;
        Motion.Reveal(DonePanel, 0, 6);
        Scroll.ScrollToTop();
    }

    /// <summary>Кнопки почт: последняя выбранная — первой и красной, остальные — обычные.</summary>
    private void BuildMailButtons()
    {
        MailServices.Children.Clear();
        MailServices.Visibility = Feedback.HasAddress ? Visibility.Visible : Visibility.Collapsed;
        if (!Feedback.HasAddress) return;
        var order = Enum.GetValues<Feedback.Service>().ToList();
        if (Enum.TryParse(settings?.Get("feedback.mail") ?? "", out Feedback.Service last)) { order.Remove(last); order.Insert(0, last); }
        foreach (var s in order)
        {
            var b = new Button { Content = Feedback.ServiceName(s), Margin = new Thickness(0, 0, 10, 8) };
            if (s == order[0] && settings?.Get("feedback.mail") is { Length: > 0 }) b.Style = (Style)FindResource("Primary");
            if (s == Feedback.Service.App) b.ToolTip = L.T("Outlook, Thunderbird, «Почта» Windows — если письма вы отправляете из программы на компьютере");
            b.Click += (_, _) =>
            {
                // Текст письма — сразу в буфер обмена: длинный текст в ссылку целиком не помещается (см. Feedback.ComposeUrl).
                bool copied = false;
                try { Clipboard.SetText(result?.Body ?? ""); copied = true; } catch { }
                try { Feedback.Open(s, result); }
                catch (Exception ex)
                {
                    Log.Error("обратная связь: открыть письмо", ex);
                    CopyNote.Text = L.T("Не удалось открыть письмо. Скопируйте адрес и текст кнопками ниже и вставьте их в почту.");
                    Motion.Reveal(CopyNote, 0, 4);
                    return;
                }
                if (settings != null) { settings.Set("feedback.mail", s.ToString()); settings.Save(); }
                CopyNote.Text = copied
                    ? L.F("Открываю: {0}. Текст письма уже в буфере обмена — если в письме он обрезан, выделите текст и вставьте Ctrl+V. Если письмо не появилось — войдите в почту и нажмите ещё раз.", Feedback.ServiceName(s))
                    : L.F("Открываю: {0}. Если письмо не появилось — войдите в почту и нажмите ещё раз или скопируйте адрес и текст кнопками ниже.", Feedback.ServiceName(s));
                Motion.Reveal(CopyNote, 0, 4);
            };
            MailServices.Children.Add(b);
        }
    }

    /// <summary>Telegram: текст — в буфер обмена, папка с архивом — рядом, чат с ботом — в Telegram (или в браузере, если его нет).</summary>
    private void Telegram_Click(object sender, RoutedEventArgs e)
    {
        bool copied = false;
        try { Clipboard.SetText(result?.Body ?? ""); copied = !string.IsNullOrEmpty(result?.Body); } catch { }
        if (result?.Archive != null) Feedback.ShowInFolder(result.Archive);
        try { Feedback.OpenTelegram(); }
        catch (Exception ex)
        {
            Log.Error("обратная связь: открыть Telegram", ex);
            CopyNote.Text = L.T("Не удалось открыть Telegram. Найдите в нём бота @pulsepc_official_bot и отправьте сообщение туда.");
            Motion.Reveal(CopyNote, 0, 4);
            return;
        }
        string archive = result?.Archive != null ? Path.GetFileName(result.Archive) : null;
        CopyNote.Text = (copied ? L.T("Открываю Telegram. Текст уже в буфере обмена — вставьте его в чат с ботом (Ctrl+V).") : L.T("Открываю Telegram."))
            + (archive != null ? " " + L.F("Затем перетащите в чат архив {0} из открывшейся папки.", archive) : "")
            + " " + L.T("Если чат не открылся — найдите в Telegram бота @pulsepc_official_bot.");
        Motion.Reveal(CopyNote, 0, 4);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (result?.Archive != null) Feedback.ShowInFolder(result.Archive);
    }

    private void CopyAddress_Click(object sender, RoutedEventArgs e) => Copy(Feedback.Address, L.T("Адрес скопирован."));

    private void CopyText_Click(object sender, RoutedEventArgs e) =>
        Copy(result == null ? "" : result.Subject + "\n\n" + result.Body, L.T("Тема и текст письма скопированы."));

    private void Copy(string text, string done)
    {
        try { Clipboard.SetText(text); CopyNote.Text = done; }
        catch { CopyNote.Text = L.T("Буфер обмена занят другой программой — попробуйте ещё раз."); }
        Motion.Reveal(CopyNote, 0, 4);
    }

    private void Star_Click(object sender, RoutedEventArgs e) => Feedback.OpenRepo();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Снимок для проверки (WindowShots, цель «feedback:done»): итог с подставным архивом — без файлов, почты и Telegram.</summary>
    internal void ShowDoneForShot()
    {
        result = new Feedback.Result { Archive = Path.Combine(Feedback.Folder, "PulsePC-report-20261123-141401.zip"), Subject = "Пульс ПК", Body = "Пульс ПК 1.6.0" };
        ShowDone(new Feedback.Draft { Kind = kind });
    }
}
