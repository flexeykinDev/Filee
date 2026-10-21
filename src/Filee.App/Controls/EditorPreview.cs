// The editor's preview: the clip's frame (its shape, as large as fits), the picture in it placed as the conversion will
// place it (fitted with bars, cropped, or over a blurred copy of itself; closer and moved by the part's zoom and focus),
// and the texts drawn over it in their styles. Texts are dragged where they go; with a part selected whose picture is
// larger than the frame, the picture is dragged to choose what is shown. A double click fills the window.

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Filee.Core.Presets;

namespace Filee.App.Controls;

public sealed class EditorPreview : Panel
{
    public static readonly StyledProperty<IEditorPreviewHost?> HostProperty =
        AvaloniaProperty.Register<EditorPreview, IEditorPreviewHost?>(nameof(Host));

    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<EditorPreview, IBrush?>(nameof(AccentBrush), Brushes.MediumPurple);

    /// <summary>Shown when the picture can be dragged ("Drag the picture to frame it").</summary>
    public static readonly StyledProperty<string?> MoveHintProperty =
        AvaloniaProperty.Register<EditorPreview, string?>(nameof(MoveHint));

    public IEditorPreviewHost? Host { get => GetValue(HostProperty); set => SetValue(HostProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public string? MoveHint { get => GetValue(MoveHintProperty); set => SetValue(MoveHintProperty, value); }

    /// <summary>Room left around the frame.</summary>
    private const double Room = 10;

    private static readonly Typeface Bold = new(new FontFamily("Segoe UI, Inter, Arial, sans-serif"), FontStyle.Normal, FontWeight.Bold);

    private readonly Overlay _overlay;
    private IEditorPreviewHost? _hooked;

    // A tiny copy of the picture, drawn large and smooth: the blurred fill behind a picture of another shape.
    private Avalonia.Media.Imaging.Bitmap? _blurOf;
    private Avalonia.Media.Imaging.Bitmap? _blurred;

    public EditorPreview()
    {
        _overlay = new Overlay(this);
        Children.Add(_overlay);
        ClipToBounds = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HostProperty)
            Hook(Host);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Hook(Host);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Hook(null);
    }

    private void Hook(IEditorPreviewHost? host)
    {
        if (ReferenceEquals(_hooked, host))
            return;
        if (_hooked is not null)
            _hooked.PreviewChanged -= OnHostChanged;
        _hooked = host;
        if (host is not null)
            host.PreviewChanged += OnHostChanged;
        OnHostChanged();
    }

    private void OnHostChanged() => _overlay.InvalidateVisual();

    /// <summary>The blurred copy of <paramref name="picture"/> (made once per picture).</summary>
    private Avalonia.Media.Imaging.Bitmap? Blurred(Avalonia.Media.Imaging.Bitmap picture)
    {
        if (ReferenceEquals(_blurOf, picture) && _blurred is not null)
            return _blurred;
        _blurred?.Dispose();
        _blurred = null;
        _blurOf = picture;
        try
        {
            var size = picture.PixelSize;
            _blurred = picture.CreateScaledBitmap(new PixelSize(Math.Max(2, size.Width / 24), Math.Max(2, size.Height / 24)),
                Avalonia.Media.Imaging.BitmapInterpolationMode.MediumQuality);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A picture that can't be scaled (disposed meanwhile): no blurred fill this time.
            System.Diagnostics.Debug.WriteLine(ex);
        }
        return _blurred;
    }

    /// <summary>The frame, black, with the picture in it as it will be written (or the clip playing).</summary>
    private void DrawPicture(DrawingContext context, Rect frame)
    {
        context.FillRectangle(Brushes.Black, frame);
        if (Host is not { } host || host.Picture is not { } picture)
            return;
        using var clip = context.PushClip(frame);
        if (Placement() is not { } place)
        {
            context.DrawImage(picture, frame);
            return;
        }
        var (width, _) = host.Canvas;
        var scale = width > 0 ? frame.Width / width : 1;
        if (host.Fit == ClipFit.Blur && Blurred(picture) is { } soft)
        {
            var cover = ClipCanvas.Place(place.Width, place.Height, host.Canvas.Width, host.Canvas.Height, ClipFit.Crop);
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality }))
                context.DrawImage(soft, new Rect(frame.X + cover.X * scale, frame.Y + cover.Y * scale, cover.Width * scale, cover.Height * scale));
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), frame);
        }
        context.DrawImage(picture, new Rect(frame.X + place.X * scale, frame.Y + place.Y * scale, place.Width * scale, place.Height * scale));
    }

    /// <summary>The frame on screen: the clip's shape, as large as fits.</summary>
    internal Rect Frame
    {
        get
        {
            var (width, height) = Host?.Canvas ?? (16, 9);
            var room = Bounds.Size.Deflate(new Thickness(Room));
            if (room.Width <= 1 || room.Height <= 1 || width <= 0 || height <= 0)
                return default;
            var scale = Math.Min(room.Width / width, room.Height / height);
            var size = new Size(width * scale, height * scale);
            return new Rect((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2, size.Width, size.Height);
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _overlay.Arrange(new Rect(finalSize));
        return finalSize;
    }

    /// <summary>Where the picture goes in the frame, in the frame's own pixels (null: it fills the frame).</summary>
    private ClipPlacement? Placement()
    {
        if (Host is not { IsComposed: false, PicturePart: { Blank: false } part } host)
            return null;
        var (width, height) = host.Canvas;
        var (pictureWidth, pictureHeight) = host.PictureSize;
        return ClipCanvas.Place(pictureWidth, pictureHeight, width, height, host.Fit, part.PlayZoom, part.FocusX, part.FocusY);
    }

    /// <summary>The texts, the selection's outline and the hint, and the pointer.</summary>
    private sealed class Overlay : Control
    {
        private readonly EditorPreview _owner;
        private int _dragText = -1;
        private bool _dragPicture;
        private Point _pressedAt;
        private (double X, double Y) _atPress;

        public Overlay(EditorPreview owner)
        {
            _owner = owner;
            Background = Brushes.Transparent;
        }

        public IBrush? Background { get; }

        private static readonly Cursor SizeAll = new(StandardCursorType.SizeAll);
        private static readonly Cursor Hand = new(StandardCursorType.Hand);

        public override void Render(DrawingContext context)
        {
            context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
            var host = _owner.Host;
            var frame = _owner.Frame;
            if (host is null || frame.Width <= 0)
                return;
            _owner.DrawPicture(context, frame);
            var accent = _owner.AccentBrush ?? Brushes.MediumPurple;
            using (context.PushClip(frame))
            {
                foreach (var (index, text) in host.VisibleTexts)
                {
                    var drawn = Draw(context, text, frame);
                    if (index == host.SelectedText && drawn is { } bounds)
                        context.DrawRectangle(null, new Pen(accent, 1.5) { DashStyle = new DashStyle([4, 3], 0) }, bounds.Inflate(6), 4, 4);
                }
            }
            if (host.CanMovePicture && !host.IsComposed && _owner.MoveHint is { Length: > 0 } hint)
            {
                var label = new FormattedText("✥  " + hint, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11.5, Brushes.White);
                // In the preview's corner: an upright frame is narrower than the words.
                var chip = new Rect(12, Bounds.Height - label.Height - 20, label.Width + 18, label.Height + 8);
                context.DrawRectangle(new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), null, chip, 999, 999);
                context.DrawText(label, new Point(chip.X + 9, chip.Y + 4));
            }
        }

        /// <summary>Draws <paramref name="text"/> in its style; returns where it is.</summary>
        internal static Rect? Draw(DrawingContext? context, ClipText text, Rect frame)
        {
            if (string.IsNullOrWhiteSpace(text.Text))
                return null;
            var size = Math.Max(4, text.PlaySize * frame.Height);
            var color = ClipCanvas.ParseColor(text.PlayColor) is { } c ? Color.FromRgb(c.R, c.G, c.B) : Colors.White;
            var light = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B > 160;
            var letters = text.Style == ClipTextStyle.Label ? light ? Color.FromRgb(17, 17, 17) : Colors.White : color;
            var formatted = new FormattedText(text.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Bold, size, new SolidColorBrush(letters))
            {
                TextAlignment = TextAlignment.Center,
                MaxTextWidth = Math.Max(10, frame.Width * 0.94),
                LineHeight = size * 1.12,
            };
            var origin = new Point(frame.X + text.X * frame.Width - formatted.MaxTextWidth / 2, frame.Y + text.Y * frame.Height - formatted.Height / 2);
            var geometry = formatted.BuildGeometry(origin);
            if (geometry is null)
                return null;
            var bounds = geometry.Bounds;
            if (context is null)
                return bounds;
            switch (text.Style)
            {
                case ClipTextStyle.Caption:
                    context.DrawRectangle(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), null, bounds.Inflate(size * 0.35), size * 0.18, size * 0.18);
                    break;
                case ClipTextStyle.Label:
                    context.DrawRectangle(new SolidColorBrush(color), null, bounds.Inflate(size * 0.4), size * 0.2, size * 0.2);
                    break;
                default:
                    // The outline and a soft shadow of a title.
                    context.DrawGeometry(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), null, formatted.BuildGeometry(origin + new Point(size / 30, size / 30))!);
                    context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), size * 0.14, lineJoin: PenLineJoin.Round), geometry);
                    break;
            }
            context.DrawGeometry(new SolidColorBrush(letters), null, geometry);
            return bounds;
        }

        /// <summary>The visible text under <paramref name="point"/>, topmost first, or -1.</summary>
        private int TextAt(Point point)
        {
            if (_owner.Host is not { } host)
                return -1;
            var frame = _owner.Frame;
            foreach (var (index, text) in host.VisibleTexts.Reverse())
            {
                if (Draw(null, text, frame) is { } bounds && bounds.Inflate(8).Contains(point))
                    return index;
            }
            return -1;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (_owner.Host is not { } host || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;
            var point = e.GetPosition(this);
            var frame = _owner.Frame;
            _pressedAt = point;
            var text = TextAt(point);
            if (e.ClickCount == 2)
            {
                if (text >= 0)
                    host.EditText(text);
                else
                    host.ToggleFullPreview();
                e.Handled = true;
                return;
            }
            if (text >= 0)
            {
                host.SelectText(text);
                var found = host.VisibleTexts.First(t => t.Index == text).Text;
                _dragText = text;
                _atPress = (found.X, found.Y);
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
            if (frame.Contains(point) && host.CanMovePicture && !host.IsComposed && host.PicturePart is { } part)
            {
                _dragPicture = true;
                _atPress = (part.FocusX, part.FocusY);
                e.Pointer.Capture(this);
                e.Handled = true;
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var point = e.GetPosition(this);
            var host = _owner.Host;
            var frame = _owner.Frame;
            if (host is null || frame.Width <= 0)
                return;
            if (_dragText >= 0)
            {
                var x = Math.Clamp(_atPress.X + (point.X - _pressedAt.X) / frame.Width, 0, 1);
                var y = Math.Clamp(_atPress.Y + (point.Y - _pressedAt.Y) / frame.Height, 0, 1);
                // The middle lines snap: across and down.
                if (Math.Abs(x - 0.5) * frame.Width < 6)
                    x = 0.5;
                host.MoveText(_dragText, x, y);
                return;
            }
            if (_dragPicture && host.PicturePart is { } part)
            {
                var (width, height) = host.Canvas;
                var scale = width / frame.Width;
                var (pictureWidth, pictureHeight) = host.PictureSize;
                var place = ClipCanvas.Place(pictureWidth, pictureHeight, width, height, host.Fit, part.PlayZoom, _atPress.X, _atPress.Y);
                var (fx, fy) = ClipCanvas.Drag(place, width, height, _atPress.X, _atPress.Y, (point.X - _pressedAt.X) * scale, (point.Y - _pressedAt.Y) * scale);
                host.MovePicture(fx, fy);
                return;
            }
            Cursor = TextAt(point) >= 0 ? SizeAll : frame.Contains(point) && host.CanMovePicture && !host.IsComposed ? Hand : null;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (_dragText >= 0 || _dragPicture)
                _owner.Host?.EndDrag();
            _dragText = -1;
            _dragPicture = false;
            e.Pointer.Capture(null);
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            if (_dragText >= 0 || _dragPicture)
                _owner.Host?.EndDrag();
            _dragText = -1;
            _dragPicture = false;
        }
    }
}
