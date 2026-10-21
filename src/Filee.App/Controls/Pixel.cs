// The pixel style (ThemeSettings.Pixel): stepped "pixel" corners on cards, buttons and fields, film grain over the
// windows, and digits drawn as a 5×7 dot matrix. Styles/Pixel.axaml switches the first two on through attached
// properties, so no view has to change.

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Filee.App.Controls;

/// <summary>
/// Stepped corners: the control is clipped to a polygon whose corners go in two 1-pixel-wide steps, and an adorner
/// draws the 1px outline along those steps in the control's border colour (the straight sides keep its own border).
/// </summary>
public static class PixelFrame
{
    /// <summary>Size of one step of a corner, in pixels.</summary>
    public const double Step = 3;

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsEnabled", typeof(PixelFrame));

    public static bool GetIsEnabled(Control control) => control.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Control control, bool value) => control.SetValue(IsEnabledProperty, value);

    static PixelFrame()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is true)
            {
                control.AttachedToVisualTree += OnAttached;
                control.DetachedFromVisualTree += OnDetached;
                control.PropertyChanged += OnPropertyChanged;
                if (TopLevel.GetTopLevel(control) is not null)
                    Install(control);
            }
            else
            {
                control.AttachedToVisualTree -= OnAttached;
                control.DetachedFromVisualTree -= OnDetached;
                control.PropertyChanged -= OnPropertyChanged;
                Remove(control);
            }
        });
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control control)
            Install(control);
    }

    private static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control control)
            Remove(control);
    }

    private static void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is not Control control)
            return;
        if (e.Property == Visual.BoundsProperty)
            control.Clip = Notch(control.Bounds.Size);
        else if (e.Property == Border.BorderBrushProperty || e.Property == Border.BorderThicknessProperty
                 || e.Property == TemplatedControl.BorderBrushProperty || e.Property == TemplatedControl.BorderThicknessProperty)
            AdornerLayer.GetAdorner(control)?.InvalidateVisual();
    }

    private static void Install(Control control)
    {
        control.Clip = Notch(control.Bounds.Size);
        if (AdornerLayer.GetAdorner(control) is null)
        {
            var steps = new Steps(control) { IsHitTestVisible = false };
            AdornerLayer.SetAdorner(control, steps);
        }
    }

    private static void Remove(Control control)
    {
        control.Clip = null;
        if (AdornerLayer.GetAdorner(control) is Steps)
            AdornerLayer.SetAdorner(control, null);
    }

    /// <summary>The rectangle of <paramref name="size"/> with two steps cut out of every corner.</summary>
    internal static Geometry? Notch(Size size)
    {
        var (w, h, s) = (size.Width, size.Height, Step);
        if (w < s * 5 || h < s * 5)
            return null;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(0, s * 2), true);
            Line(c, s, s * 2); Line(c, s, s); Line(c, s * 2, s); Line(c, s * 2, 0);
            Line(c, w - s * 2, 0); Line(c, w - s * 2, s); Line(c, w - s, s); Line(c, w - s, s * 2); Line(c, w, s * 2);
            Line(c, w, h - s * 2); Line(c, w - s, h - s * 2); Line(c, w - s, h - s); Line(c, w - s * 2, h - s); Line(c, w - s * 2, h);
            Line(c, s * 2, h); Line(c, s * 2, h - s); Line(c, s, h - s); Line(c, s, h - s * 2); Line(c, 0, h - s * 2);
            c.EndFigure(true);
        }
        return g;

        static void Line(StreamGeometryContext c, double x, double y) => c.LineTo(new Point(x, y));
    }

    /// <summary>Draws the outline along the steps the clip leaves where the control's own border was cut away.</summary>
    private sealed class Steps : Control
    {
        private readonly Control _owner;

        public Steps(Control owner) => _owner = owner;

        public override void Render(DrawingContext context)
        {
            var brush = _owner switch
            {
                Border b when b.BorderThickness.Left > 0 => b.BorderBrush,
                TemplatedControl t when t.BorderThickness.Left > 0 => t.BorderBrush,
                _ => null,
            };
            var (w, h, s) = (Bounds.Width, Bounds.Height, Step);
            if (brush is null || w < s * 5 || h < s * 5)
                return;
            // Every corner: a 1px square run along its two steps (outer edge to the first step, then to the second).
            foreach (var (x, y, dx, dy) in new[] { (0.0, 0.0, 1, 1), (w - 1, 0.0, -1, 1), (0.0, h - 1, 1, -1), (w - 1, h - 1, -1, -1) })
            {
                Px(x, y + dy * s * 2, s + 1, 1, dx, dy);   // the bottom of the outer step
                Px(x + dx * s, y + dy * s, 1, s + 1, dx, dy); // its riser
                Px(x + dx * s, y + dy * s, s + 1, 1, dx, dy); // the top step
                Px(x + dx * s * 2, y, 1, s + 1, dx, dy);   // its riser up to the edge
            }

            void Px(double px, double py, double width, double height, int dx, int dy) =>
                context.FillRectangle(brush, new Rect(dx > 0 ? px : px - width + 1, dy > 0 ? py : py - height + 1, width, height));
        }
    }
}

