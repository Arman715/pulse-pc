using System.Windows;
using System.Windows.Controls;
using PulsePC.Services;

namespace PulsePC.Panels;

/// <summary>
/// Раздел «Язык / Language»: русский, английский или как в Windows. Новый язык вступает в силу после перезапуска программы —
/// кнопка «Перезапустить» делает это сразу. Тексты раздела всегда на двух языках: его должен понять и тот, кто случайно
/// переключил язык, которого не читает.
/// </summary>
public partial class LanguagePanel : UserControl, ISystemPanel
{
    private PanelHost host;
    private bool loading;

    public LanguagePanel()
    {
        InitializeComponent();
        State.Text = L.En ? "Language: English" : "Язык: русский";
        Intro.Text = "Язык меню, отчётов и уведомлений. Названия игр и файлов не переводятся.\nLanguage of menus, reports and notifications. Game and file names are not translated.";
        ChoiceLabel.Source = "ЯЗЫК · LANGUAGE";
        string win = L.Resolve("") == "en" ? "English" : "Русский";
        AutoTitle.Text = "Как в Windows · Same as Windows";
        AutoCaption.Text = (L.En ? "now: " : "сейчас: ") + win;
    }

    public void Attach(PanelHost host)
    {
        this.host = host;
        loading = true;
        string saved = host.Settings.Get(L.SettingKey);
        OptRu.IsChecked = saved == "ru";
        OptEn.IsChecked = saved == "en";
        OptAuto.IsChecked = saved is not ("ru" or "en");
        loading = false;
        UpdateRestart();
    }

    public void Shown() => UpdateRestart();

    private void Option_Click(object sender, RoutedEventArgs e)
    {
        if (loading || host == null) return;
        string value = OptRu.IsChecked == true ? "ru" : OptEn.IsChecked == true ? "en" : null;
        host.Settings.Set(L.SettingKey, value);
        host.Settings.Save();
        UpdateRestart();
    }

    /// <summary>Выбранный язык отличается от того, на котором программа сейчас работает, — предложить перезапуск.</summary>
    private void UpdateRestart()
    {
        if (host == null) return;
        string chosen = L.Resolve(host.Settings.Get(L.SettingKey));
        bool differs = chosen != L.Code;
        RestartBlock.Visibility = differs ? Visibility.Visible : Visibility.Collapsed;
        if (!differs) return;
        RestartText.Text = chosen == "en"
            ? "English will be used after the program restarts. Recording and settings are kept.\nАнглийский включится после перезапуска программы."
            : "Русский включится после перезапуска программы. Запись и настройки сохранятся.\nRussian will be used after the program restarts.";
        BtnRestart.Content = chosen == "en" ? "Restart now · Перезапустить" : "Перезапустить · Restart now";
        Motion.Reveal(RestartBlock, 0, 4);
    }

    private void Restart_Click(object sender, RoutedEventArgs e) => App.Restart();
}
