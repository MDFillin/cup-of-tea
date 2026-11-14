using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace PVpn.App;

/// <summary>
/// Редкий тихий снег за содержимым главной (зимнее оформление). Рисуется сам, без элементов на каждую снежинку;
/// идёт, только пока виден, и стоит на месте, если в Windows выключена анимация.
/// </summary>
public sealed class Snowfall : FrameworkElement
{
    private const int MaxFlakes = 60;
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(40);
    private static readonly Brush[] Shades = [Frozen(0x2E), Frozen(0x4A), Frozen(0x6E), Frozen(0x96)];

    private readonly DispatcherTimer timer = new(DispatcherPriority.Render) { Interval = Frame };
    private readonly Random random = new();
    private Flake[] flakes = [];
    private long lastTick;

    private struct Flake
    {
        public double X, Y, Radius, Speed, Drift, Phase, PhaseSpeed;
        public int Shade;
    }

    /// <summary>Снежок: снежинки разлетаются из точки и падают.</summary>
    private struct Puff
    {
        public double X, Y, Vx, Vy, Radius, Life;
    }

    private readonly List<Puff> puffs = [];

    /// <summary>Пасхалка: горсть снега из точки (щелчок по пустому месту главной, секретный код).</summary>
    public void Burst(Point at, int count = 26)
    {
        for (var i = 0; i < count; i++)
        {
            var angle = random.NextDouble() * Math.PI * 2;
            var speed = 40 + random.NextDouble() * 140;
            puffs.Add(new Puff
            {
                X = at.X, Y = at.Y, Vx = Math.Cos(angle) * speed, Vy = Math.Sin(angle) * speed - 60,
                Radius = 1.2 + random.NextDouble() * 2.2, Life = 1.4 + random.NextDouble(),
            });
        }
        if (!Motion.Enabled)
        {
            puffs.Clear();
            return;
        }
        lastTick = Environment.TickCount64;
        timer.Start();
    }

    public Snowfall()
    {
        IsHitTestVisible = false;
        Focusable = false;
        timer.Tick += (_, _) => Step();
        IsVisibleChanged += (_, _) => UpdateRunning();
        Unloaded += (_, _) => timer.Stop();
        Loaded += (_, _) => UpdateRunning();
    }

    private void UpdateRunning()
    {
        if (IsVisible && IsLoaded && Motion.Enabled)
        {
            lastTick = Environment.TickCount64;
            timer.Start();
        }
        else
            timer.Stop();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 1 || h < 1)
            return;
        var count = (int)Math.Clamp(w * h / 14000, 12, MaxFlakes);
        if (flakes.Length == count)
            return;
        flakes = new Flake[count];
        for (var i = 0; i < count; i++)
            flakes[i] = NewFlake(w, random.NextDouble() * h);
        InvalidateVisual();
    }

    private Flake NewFlake(double width, double y)
    {
        // Мелкие — дальние: медленнее и прозрачнее.
        var depth = random.NextDouble();
        return new Flake
        {
            X = random.NextDouble() * width,
            Y = y,
            Radius = 0.8 + depth * 1.9,
            Speed = 10 + depth * 26,
            Drift = 4 + random.NextDouble() * 12,
            Phase = random.NextDouble() * Math.PI * 2,
            PhaseSpeed = 0.3 + random.NextDouble() * 0.7,
            Shade = Math.Min(Shades.Length - 1, (int)(depth * Shades.Length)),
        };
    }

    private void Step()
    {
        var now = Environment.TickCount64;
        // После паузы (окно в фоне) снег не прыгает.
        var dt = Math.Min(0.1, (now - lastTick) / 1000.0);
        lastTick = now;
        var w = ActualWidth;
        var h = ActualHeight;
        for (var i = 0; i < flakes.Length; i++)
        {
            ref var f = ref flakes[i];
            f.Y += f.Speed * dt;
            f.Phase += f.PhaseSpeed * dt;
            if (f.Y - f.Radius > h)
                f = NewFlake(w, -f.Radius - random.NextDouble() * 20);
        }
        for (var i = puffs.Count - 1; i >= 0; i--)
        {
            var p = puffs[i];
            p.Vy += 160 * dt;
            p.Vx *= 1 - 1.5 * dt;
            p.X += p.Vx * dt;
            p.Y += p.Vy * dt;
            p.Life -= dt;
            if (p.Life <= 0 || p.Y > h)
                puffs.RemoveAt(i);
            else
                puffs[i] = p;
        }
        if (!IsVisible && puffs.Count == 0)
            timer.Stop();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        foreach (var f in flakes)
            dc.DrawEllipse(Shades[f.Shade], null, new Point(f.X + Math.Sin(f.Phase) * f.Drift, f.Y), f.Radius, f.Radius);
        foreach (var p in puffs)
            dc.DrawEllipse(Shades[p.Life > 0.6 ? 3 : 2], null, new Point(p.X, p.Y), p.Radius, p.Radius);
    }

    private static Brush Frozen(byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, 0xE6, 0xF4, 0xFF));
        brush.Freeze();
        return brush;
    }
}