/// <summary>Film grain and a few specks of dust over a window, barely visible (the pixel style).</summary>
public static class PixelGrain
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Window, bool>("IsEnabled", typeof(PixelGrain));

    public static bool GetIsEnabled(Window window) => window.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Window window, bool value) => window.SetValue(IsEnabledProperty, value);

    private static Bitmap? _noise;

    static PixelGrain()
    {
        IsEnabledProperty.Changed.AddClassHandler<Window>((window, e) =>
        {
            if (e.NewValue is true)
            {
                window.Opened += OnOpened;
                if (window.IsVisible)
                    Install(window);
            }
            else
            {
                window.Opened -= OnOpened;
                if (window.Content is Control content && AdornerLayer.GetAdorner(content) is Grain)
                    AdornerLayer.SetAdorner(content, null);
            }
        });
    }

    private static void OnOpened(object? sender, EventArgs e)
    {
        if (sender is Window window)
            Install(window);
    }

    private static void Install(Window window)
    {
        // A see-through window (the donut, the toast) would show the grain as a rectangle around what it draws.
        if (window.Background is not ISolidColorBrush { Color.A: 255 })
            return;
        if (window.Content is Control content && AdornerLayer.GetAdorner(content) is null)
            AdornerLayer.SetAdorner(content, new Grain { IsHitTestVisible = false });
    }

    /// <summary>A 160 px tile of grey noise whose alpha varies, the same every time.</summary>
    internal static Bitmap Noise()
    {
        if (_noise is not null)
            return _noise;
        const int size = 160;
        var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        var random = new Random(1608);
        using (var buffer = bitmap.Lock())
        {
            var pixels = new byte[size * size * 4];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var alpha = (byte)random.Next(0, 256);
                var value = (byte)(random.Next(2) == 0 ? 0 : alpha); // light or dark grain, premultiplied
                pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
                pixels[i + 3] = alpha;
            }
            for (var row = 0; row < size; row++)
                System.Runtime.InteropServices.Marshal.Copy(pixels, row * size * 4, buffer.Address + row * buffer.RowBytes, size * 4);
        }
        return _noise = bitmap;
    }

    private sealed class Grain : Control
    {
        private static readonly IBrush Dust = new SolidColorBrush(Color.Parse("#2C2C33"));
        private static readonly IBrush LavenderDust = new SolidColorBrush(Color.Parse("#3B3550"));

        public override void Render(DrawingContext context)
        {
            var bounds = new Rect(Bounds.Size);
            // Dust: the same specks on every redraw, scattered by the window's size.
            var random = new Random(42);
            for (var i = 0; i < 26; i++)
            {
                var x = Math.Floor(random.NextDouble() * bounds.Width);
                var y = Math.Floor(random.NextDouble() * bounds.Height);
                context.FillRectangle(i % 3 == 0 ? LavenderDust : Dust, new Rect(x, y, 2, 2));
            }
            var noise = new ImageBrush(Noise())
            {
                TileMode = TileMode.Tile,
                Stretch = Stretch.None,
                DestinationRect = new RelativeRect(0, 0, 160, 160, RelativeUnit.Absolute),
                Opacity = 0.07,
            };
            context.FillRectangle(noise, bounds);
        }
    }
}

