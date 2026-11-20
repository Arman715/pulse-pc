using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PulsePC;

/// <summary>
/// Вся анимация программы. Характер: прибор, а не шоу — страница неподвижна, один раз сдвигается,
/// когда меняются данные или состояние, и снова замирает.
/// Анимация выключается, если нет аппаратной отрисовки, если окно не активно (в фокусе игра)
/// или идёт удалённый сеанс: тогда значения просто подставляются.
/// </summary>
public static class Motion
{
    public const int Fast = 120;     // уход, наведение
    public const int Normal = 220;   // появление, смена текста, выбор
    public const int Data = 400;     // данные
    public const int Stagger = 30;

    public static readonly IEasingFunction Out = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction In = Frozen(new QuadraticEase { EasingMode = EasingMode.EaseIn });
    public static readonly IEasingFunction InOut = Frozen(new SineEase { EasingMode = EasingMode.EaseInOut });

    private static bool systemAllows = true;
    private static bool windowActive = true;

    // Для проверки анимации из скрипта, когда окно не получает фокус: PULSEPC_FORCE_MOTION=1.
    private static readonly bool forced = Environment.GetEnvironmentVariable("PULSEPC_FORCE_MOTION") == "1";

    public static bool On => forced || (systemAllows && windowActive);

    /// <summary>Вызывается, когда меняется возможность анимировать (активность окна, настройки Windows).</summary>
    public static event Action Changed;

    private static bool systemHooked;

    public static void Init(Window window)
    {
        Evaluate();
        // Init зовёт каждое окно (осмотр, отзыв — при каждом открытии): подписка на настройки Windows — одна на программу,
        // иначе обработчики копились бы и Changed срабатывал бы столько раз, сколько окон открывалось.
        if (!systemHooked)
        {
            systemHooked = true;
            SystemParameters.StaticPropertyChanged += (_, _) => { Evaluate(); Changed?.Invoke(); };
        }
        window.Activated += (_, _) => { windowActive = true; Changed?.Invoke(); };
        window.Deactivated += (_, _) => { windowActive = false; Changed?.Invoke(); };
    }

    private static void Evaluate()
    {
        // Настройку Windows «Эффекты анимации» намеренно не учитываем: на этом ПК она выключена ради отзывчивости системы,
        // а анимация чертежей — часть самой программы. Выключаем только без аппаратной отрисовки и в удалённом сеансе.
        systemAllows = (RenderCapability.Tier >> 16) > 0
                       && !SystemParameters.IsRemoteSession;
    }

    private static T Frozen<T>(T easing) where T : Freezable { easing.Freeze(); return easing; }

    /// <summary>
    /// Плавно привести числовое свойство к значению. Базовое значение сразу становится конечным,
    /// а анимация идёт от текущего видимого: новую можно запускать поверх незаконченной.
    /// </summary>
    public static void To(DependencyObject target, DependencyProperty property, double to, int ms = Data,
        IEasingFunction ease = null, int fps = 60)
    {
        if (target is not IAnimatable animatable) { target.SetValue(property, to); return; }

        double current = (double)target.GetValue(property);
        if (!On || double.IsNaN(current) || double.IsNaN(to) || Math.Abs(current - to) < 1e-6)
        {
            animatable.BeginAnimation(property, null);
            target.SetValue(property, to);
            return;
        }

        var animation = new DoubleAnimation(current, to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = ease ?? Out,
            FillBehavior = FillBehavior.Stop,
        };
        Timeline.SetDesiredFrameRate(animation, fps);
        // Сначала анимация, потом базовое значение: так между ними не проскакивает кадр с конечным значением.
        animatable.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        target.SetValue(property, to);
    }

    /// <summary>Подставить значение без анимации.</summary>
    public static void Snap(DependencyObject target, DependencyProperty property, double value)
    {
        (target as IAnimatable)?.BeginAnimation(property, null);
        target.SetValue(property, value);
    }

    private static TranslateTransform Shift(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform t && !t.IsFrozen) return t;
        t = new TranslateTransform();
        element.RenderTransform = t;
        return t;
    }

    /// <summary>Появление: проявиться и подняться на место. Используется для строк результатов и страниц.</summary>
    public static void Reveal(UIElement element, int delayMs = 0, double fromY = 8, int fadeMs = Normal, int moveMs = Normal)
    {
        var shift = Shift(element);
        if (!On)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            shift.BeginAnimation(TranslateTransform.YProperty, null);
            element.Opacity = 1; shift.Y = 0;
            return;
        }

        var delay = TimeSpan.FromMilliseconds(delayMs);
        element.Opacity = 1; shift.Y = 0;
        // Во время задержки элемент должен оставаться невидимым, поэтому анимация держит начальное значение.
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(fadeMs)) { BeginTime = delay, FillBehavior = FillBehavior.Stop };
        var move = new DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(moveMs)) { BeginTime = delay, EasingFunction = Out, FillBehavior = FillBehavior.Stop };
        if (delayMs > 0)
        {
            element.Opacity = 0;
            fade.Completed += (_, _) => { element.Opacity = 1; };
        }
        element.BeginAnimation(UIElement.OpacityProperty, fade);
        shift.BeginAnimation(TranslateTransform.YProperty, move);
    }

    /// <summary>Смена текста: новое сообщение проявляется и чуть поднимается, чтобы его заметили.</summary>
    public static void SetText(TextBlock block, string text)
    {
        if (block.Text == text) return;
        block.Text = text;
        if (string.IsNullOrEmpty(text) || !block.IsVisible) return;
        Reveal(block, 0, 4);
    }

    /// <summary>Короткая вспышка-подсветка строки, которая изменилась (накладка поверх строки).</summary>
    public static void Flash(UIElement overlay, int delayMs = 0)
    {
        if (!On) { overlay.Opacity = 0; return; }
        var frames = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(delayMs), FillBehavior = FillBehavior.Stop };
        frames.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        frames.KeyFrames.Add(new LinearDoubleKeyFrame(0.16, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80))));
        frames.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600))));
        overlay.BeginAnimation(UIElement.OpacityProperty, frames);
    }
}
