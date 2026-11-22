using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PulsePC.Services;

namespace PulsePC;

/// <summary>
/// Осторожный режим в отчёте сессии: отметка под заголовком («Осторожный режим: античит … — FPS не записывался») с кнопкой
/// «Записывать полностью» (после подтверждения) или «Включить осторожный режим для этой игры»; предложение режима при ручном
/// добавлении игры. Решение и настройки — <see cref="CautiousMode"/>.
/// </summary>
public partial class MainWindow
{
    private DockPanel cautiousMark;
    private TextBlock cautiousText;
    private Button cautiousButton;
    private bool cautiousHooked;

    private void ShowCautiousMark(GameSession s)
    {
        if (!cautiousHooked)
        {
            cautiousHooked = true;
            // Выбор поменяли в «Установленных программах» — отметка открытого отчёта обновляется.
            CautiousMode.Changed += () => OnUi(() => { if (SessionList.SelectedItem is GameSession cur) ShowCautiousMark(cur); });
        }
        var was = s == null ? null : CautiousMode.ForSession(s);
        var now = s == null ? null : CautiousMode.For(s.Game);
        bool show = was != null && (was.Cautious || was.AntiCheat != null || was.By != "auto" || now.By != "auto" || now.Cautious);
        if (cautiousMark == null)
        {
            if (!show || SessWhen.Parent is not Panel parent) return;
            cautiousMark = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            cautiousButton = new Button { Style = (Style)FindResource("Link"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            cautiousButton.Click += (_, _) => CautiousButton_Click();
            DockPanel.SetDock(cautiousButton, Dock.Right);
            cautiousMark.Children.Add(cautiousButton);
            cautiousText = new TextBlock { Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap };
            cautiousMark.Children.Add(cautiousText);
            int after = behaviorMark != null && parent.Children.Contains(behaviorMark) ? parent.Children.IndexOf(behaviorMark) : parent.Children.IndexOf(SessWhen);
            parent.Children.Insert(after + 1, cautiousMark);
        }
        cautiousMark.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        cautiousMark.Tag = s;
        cautiousText.Text = MarkText(was, now, legacy: s.Cautious == null);
        if (s.FramesFrom is DateTime from && s.Cautious == false)
            cautiousText.Text = (L.F("FPS записан с {0} — с того момента, как вы разрешили полную запись.", from.ToString("HH:mm", L.Culture)) + " " + cautiousText.Text).Trim();
        cautiousText.ToolTip = CautiousMode.Explain;
        cautiousText.SetResourceReference(TextBlock.ForegroundProperty, was.Cautious ? "Ink2" : "Ink3");
        cautiousButton.Content = now.Cautious ? L.T("Записывать полностью") : L.T("Включить осторожный режим для этой игры");
        cautiousButton.Tag = now.Cautious;
    }

    /// <summary>Строка отметки: как была записана сессия и что будет со следующей игры.</summary>
    private static string MarkText(CautiousDecision was, CautiousDecision now, bool legacy)
    {
        var ac = was.AntiCheat ?? now.AntiCheat;
        string text;
        if (was.Cautious)
            text = was.By == "manual" || ac == null
                ? L.T("Осторожный режим (вы включили его для этой игры) — FPS не записывался.")
                : L.F("Осторожный режим: античит {0} — FPS не записывался.", ac.Name);
        else if (was.By == "full")
            text = ac != null ? L.F("Полная запись — вы разрешили её для этой игры (античит {0}).", ac.Describe()) : L.T("Полная запись — вы разрешили её для этой игры.");
        else if (ac?.Kernel == true && !AntiCheat.IsStrict(ac.Name))
            // EasyAntiCheat и BattlEye: полная запись — решение по умолчанию, а не «программа не знала об античите».
            text = L.F("В игре античит {0}; для EasyAntiCheat и BattlEye запись по умолчанию полная.", ac.Describe());
        else if (ac?.Kernel == true)
            text = legacy ? L.F("В игре античит {0}. Эта сессия записана полностью: осторожного режима тогда ещё не было.", ac.Describe())
                          : L.F("В игре античит {0}; программа узнала о нём уже после этой игры, поэтому она записана полностью.", ac.Describe());
        else if (ac != null)
            text = L.F("В игре античит {0}; он работает не на уровне ядра, поэтому запись полная.", ac.Name);
        else text = "";
        if (now.Cautious != was.Cautious || (now.By != was.By && now.By != "auto"))
            text = (text + " " + (now.Cautious ? L.T("Теперь — осторожный режим; если игра идёт, запись изменится через несколько секунд.") : L.T("Теперь — полная запись; если игра идёт, FPS начнёт записываться через несколько секунд."))).Trim();
        return text;
    }

    private void CautiousButton_Click()
    {
        if (cautiousMark?.Tag is not GameSession s) return;
        if (cautiousButton.Tag is true)
        {
            ShowTitlePopup(cautiousButton, CautiousMode.ConfirmFullText(s.Game), new (string, bool, Action)[]
            {
                (L.T("Записывать полностью"), true, () => { CautiousMode.Choose(s.Game, false); ShowCautiousMark(s); }),
                (L.T("Отмена"), false, null),
            });
        }
        else
        {
            CautiousMode.Choose(s.Game, true);
            ShowCautiousMark(s);
        }
    }

    // ================================================================== игра, добавленная вручную
    private WrapPanel cautiousOffer;

    /// <summary>
    /// После ручного добавления игры: в её папке ищется античит (в фоне, только имена файлов). Нашёлся уровня ядра — режим
    /// включается сам, рядом кнопка «Записывать полностью»; иначе — предложение включить осторожный режим вручную.
    /// </summary>
    private void OfferCautiousForAdded(string name, string exe)
    {
        Task.Run(() =>
        {
            AntiCheatInfo found = null;
            try { found = AntiCheat.FromFolder(exe) ?? AntiCheat.FromList(name, exe); } catch (Exception ex) { Log.Error("античит добавленной игры", ex); }
            AntiCheat.Remember(name, found);
            OnUi(() =>
            {
                if (customNote == null) return;
                var d = CautiousMode.For(name);
                if (d.Cautious && d.AntiCheat != null)
                    customNote.Text += " " + (d.AntiCheat.Source == "folder"
                        ? L.F("В папке игры найден античит {0} — для неё включён осторожный режим: FPS и сервер игры записываться не будут.", d.AntiCheat.Describe())
                        // Найден не в папке, а по списку известных игр (или раньше по службе) — так и сказать.
                        : L.F("У этой игры античит {0} ({1}) — для неё включён осторожный режим: FPS и сервер игры записываться не будут.", d.AntiCheat.Describe(), d.AntiCheat.SourceText()));
                cautiousOffer ??= new WrapPanel { Margin = new Thickness(0, 4, 16, 0) };
                cautiousOffer.Children.Clear();
                var b = new Button { Style = (Style)FindResource("Link"), Content = d.Cautious ? L.T("Записывать полностью") : L.T("Включить осторожный режим для этой игры") };
                b.ToolTip = CautiousMode.Explain;
                b.Click += (_, _) =>
                {
                    if (d.Cautious)
                        ShowTitlePopup(b, CautiousMode.ConfirmFullText(name), new (string, bool, Action)[]
                        {
                            (L.T("Записывать полностью"), true, () => { CautiousMode.Choose(name, false); ShowCustomNote(L.F("«{0}»: полная запись — с FPS и сервером игры.", name), warn: false); }),
                            (L.T("Отмена"), false, null),
                        });
                    else
                    {
                        CautiousMode.Choose(name, true);
                        ShowCustomNote(L.F("«{0}»: включён осторожный режим — FPS и сервер игры записываться не будут.", name), warn: false);
                    }
                    if (cautiousOffer.Parent is Panel p) p.Children.Remove(cautiousOffer);
                };
                cautiousOffer.Children.Add(b);
                if (cautiousOffer.Parent is Panel old) old.Children.Remove(cautiousOffer);
                int at = CustomGamesPanel.Children.IndexOf(customNote);
                CustomGamesPanel.Children.Insert(at < 0 ? CustomGamesPanel.Children.Count : at + 1, cautiousOffer);
            });
        });
    }
}