/// <summary>Digits (and "-", "%", "/", ".") drawn as a 5×7 matrix of square dots; dots that are off stay faintly visible.</summary>
public sealed class DotMatrix : Control
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<DotMatrix, string?>(nameof(Text));
    public static readonly StyledProperty<double> DotProperty = AvaloniaProperty.Register<DotMatrix, double>(nameof(Dot), 3);
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextBlock.ForegroundProperty.AddOwner<DotMatrix>();

    private static readonly IBrush Off = new SolidColorBrush(Color.FromArgb(14, 255, 255, 255));

    private static readonly Dictionary<char, string[]> Font = new()
    {
        ['0'] = ["01110", "10001", "10011", "10101", "11001", "10001", "01110"],
        ['1'] = ["00100", "01100", "00100", "00100", "00100", "00100", "01110"],
        ['2'] = ["01110", "10001", "00001", "00010", "00100", "01000", "11111"],
        ['3'] = ["11111", "00010", "00100", "00010", "00001", "10001", "01110"],
        ['4'] = ["00010", "00110", "01010", "10010", "11111", "00010", "00010"],
        ['5'] = ["11111", "10000", "11110", "00001", "00001", "10001", "01110"],
        ['6'] = ["00110", "01000", "10000", "11110", "10001", "10001", "01110"],
        ['7'] = ["11111", "00001", "00010", "00100", "01000", "01000", "01000"],
        ['8'] = ["01110", "10001", "10001", "01110", "10001", "10001", "01110"],
        ['9'] = ["01110", "10001", "10001", "01111", "00001", "00010", "01100"],
        ['-'] = ["00000", "00000", "00000", "11111", "00000", "00000", "00000"],
        ['%'] = ["11000", "11001", "00010", "00100", "01000", "10011", "00011"],
        ['/'] = ["00000", "00001", "00010", "00100", "01000", "10000", "00000"],
        ['.'] = ["00000", "00000", "00000", "00000", "00000", "01100", "01100"],
        [' '] = ["00000", "00000", "00000", "00000", "00000", "00000", "00000"],
    };

    static DotMatrix() => AffectsRender<DotMatrix>(TextProperty, DotProperty, ForegroundProperty);

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

    /// <summary>Size of one dot; the gap between dots is a third of it (at least 1 px).</summary>
    public double Dot { get => GetValue(DotProperty); set => SetValue(DotProperty, value); }

    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    private double Gap => Math.Max(1, Math.Round(Dot / 3));
    private double GlyphWidth => 5 * Dot + 4 * Gap;

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = Text?.Length ?? 0;
        return count == 0 ? default : new Size(count * GlyphWidth + (count - 1) * (Dot + Gap), 7 * Dot + 6 * Gap);
    }

    public override void Render(DrawingContext context)
    {
        var on = Foreground ?? Brushes.White;
        var x = 0.0;
        foreach (var ch in Text ?? "")
        {
            var rows = Font.TryGetValue(ch, out var glyph) ? glyph : Font['-'];
            for (var r = 0; r < 7; r++)
                for (var c = 0; c < 5; c++)
                    context.FillRectangle(rows[r][c] == '1' ? on : Off,
                        new Rect(x + c * (Dot + Gap), r * (Dot + Gap), Dot, Dot));
            x += GlyphWidth + Dot + Gap;
        }
    }
}

/// <summary>Whether the pixel style is on, for bindings (ThemeService sets it).</summary>
public sealed class PixelState : INotifyPropertyChanged
{
    public static PixelState Instance { get; } = new();

    private bool _isOn;

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value)
                return;
            _isOn = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOn)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
