using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PVpn.App;

/// <summary>
/// Небольшие анимации интерфейса: нажатие, сворачивание групп, появление страниц и смена состояния.
/// Всё короткое и только по событию — бесконечных анимаций нет (подключённое окно не грузит процессор).
/// Если в Windows выключены «Эффекты анимации», всё меняется сразу.
/// </summary>
public static class Motion
{
    private static readonly IEasingFunction Ease = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });

    /// <summary>Анимации разрешены в Windows («Параметры → Специальные возможности → Эффекты анимации»).</summary>
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    // ───────────── Нажатие: элемент чуть «утапливается» ─────────────

    /// <summary>Масштаб при нажатии (0,95 — на 5 % меньше); 0 — без анимации.</summary>
    public static readonly DependencyProperty PressScaleProperty = DependencyProperty.RegisterAttached(
        "PressScale", typeof(double), typeof(Motion), new PropertyMetadata(0.0, OnPressScaleChanged));

    public static double GetPressScale(DependencyObject d) => (double)d.GetValue(PressScaleProperty);
    public static void SetPressScale(DependencyObject d, double value) => d.SetValue(PressScaleProperty, value);

    private static void OnPressScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
            return;
        element.PreviewMouseLeftButtonDown -= OnPressDown;
        element.PreviewMouseLeftButtonUp -= OnPressUp;
        element.MouseLeave -= OnPressUp;
        element.LostMouseCapture -= OnPressUp;
        if (e.NewValue is not (double and > 0))
            return;
        element.PreviewMouseLeftButtonDown += OnPressDown;
        element.PreviewMouseLeftButtonUp += OnPressUp;
        element.MouseLeave += OnPressUp;
        element.LostMouseCapture += OnPressUp;
    }

    private static void OnPressDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is UIElement { IsEnabled: true } element && Enabled)
            AnimateScale(element, GetPressScale(element), 90, null);
    }

    private static void OnPressUp(object sender, EventArgs e)
    {
        if (sender is UIElement element && element.RenderTransform is ScaleTransform { ScaleX: < 1 })
            AnimateScale(element, 1, 220, Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 }));
    }

    private static void AnimateScale(UIElement element, double to, int ms, IEasingFunction? ease)
    {
        if (element.RenderTransform is not ScaleTransform scale || scale.IsFrozen)
        {
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = scale = new ScaleTransform();
        }
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease ?? Ease };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    // ───────────── Сворачивание: высота плавно уходит в ноль ─────────────

    /// <summary>Содержимое развёрнуто. Первое значение ставится сразу, дальнейшие — с анимацией.</summary>
    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.RegisterAttached(
        "IsOpen", typeof(bool), typeof(Motion), new PropertyMetadata(true, OnIsOpenChanged));

    public static bool GetIsOpen(DependencyObject d) => (bool)d.GetValue(IsOpenProperty);
    public static void SetIsOpen(DependencyObject d, bool value) => d.SetValue(IsOpenProperty, value);

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;
        var open = (bool)e.NewValue;
        if (element.LayoutTransform is not ScaleTransform scale || scale.IsFrozen)
            element.LayoutTransform = scale = new ScaleTransform();

        if (!element.IsLoaded || !Enabled)
        {
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            element.BeginAnimation(UIElement.OpacityProperty, null);
            scale.ScaleY = open ? 1 : 0;
            element.Opacity = open ? 1 : 0;
            element.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(open ? 240 : 180);
        var height = new DoubleAnimation(open ? 1 : 0, duration) { EasingFunction = Ease };
        // Свёрнутое содержимое убирается совсем: по нему не ходит клавиатура и не идёт поиск элемента.
        if (open)
            element.Visibility = Visibility.Visible;
        else
            height.Completed += (_, _) =>
            {
                if (!GetIsOpen(element))
                    element.Visibility = Visibility.Collapsed;
            };
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, height);
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(open ? 1 : 0, duration) { EasingFunction = Ease });
    }

    /// <summary>Стрелка группы: повёрнута на 90° (вниз), когда группа развёрнута. Первое значение — сразу.</summary>
    public static readonly DependencyProperty TurnedProperty = DependencyProperty.RegisterAttached(
        "Turned", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnTurnedChanged));

    public static bool GetTurned(DependencyObject d) => (bool)d.GetValue(TurnedProperty);
    public static void SetTurned(DependencyObject d, bool value) => d.SetValue(TurnedProperty, value);

    private static void OnTurnedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;
        if (element.RenderTransform is not RotateTransform rotate || rotate.IsFrozen)
        {
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = rotate = new RotateTransform();
        }
        var angle = e.NewValue is true ? 90 : 0;
        if (!element.IsLoaded || !Enabled)
        {
            rotate.BeginAnimation(RotateTransform.AngleProperty, null);
            rotate.Angle = angle;
            return;
        }
        rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(angle, TimeSpan.FromMilliseconds(200)) { EasingFunction = Ease });
    }

    // ───────────── Появление: страница или блок выплывает снизу ─────────────

    /// <summary>При каждом показе (Visibility → Visible) элемент проявляется и чуть поднимается.</summary>
    public static readonly DependencyProperty FadeInOnShowProperty = DependencyProperty.RegisterAttached(
        "FadeInOnShow", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnFadeInOnShowChanged));

    public static bool GetFadeInOnShow(DependencyObject d) => (bool)d.GetValue(FadeInOnShowProperty);
    public static void SetFadeInOnShow(DependencyObject d, bool value) => d.SetValue(FadeInOnShowProperty, value);

    private static void OnFadeInOnShowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
            return;
        element.IsVisibleChanged -= OnShown;
        if (e.NewValue is true)
            element.IsVisibleChanged += OnShown;
    }

    private static void OnShown(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is UIElement element)
            Appear(element, 10, 0, 220);
    }

    /// <summary>При смене значения (например, состояния подключения) элемент заново проявляется.</summary>
    public static readonly DependencyProperty FadeOnChangeProperty = DependencyProperty.RegisterAttached(
        "FadeOnChange", typeof(object), typeof(Motion), new PropertyMetadata(null, OnFadeOnChangeChanged));

    public static object? GetFadeOnChange(DependencyObject d) => d.GetValue(FadeOnChangeProperty);
    public static void SetFadeOnChange(DependencyObject d, object? value) => d.SetValue(FadeOnChangeProperty, value);

    private static void OnFadeOnChangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UIElement { IsVisible: true } element && e.OldValue != null)
            Appear(element, 6, 0.35, 260);
    }

    private static void Appear(UIElement element, double offset, double fromOpacity, int ms)
    {
        if (!Enabled)
            return;
        if (element.RenderTransform is not TranslateTransform move || move.IsFrozen)
            element.RenderTransform = move = new TranslateTransform();
        var duration = TimeSpan.FromMilliseconds(ms);
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(fromOpacity, 1, duration) { EasingFunction = Ease });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offset, 0, duration) { EasingFunction = Ease });
    }

    private static IEasingFunction Freeze(EasingFunctionBase ease)
    {
        ease.Freeze();
        return ease;
    }
}
