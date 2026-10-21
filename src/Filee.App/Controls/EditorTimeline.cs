// The editor's timeline: the clip as it plays on four tracks under a ruler. T the texts (dragged along, their edges
// set how long they stay), V1 the parts' pictures (clicked to select, dragged to another place, their edges dragged to
// take more or less of their video; a button between two parts picks the transition; a gap a part left has a button
// that closes it), A1 the parts' own sound (its loudness and a volume line to drag), A2 the music (dragged along, its own
// volume line). The ruler and empty places put the playhead. The wheel scrolls, Alt + wheel zooms (also the slider in
// the editor). Files dropped on it go in where they are dropped. Time runs left to right in every language.

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Filee.App.Services;
using Filee.Core.Presets;

namespace Filee.App.Controls;

public sealed class EditorTimeline : Control
{
    public static readonly StyledProperty<IEditorTimelineHost?> HostProperty =
        AvaloniaProperty.Register<EditorTimeline, IEditorTimelineHost?>(nameof(Host));

    /// <summary>How far it is zoomed in: 0 the whole clip in view, 1 closest.</summary>
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<EditorTimeline, double>(nameof(Zoom), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<EditorTimeline, IBrush?>(nameof(AccentBrush), Brushes.MediumPurple);

    public static readonly StyledProperty<IBrush?> OnAccentBrushProperty =
        AvaloniaProperty.Register<EditorTimeline, IBrush?>(nameof(OnAccentBrush), Brushes.White);

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<EditorTimeline, IBrush?>(nameof(TrackBrush), Brushes.LightGray);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<EditorTimeline, IBrush?>(nameof(Foreground), Brushes.Black);

    public static readonly StyledProperty<IBrush?> SoundBrushProperty =
        AvaloniaProperty.Register<EditorTimeline, IBrush?>(nameof(SoundBrush), new SolidColorBrush(Color.FromRgb(46, 160, 140)));

    public static readonly StyledProperty<IBrush?> MusicBrushProperty =
        AvaloniaProperty.Register<EditorTimeline, IBrush?>(nameof(MusicBrush), new SolidColorBrush(Color.FromRgb(214, 138, 54)));

    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<EditorTimeline, IBrush?>(nameof(TextBrush), new SolidColorBrush(Color.FromRgb(232, 80, 91)));

    public IEditorTimelineHost? Host { get => GetValue(HostProperty); set => SetValue(HostProperty, value); }
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? OnAccentBrush { get => GetValue(OnAccentBrushProperty); set => SetValue(OnAccentBrushProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public IBrush? SoundBrush { get => GetValue(SoundBrushProperty); set => SetValue(SoundBrushProperty, value); }
    public IBrush? MusicBrush { get => GetValue(MusicBrushProperty); set => SetValue(MusicBrushProperty, value); }
    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }

    // Layout: the tracks' names on the left, a ruler on top, then T, V1, A1, A2, and a scroll bar when zoomed in.
    internal const double LabelWidth = 30;
    internal const double RulerHeight = 20;
    internal const double TextHeight = 22;
    internal const double VideoHeight = 54;
    internal const double SoundHeight = 34;
    internal const double MusicHeight = 28;
    private const double LaneGap = 4;
    private const double BarHeight = 8;
    private const double Radius = 6;
    private const double EdgeGrab = 7;
    private const double DragThreshold = 4;
    private const double SnapDistance = 8;
    private const double TransitionSize = 18;

    /// <summary>Zoomed all the way in, a second is this many times as wide as with the whole clip in view (at most <see cref="MaxPixelsPerSecond"/>).</summary>
    private const double MaxZoomFactor = 60;

    private const double MaxPixelsPerSecond = 600;

    private enum Drag { None, Playhead, Edge, Volume, Music, MusicVolume, Pending, Move, Text, TextStart, TextEnd, Scroll }

    private enum Lane { None, Ruler, Text, Video, Sound, Music, Bar }

    private static readonly Cursor IbeamCursor = new(StandardCursorType.Ibeam);
    private static readonly Cursor SizeNorthSouthCursor = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor SizeAllCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor SizeWestEastCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private double _scroll;          // seconds at the left edge
    private Drag _drag;
    private Point _pressedAt;
    private int _index = -1;         // the part, text or gap the drag is about
    private bool _dragEnd;
    private double _valueAtPress;    // an offset, a start or a length when the drag started
    private double _lengthAtPress;
    private double _startAtPress;
    private ClipSelect _pendingMode;
    private Lane _pendingLane;
    private int _moveTo = -1;        // where a part being moved goes
    private double _dropAt = -1;     // where dropped files go (seconds), while they are dragged over
    private IEditorTimelineHost? _hooked;

    static EditorTimeline()
    {
        AffectsRender<EditorTimeline>(HostProperty, ZoomProperty, AccentBrushProperty, TrackBrushProperty, ForegroundProperty);
        FocusableProperty.OverrideDefaultValue<EditorTimeline>(false);
    }

    public EditorTimeline()
    {
        FlowDirection = FlowDirection.LeftToRight;
        ClipToBounds = true;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) =>
        {
            _dropAt = -1;
            InvalidateVisual();
        });
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HostProperty)
        {
            if (_hooked is not null)
                _hooked.TimelineChanged -= OnHostChanged;
            _hooked = Host;
            if (_hooked is not null)
                _hooked.TimelineChanged += OnHostChanged;
        }
        else if (change.Property == ZoomProperty && change.OldValue is double before && Host is { } host)
        {
            // Zoomed around the playhead: it stays where it is on screen.
            var x = XOf(host.Playhead, before);
            _scroll = host.Playhead - (x - Content.X) / PixelsPerSecond();
            ClampScroll();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_hooked is not null)
            _hooked.TimelineChanged -= OnHostChanged;
        _hooked = null;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_hooked is null && Host is { } host)
        {
            _hooked = host;
            host.TimelineChanged += OnHostChanged;
        }
    }

    private void OnHostChanged()
    {
        // While it plays, the playhead is kept in view (a page on when it reaches the right edge).
        if (_drag == Drag.None && Host is { } host && PixelsPerSecond() > 0)
        {
            var x = XOf(host.Playhead);
            if (x > Content.Right - 4 || x < Content.X)
            {
                _scroll = host.Playhead - Content.Width / PixelsPerSecond() * 0.1;
                ClampScroll();
            }
        }
        ClampScroll();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(0, RulerHeight + TextHeight + LaneGap + VideoHeight + LaneGap + SoundHeight + LaneGap + MusicHeight + LaneGap + BarHeight);

    // ───────────────────────── Geometry ─────────────────────────

    internal Rect Content => new(LabelWidth + 6, 0, Math.Max(1, Bounds.Width - LabelWidth - 12), Bounds.Height);
    internal Rect TextLane => new(Content.X, RulerHeight, Content.Width, TextHeight);
    internal Rect VideoLane => new(Content.X, TextLane.Bottom + LaneGap, Content.Width, VideoHeight);
    internal Rect SoundLane => new(Content.X, VideoLane.Bottom + LaneGap, Content.Width, SoundHeight);
    internal Rect MusicLane => new(Content.X, SoundLane.Bottom + LaneGap, Content.Width, MusicHeight);
    private Rect BarLane => new(Content.X, MusicLane.Bottom + LaneGap, Content.Width, BarHeight);

    /// <summary>How long the clip plays (at least a moment).</summary>
    private double Total => Math.Max(ClipRange.MinLength, Host?.Edit.Length ?? 0);

    /// <summary>Pixels a second at <paramref name="zoom"/> (the whole clip in view at 0).</summary>
    private double PixelsPerSecond(double? zoom = null)
    {
        var fit = Content.Width / Total;
        return Math.Max(fit, Math.Min(fit * Math.Pow(MaxZoomFactor, Math.Clamp(zoom ?? Zoom, 0, 1)), MaxPixelsPerSecond));
    }

    private bool IsZoomed => PixelsPerSecond() * Total > Content.Width + 1;

    internal double XOf(double seconds) => Content.X + (seconds - _scroll) * PixelsPerSecond();

    private double XOf(double seconds, double zoom) => Content.X + (seconds - _scroll) * PixelsPerSecond(zoom);

    internal double TimeAt(double x) => Math.Clamp(_scroll + (x - Content.X) / PixelsPerSecond(), 0, Total);

    private void ClampScroll()
    {
        var visible = Content.Width / PixelsPerSecond();
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, Total - visible));
    }

    private Lane LaneAt(Point point)
    {
        if (point.X < Content.X - 4)
            return Lane.None;
        if (point.Y < RulerHeight)
            return Lane.Ruler;
        if (point.Y < TextLane.Bottom + LaneGap / 2)
            return Lane.Text;
        if (point.Y < VideoLane.Bottom + LaneGap / 2)
            return Lane.Video;
        if (point.Y < SoundLane.Bottom + LaneGap / 2)
            return Lane.Sound;
        if (point.Y < MusicLane.Bottom + LaneGap / 2)
            return Lane.Music;
        return Lane.Bar;
    }

    /// <summary>The part playing at <paramref name="seconds"/> (the incoming one in a transition), or -1.</summary>
    private int PartAt(double seconds)
    {
        if (Host is not { } host)
            return -1;
        var starts = host.Edit.PartStarts();
        for (var i = starts.Length - 1; i >= 0; i--)
        {
            if (seconds >= starts[i] && seconds <= starts[i] + host.Edit.Parts[i].Length)
                return i;
        }
        return -1;
    }

    private Rect PartRect(int index, Rect lane)
    {
        var edit = Host!.Edit;
        var start = edit.PartStarts()[index];
        var x0 = XOf(start);
        var x1 = XOf(start + edit.Parts[index].Length);
        return new Rect(x0, lane.Y, Math.Max(1, x1 - x0), lane.Height);
    }

    /// <summary>Where the button between part <paramref name="index"/> and the next one is.</summary>
    private Rect TransitionButton(int index)
    {
        var edit = Host!.Edit;
        var join = edit.PartStarts()[index + 1] + edit.Overlap(index) / 2;
        var x = XOf(join);
        return new Rect(x - TransitionSize / 2, VideoLane.Center.Y - TransitionSize / 2, TransitionSize, TransitionSize);
    }

    private Rect TextRect(ClipText text) =>
        new(XOf(text.Start), TextLane.Y + 1, Math.Max(6, text.Length * PixelsPerSecond()), TextLane.Height - 2);

    private Rect? MusicRect()
    {
        if (Host?.Edit.Music is not { } music)
            return null;
        var x0 = XOf(music.Offset);
        return new Rect(x0, MusicLane.Y, Math.Max(2, music.PlayLength(Total) * PixelsPerSecond()), MusicLane.Height);
    }

    // ───────────────────────── Drawing ─────────────────────────

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width < LabelWidth + 40 || Host is not { } host)
            return;
        var edit = host.Edit;
        var accent = AccentBrush ?? Brushes.MediumPurple;
        DrawLabels(context);
        foreach (var lane in new[] { TextLane, VideoLane, SoundLane, MusicLane })
            context.DrawRectangle(TrackBrush ?? Brushes.LightGray, null, lane, Radius, Radius);

        using (context.PushClip(new Rect(Content.X - 2, 0, Content.Width + 4, Bounds.Height)))
        {
            DrawRuler(context);
            var starts = edit.PartStarts();
            for (var i = 0; i < edit.Parts.Count; i++)
            {
                if (_drag == Drag.Move && i == _index)
                    continue;
                DrawPart(context, edit, starts, i, accent, host.SelectedParts.Contains(i));
            }
            for (var i = 0; i < edit.Parts.Count - 1; i++)
                DrawTransition(context, edit, i, accent, host.SelectedTransition == i);
            DrawFades(context, edit, accent);
            DrawTexts(context, edit, accent, host.SelectedText);
            DrawMusic(context, host, accent);
            if (_drag == Drag.Move && _index >= 0 && _index < edit.Parts.Count)
                DrawMoving(context, edit, starts, accent);
            if (_dropAt >= 0)
                DrawInsertMarker(context, XOf(_dropAt), accent);
            DrawPlayhead(context, host.Playhead);
        }
        if (IsZoomed)
            DrawScrollBar(context);
    }

    private void DrawLabels(DrawingContext context)
    {
        var brush = Foreground ?? Brushes.Black;
        void Label(string text, Rect lane)
        {
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, brush);
            using (context.PushOpacity(0.6))
                context.DrawText(formatted, new Point(4, lane.Center.Y - formatted.Height / 2));
        }
        Label("T", TextLane);
        Label("V1", VideoLane);
        Label("A1", SoundLane);
        Label("A2", MusicLane);
    }

    private void DrawRuler(DrawingContext context)
    {
        var foreground = Foreground ?? Brushes.Gray;
        var pen = new Pen(foreground, 1);
        double[] steps = [0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600];
        var pps = PixelsPerSecond();
        var step = steps.FirstOrDefault(s => s * pps >= 9, steps[^1]);
        var labelEvery = steps.FirstOrDefault(s => s * pps >= 70 && Math.Abs(s / step - Math.Round(s / step)) < 1e-9, steps[^1]);
        var first = Math.Floor(_scroll / step);
        using (context.PushOpacity(0.5))
        {
            for (var n = first; n * step <= Math.Min(Total, _scroll + Content.Width / pps) + 1e-6; n++)
            {
                var t = n * step;
                var x = Math.Round(XOf(t)) + 0.5;
                var labelled = Math.Abs(t / labelEvery - Math.Round(t / labelEvery)) < 1e-6;
                context.DrawLine(pen, new Point(x, RulerHeight - (labelled ? 8 : 4)), new Point(x, RulerHeight - 1));
                if (!labelled)
                    continue;
                var text = new FormattedText(ClipRange.Display(t), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 9.5, foreground);
                context.DrawText(text, new Point(x + 3, 0));
            }
        }
    }

    private void DrawPart(DrawingContext context, ClipEdit edit, double[] starts, int index, IBrush accent, bool selected)
    {
        var part = edit.Parts[index];
        var video = PartRect(index, VideoLane);
        if (video.Right < Content.X - 50 || video.X > Content.Right + 50)
            return;
        var sound = new Rect(video.X, SoundLane.Y, video.Width, SoundLane.Height);
        if (part.Blank)
        {
            DrawGap(context, video, sound, accent);
            return;
        }
        DrawPictures(context, part, video);
        DrawSound(context, part, sound);
        DrawBadges(context, part, video, index);
        var outline = new Pen(selected ? accent : new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), selected ? 2.5 : 1);
        context.DrawRectangle(null, outline, video.Deflate(selected ? 1.25 : 0.5), Radius, Radius);
        context.DrawRectangle(null, outline, sound.Deflate(selected ? 1.25 : 0.5), Radius, Radius);
    }

    /// <summary>Pictures of the part's video side by side, each from where that moment of the part is in it.</summary>
    private void DrawPictures(DrawingContext context, ClipPart part, Rect rect)
    {
        using var clip = context.PushClip(new RoundedRect(rect, Radius));
        context.FillRectangle(TrackBrush ?? Brushes.Gray, rect);
        var tile = rect.Height * 16 / 9;
        var pps = PixelsPerSecond();
        var first = Math.Max(rect.X, Content.X - tile);
        for (var x = rect.X + Math.Floor((first - rect.X) / tile) * tile; x < Math.Min(rect.Right, Content.Right + tile); x += tile)
        {
            var seconds = (x + tile / 2 - rect.X) / pps;
            if (Host!.PictureAt(part.Source, part.SourceAt(seconds)) is not { } bitmap)
                continue;
            var target = new Rect(x, rect.Y, tile, rect.Height);
            var size = bitmap.Size;
            var scale = Math.Max(target.Width / size.Width, target.Height / size.Height);
            var w = target.Width / scale;
            var h = target.Height / scale;
            context.DrawImage(bitmap, new Rect((size.Width - w) / 2, (size.Height - h) / 2, w, h), target);
        }
    }

    /// <summary>The part's sound: its loudness (as loud as its volume makes it) and its volume line; hatched when muted.</summary>
    private void DrawSound(DrawingContext context, ClipPart part, Rect rect)
    {
        var brush = SoundBrush ?? Brushes.Teal;
        using var clip = context.PushClip(new RoundedRect(rect, Radius));
        using (context.PushOpacity(part.Muted || part.IsStill ? 0.15 : 0.32))
            context.FillRectangle(brush, rect);
        if (part.Muted || part.IsStill)
        {
            using (context.PushOpacity(0.3))
            {
                var hatch = new Pen(Foreground ?? Brushes.Black, 1);
                for (var x = rect.Left - rect.Height; x < rect.Right; x += 8)
                    context.DrawLine(hatch, new Point(x, rect.Bottom), new Point(x + rect.Height, rect.Top));
            }
            return;
        }
        var levels = Host!.LoudnessOf(part.Source);
        var duration = Host.DurationOf(part.Source);
        if (levels.Count > 0 && duration > 0)
        {
            var bars = new StreamGeometry();
            using (var g = bars.Open())
            {
                var pps = PixelsPerSecond();
                var from = Math.Max(rect.X, Content.X);
                var to = Math.Min(rect.Right, Content.Right);
                for (var x = from; x < to; x += 2)
                {
                    var seconds = part.SourceAt((x + 1 - rect.X) / pps);
                    var level = levels[Math.Clamp((int)(seconds / duration * levels.Count), 0, levels.Count - 1)];
                    var height = Math.Min(rect.Height - 4, Math.Max(0.5, level * Math.Min(part.PlayVolume, 2) * (rect.Height - 4) / 2));
                    g.BeginFigure(new Point(x, rect.Center.Y - height / 2), true);
                    g.LineTo(new Point(x + 1.4, rect.Center.Y - height / 2));
                    g.LineTo(new Point(x + 1.4, rect.Center.Y + height / 2));
                    g.LineTo(new Point(x, rect.Center.Y + height / 2));
                    g.EndFigure(true);
                }
            }
            context.DrawGeometry(brush, null, bars);
        }
        var y = VolumeY(rect, part.Volume);
        context.DrawLine(new Pen(Brushes.White, 3), new Point(rect.Left, y), new Point(rect.Right, y));
        context.DrawLine(new Pen(Foreground ?? Brushes.Black, 1.2), new Point(rect.Left, y), new Point(rect.Right, y));
    }

    private static double VolumeY(Rect rect, double volume) => rect.Bottom - 3 - Math.Clamp(volume, 0, 2) / 2 * (rect.Height - 6);

    private static double VolumeAt(Rect rect, double y) => Math.Clamp((rect.Bottom - 3 - y) / (rect.Height - 6) * 2, 0, 2);

    /// <summary>A gap a part cut out left: dashed, hatched, with the button that closes it.</summary>
    private void DrawGap(DrawingContext context, Rect video, Rect sound, IBrush accent)
    {
        var rect = new Rect(video.X, video.Y, video.Width, sound.Bottom - video.Y);
        context.DrawRectangle(null, new Pen(accent, 1.5) { DashStyle = new DashStyle([4, 3], 0) }, rect.Deflate(1), Radius, Radius);
        using (context.PushClip(rect))
        using (context.PushOpacity(0.25))
        {
            var hatch = new Pen(accent, 1);
            for (var x = rect.Left - rect.Height; x < rect.Right; x += 9)
                context.DrawLine(hatch, new Point(x, rect.Bottom), new Point(x + rect.Height, rect.Top));
        }
        if (GapButton(rect) is not { } chip)
            return;
        context.DrawRectangle(accent, null, chip, 999, 999);
        var text = new FormattedText(chip.Width > 40 ? "✕ " + Host!.GapText : "✕", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, 11, OnAccentBrush ?? Brushes.White);
        context.DrawText(text, new Point(chip.Center.X - text.Width / 2, chip.Center.Y - text.Height / 2));
    }

    private Rect? GapButton(Rect gap)
    {
        if (gap.Width < 18)
            return null;
        var text = new FormattedText("✕ " + Host!.GapText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, Brushes.White);
        var width = text.Width + 16 <= gap.Width - 8 ? text.Width + 16 : 20;
        return new Rect(gap.Center.X - width / 2, gap.Center.Y - 10, width, 20);
    }

    /// <summary>Its video's name, and what it does: speed, backwards, still, closer look, sound off.</summary>
    private void DrawBadges(DrawingContext context, ClipPart part, Rect rect, int index)
    {
        var notes = new List<string>();
        if (part.IsStill)
            notes.Add("❚❚ " + ClipRange.Display(part.Length));
        else if (part.IsRamp)
            notes.Add($"{part.PlaySpeed.ToString("0.##", CultureInfo.CurrentCulture)}→{part.PlaySpeedEnd.ToString("0.##", CultureInfo.CurrentCulture)}×");
        else if (part.PlaySpeed != 1)
            notes.Add("×" + part.PlaySpeed.ToString("0.##", CultureInfo.CurrentCulture));
        if (part.Reverse)
            notes.Add("◀◀");
        if (part.PlayZoom > 1.001)
            notes.Add("⤢ " + Math.Round(part.PlayZoom * 100).ToString(CultureInfo.CurrentCulture) + "%");
        if (part.Muted)
            notes.Add("A1 ✕");
        var name = rect.Width > 90 && Host!.Edit.Parts.Select(p => p.Source).Distinct().Count() > 1
            ? Path.GetFileNameWithoutExtension(part.Source ?? "") is { Length: > 0 } file ? file : null
            : null;
        var line = string.Join("  ", (name is null ? notes : notes.Prepend(name)).Where(n => n.Length > 0));
        if (line.Length == 0 && index >= 0)
            return;
        var text = new FormattedText(line, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10.5, Brushes.White);
        if (text.Width + 12 > rect.Width - 4)
            return;
        var chip = new Rect(rect.X + 4, rect.Y + 4, text.Width + 10, text.Height + 2);
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), null, chip, 5, 5);
        context.DrawText(text, new Point(chip.X + 5, chip.Y + 1));
    }

    /// <summary>The button between two parts: plain for a cut, filled with its sign for a transition, its overlap shaded.</summary>
    private void DrawTransition(DrawingContext context, ClipEdit edit, int index, IBrush accent, bool selected)
    {
        var part = edit.Parts[index];
        var overlap = edit.Overlap(index);
        if (overlap > 0)
        {
            var start = edit.PartStarts()[index + 1];
            var band = new Rect(XOf(start), VideoLane.Y, overlap * PixelsPerSecond(), VideoLane.Height);
            using (context.PushOpacity(0.35))
                context.FillRectangle(accent, band);
        }
        var button = TransitionButton(index);
        if (button.Right < Content.X || button.X > Content.Right)
            return;
        var on = part.Transition != ClipTransitionKind.Cut;
        context.DrawEllipse(on || selected ? accent : new SolidColorBrush(Color.FromArgb(200, 30, 30, 34)),
            new Pen(selected ? Brushes.White : on ? accent : new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)), selected ? 2 : 1),
            button.Center, TransitionSize / 2, TransitionSize / 2);
        var sign = part.Transition switch
        {
            ClipTransitionKind.Crossfade => "⧓",
            ClipTransitionKind.Black => "◐",
            ClipTransitionKind.Sound => "♪",
            _ => "+",
        };
        var text = new FormattedText(sign, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11,
            on || selected ? OnAccentBrush ?? Brushes.White : Brushes.White);
        context.DrawText(text, new Point(button.Center.X - text.Width / 2, button.Center.Y - text.Height / 2));
    }

    private void DrawFades(DrawingContext context, ClipEdit edit, IBrush accent)
    {
        var height = VideoLane.Height;
        using (context.PushOpacity(0.55))
        {
            if (edit.FadeIn > 0)
            {
                var x0 = XOf(0);
                var x1 = XOf(Math.Min(edit.FadeIn, Total));
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(x0, VideoLane.Y), true);
                    c.LineTo(new Point(x1, VideoLane.Y));
                    c.LineTo(new Point(x0, VideoLane.Y + height));
                    c.EndFigure(true);
                }
                context.DrawGeometry(Brushes.Black, null, g);
            }
            if (edit.FadeOut > 0)
            {
                var x0 = XOf(Math.Max(0, Total - edit.FadeOut));
                var x1 = XOf(Total);
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(x0, VideoLane.Y), true);
                    c.LineTo(new Point(x1, VideoLane.Y));
                    c.LineTo(new Point(x1, VideoLane.Y + height));
                    c.EndFigure(true);
                }
                context.DrawGeometry(Brushes.Black, null, g);
            }
        }
    }

    private void DrawTexts(DrawingContext context, ClipEdit edit, IBrush accent, int selected)
    {
        var brush = TextBrush ?? accent;
        for (var i = 0; i < edit.Texts.Count; i++)
        {
            var text = edit.Texts[i];
            var rect = TextRect(text);
            if (rect.Right < Content.X || rect.X > Content.Right)
                continue;
            var on = i == selected;
            using (context.PushOpacity(on ? 1 : 0.7))
                context.DrawRectangle(brush, on ? new Pen(Brushes.White, 1.5) : null, rect, 5, 5);
            var label = new FormattedText("T  " + text.Text.Replace('\n', ' '), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(Typeface.Default.FontFamily, FontStyle.Normal, FontWeight.SemiBold), 10.5, Brushes.White)
            {
                MaxTextWidth = Math.Max(1, rect.Width - 12),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            using (context.PushClip(rect))
                context.DrawText(label, new Point(rect.X + 6, rect.Center.Y - label.Height / 2));
        }
    }

    private void DrawMusic(DrawingContext context, IEditorTimelineHost host, IBrush accent)
    {
        var lane = MusicLane;
        if (MusicRect() is not { } rect || host.Edit.Music is not { } music)
        {
            if (host.NoMusicText is { Length: > 0 } hint)
            {
                var text = new FormattedText(hint, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10.5, Foreground ?? Brushes.Gray);
                using (context.PushOpacity(0.45))
                    context.DrawText(text, new Point(Content.X + 8, lane.Center.Y - text.Height / 2));
            }
            return;
        }
        var brush = MusicBrush ?? Brushes.Orange;
        var selected = host.IsMusicSelected;
        using (context.PushClip(new RoundedRect(rect, Radius)))
        {
            using (context.PushOpacity(0.3))
                context.FillRectangle(brush, rect);
            var levels = host.MusicLoudness;
            if (levels.Count > 0 && music.Length > 0)
            {
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    var pps = PixelsPerSecond();
                    for (var x = Math.Max(rect.X, Content.X); x < Math.Min(rect.Right, Content.Right); x += 2)
                    {
                        var seconds = music.From + (x - rect.X) / pps;
                        var level = levels[Math.Clamp((int)(seconds / music.Length * levels.Count), 0, levels.Count - 1)];
                        var height = Math.Min(rect.Height - 4, Math.Max(0.5, level * music.PlayVolume * (rect.Height - 4) / 2));
                        c.BeginFigure(new Point(x, rect.Center.Y - height / 2), true);
                        c.LineTo(new Point(x + 1.4, rect.Center.Y - height / 2));
                        c.LineTo(new Point(x + 1.4, rect.Center.Y + height / 2));
                        c.LineTo(new Point(x, rect.Center.Y + height / 2));
                        c.EndFigure(true);
                    }
                }
                context.DrawGeometry(brush, null, g);
            }
            var name = new FormattedText(Path.GetFileName(music.Path), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10,
                Foreground ?? Brushes.Black);
            context.DrawText(name, new Point(rect.X + 6, rect.Y + 1));
            var y = VolumeY(rect, music.Volume);
            context.DrawLine(new Pen(Brushes.White, 3), new Point(rect.Left, y), new Point(rect.Right, y));
            context.DrawLine(new Pen(Foreground ?? Brushes.Black, 1.2), new Point(rect.Left, y), new Point(rect.Right, y));
        }
        context.DrawRectangle(null, new Pen(selected ? accent : brush, selected ? 2.5 : 1), rect.Deflate(selected ? 1.25 : 0.5), Radius, Radius);
    }

    /// <summary>The part being moved, under the pointer, and where it would go.</summary>
    private void DrawMoving(DrawingContext context, ClipEdit edit, double[] starts, IBrush accent)
    {
        var rect = PartRect(_index, VideoLane);
        var moved = rect.Translate(new Vector(_lastPoint.X - _pressedAt.X, 0));
        using (context.PushOpacity(0.7))
            DrawPictures(context, edit.Parts[_index], moved);
        context.DrawRectangle(null, new Pen(accent, 2.5), moved.Deflate(1.25), Radius, Radius);
        if (_moveTo >= 0)
        {
            var at = _moveTo >= edit.Parts.Count ? Total : starts[_moveTo];
            if (_moveTo > _index && _moveTo < edit.Parts.Count)
                at = starts[_moveTo] + edit.Parts[_moveTo].Length;
            else if (_moveTo >= edit.Parts.Count - 1 && _moveTo > _index)
                at = Total;
            DrawInsertMarker(context, XOf(at), accent);
        }
    }

    private void DrawInsertMarker(DrawingContext context, double x, IBrush accent)
    {
        context.DrawLine(new Pen(accent, 3), new Point(x, VideoLane.Y - 2), new Point(x, SoundLane.Bottom + 2));
        context.DrawEllipse(accent, null, new Point(x, VideoLane.Y - 2), 4, 4);
    }

    private void DrawPlayhead(DrawingContext context, double seconds)
    {
        var x = Math.Round(XOf(seconds)) + 0.5;
        if (x < Content.X - 1 || x > Content.Right + 1)
            return;
        var accent = AccentBrush ?? Brushes.MediumPurple;
        context.DrawLine(new Pen(Brushes.White, 3), new Point(x, RulerHeight - 6), new Point(x, MusicLane.Bottom));
        context.DrawLine(new Pen(accent, 1.6), new Point(x, RulerHeight - 6), new Point(x, MusicLane.Bottom));
        var head = new StreamGeometry();
        using (var g = head.Open())
        {
            g.BeginFigure(new Point(x - 6, RulerHeight - 12), true);
            g.LineTo(new Point(x + 6, RulerHeight - 12));
            g.LineTo(new Point(x + 6, RulerHeight - 6));
            g.LineTo(new Point(x, RulerHeight));
            g.LineTo(new Point(x - 6, RulerHeight - 6));
            g.EndFigure(true);
        }
        context.DrawGeometry(accent, null, head);
    }

    private void DrawScrollBar(DrawingContext context)
    {
        var lane = BarLane;
        context.DrawRectangle(TrackBrush ?? Brushes.LightGray, null, lane, 4, 4);
        var visible = Content.Width / PixelsPerSecond();
        var thumb = new Rect(lane.X + _scroll / Total * lane.Width, lane.Y, Math.Max(24, visible / Total * lane.Width), lane.Height);
        using (context.PushOpacity(0.55))
            context.DrawRectangle(Foreground ?? Brushes.Gray, null, thumb, 4, 4);
    }

    // ───────────────────────── Pointer ─────────────────────────

    private Point _lastPoint;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Host is not { } host || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var point = e.GetPosition(this);
        _pressedAt = _lastPoint = point;
        var edit = host.Edit;
        var lane = LaneAt(point);
        var mods = e.KeyModifiers;
        _pendingMode = mods.HasFlag(KeyModifiers.Shift) ? ClipSelect.Range
            : mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Meta) ? ClipSelect.Toggle
            : ClipSelect.Only;
        _pendingLane = lane;
        _index = -1;
        e.Pointer.Capture(this);
        e.Handled = true;

        switch (lane)
        {
            case Lane.Ruler:
                _drag = Drag.Playhead;
                host.Seek(TimeAt(point.X));
                return;
            case Lane.Bar when IsZoomed:
                _drag = Drag.Scroll;
                _valueAtPress = _scroll;
                return;
            case Lane.Text:
                for (var i = edit.Texts.Count - 1; i >= 0; i--)
                {
                    var pill = TextRect(edit.Texts[i]);
                    if (!pill.Inflate(new Thickness(2, 0)).Contains(point))
                        continue;
                    _index = i;
                    _startAtPress = edit.Texts[i].Start;
                    _lengthAtPress = edit.Texts[i].Length;
                    _drag = Math.Abs(point.X - pill.Right) <= EdgeGrab ? Drag.TextEnd
                        : Math.Abs(point.X - pill.X) <= EdgeGrab ? Drag.TextStart
                        : Drag.Text;
                    host.SelectText(i);
                    return;
                }
                break;
            case Lane.Video or Lane.Sound:
                if (lane == Lane.Video)
                {
                    for (var i = 0; i < edit.Parts.Count - 1; i++)
                    {
                        if (TransitionButton(i).Inflate(3).Contains(point))
                        {
                            _drag = Drag.None;
                            host.SelectTransition(i);
                            return;
                        }
                    }
                }
                if (EdgeAt(point) is { } edge)
                {
                    _drag = Drag.Edge;
                    _index = edge.Index;
                    _dragEnd = edge.End;
                    _startAtPress = edit.PartStarts()[edge.Index];
                    _lengthAtPress = edit.Parts[edge.Index].Length;
                    host.SelectPart(edge.Index, ClipSelect.Only);
                    return;
                }
                var index = PartAt(TimeAt(point.X));
                if (index < 0)
                    break;
                var part = edit.Parts[index];
                var rect = PartRect(index, lane == Lane.Video ? VideoLane : SoundLane);
                if (part.Blank)
                {
                    var gap = new Rect(rect.X, VideoLane.Y, rect.Width, SoundLane.Bottom - VideoLane.Y);
                    if (GapButton(gap) is { } chip && chip.Inflate(3).Contains(point))
                    {
                        _drag = Drag.None;
                        host.CloseGap(index);
                        return;
                    }
                }
                if (lane == Lane.Sound && !part.Blank && !part.Muted && Math.Abs(point.Y - VolumeY(rect, part.Volume)) <= 5)
                {
                    _drag = Drag.Volume;
                    _index = index;
                    return;
                }
                _drag = Drag.Pending;
                _index = index;
                return;
            case Lane.Music:
                if (MusicRect() is { } music && music.Contains(point) && edit.Music is { } song)
                {
                    _index = 0;
                    if (Math.Abs(point.Y - VolumeY(music, song.Volume)) <= 5)
                    {
                        _drag = Drag.MusicVolume;
                        return;
                    }
                    _drag = Drag.Pending;
                    _valueAtPress = song.Offset;
                    return;
                }
                break;
        }
        // An empty place: nothing selected, the playhead there.
        _drag = Drag.Playhead;
        host.ClearSelection();
        host.Seek(TimeAt(point.X));
    }

    /// <summary>The part edge under the pointer on V1 or A1: the part and whether it is its end.</summary>
    private (int Index, bool End)? EdgeAt(Point point)
    {
        if (Host is not { } host)
            return null;
        var edit = host.Edit;
        var starts = edit.PartStarts();
        (int, bool)? best = null;
        var nearest = EdgeGrab;
        for (var i = 0; i < edit.Parts.Count; i++)
        {
            var x0 = XOf(starts[i]);
            var x1 = XOf(starts[i] + edit.Parts[i].Length);
            if (x1 - x0 < 3 * EdgeGrab)
                continue; // too narrow to tell an edge from the part
            if (Math.Abs(point.X - x1) < nearest)
            {
                nearest = Math.Abs(point.X - x1);
                best = (i, true);
            }
            if (Math.Abs(point.X - x0) < nearest && i > 0 || i == 0 && Math.Abs(point.X - x0) < nearest)
            {
                nearest = Math.Abs(point.X - x0);
                best = (i, false);
            }
        }
        return best;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        _lastPoint = point;
        if (Host is not { } host)
            return;
        if (_drag == Drag.None)
        {
            Cursor = CursorAt(point);
            return;
        }
        var edit = host.Edit;
        var pps = PixelsPerSecond();
        switch (_drag)
        {
            case Drag.Playhead:
                host.Seek(Snap(TimeAt(point.X), host, exceptText: -1));
                break;
            case Drag.Scroll:
                _scroll = _valueAtPress + (point.X - _pressedAt.X) / BarLane.Width * Total;
                ClampScroll();
                InvalidateVisual();
                break;
            case Drag.Edge:
                // The start edge: dragged right, the part takes less from its start (it keeps its place).
                host.DragEdge(_index, _dragEnd, _dragEnd
                    ? TimeAt(point.X) - _startAtPress
                    : _lengthAtPress - (point.X - _pressedAt.X) / pps);
                break;
            case Drag.Volume:
                host.SetPartVolume(_index, VolumeAt(PartRect(_index, SoundLane), point.Y));
                break;
            case Drag.MusicVolume when MusicRect() is { } music:
                host.SetMusicVolume(VolumeAt(music, point.Y));
                break;
            case Drag.Music:
                host.MoveMusic(Math.Max(0, _valueAtPress + (point.X - _pressedAt.X) / pps));
                break;
            case Drag.Text:
                host.MoveText(_index, Snap(Math.Max(0, _startAtPress + (point.X - _pressedAt.X) / pps), host, _index));
                break;
            case Drag.TextStart:
                {
                    var end = _startAtPress + _lengthAtPress;
                    var start = Math.Clamp(Snap(_startAtPress + (point.X - _pressedAt.X) / pps, host, _index), 0, end - ClipRange.MinLength);
                    host.ResizeText(_index, start, end - start);
                    break;
                }
            case Drag.TextEnd:
                {
                    var end = Snap(_startAtPress + _lengthAtPress + (point.X - _pressedAt.X) / pps, host, _index);
                    host.ResizeText(_index, _startAtPress, Math.Max(ClipRange.MinLength, end - _startAtPress));
                    break;
                }
            case Drag.Pending when Math.Abs(point.X - _pressedAt.X) >= DragThreshold:
                if (_pendingLane == Lane.Music)
                {
                    _drag = Drag.Music;
                    host.SelectMusic();
                }
                else if (_pendingLane == Lane.Video && _index >= 0 && edit.Parts.Count > 1)
                {
                    _drag = Drag.Move;
                    host.SelectPart(_index, ClipSelect.Only);
                }
                break;
            case Drag.Move:
                _moveTo = MoveTarget(point.X);
                InvalidateVisual();
                break;
        }
    }

    /// <summary>Where the part being moved would go: the place whose middle the pointer passed.</summary>
    private int MoveTarget(double x)
    {
        var edit = Host!.Edit;
        var starts = edit.PartStarts();
        var seconds = TimeAt(x);
        var target = 0;
        for (var i = 0; i < edit.Parts.Count; i++)
        {
            if (i == _index)
                continue;
            if (seconds > starts[i] + edit.Parts[i].Length / 2)
                target = i < _index ? i + 1 : i;
        }
        return Math.Clamp(target, 0, edit.Parts.Count - 1);
    }

    /// <summary>
    /// <paramref name="seconds"/>, or the cut, the playhead or a text's end within <see cref="SnapDistance"/> pixels of
    /// it (not text <paramref name="exceptText"/>'s own).
    /// </summary>
    private double Snap(double seconds, IEditorTimelineHost host, int exceptText)
    {
        var pps = PixelsPerSecond();
        var edit = host.Edit;
        var points = edit.PartStarts().Append(edit.Length).Append(0.0);
        if (_drag != Drag.Playhead)
            points = points.Append(host.Playhead);
        points = points.Concat(edit.Texts.Where((_, i) => i != exceptText).SelectMany(t => new[] { t.Start, t.Start + t.Length }));
        var best = seconds;
        var nearest = SnapDistance / pps;
        foreach (var point in points)
        {
            if (Math.Abs(point - seconds) < nearest)
            {
                nearest = Math.Abs(point - seconds);
                best = point;
            }
        }
        return best;
    }

    private Cursor? CursorAt(Point point)
    {
        if (Host is not { } host)
            return null;
        var edit = host.Edit;
        switch (LaneAt(point))
        {
            case Lane.Ruler:
                return IbeamCursor;
            case Lane.Text:
                foreach (var text in edit.Texts)
                {
                    var rect = TextRect(text);
                    if (!rect.Inflate(new Thickness(2, 0)).Contains(point))
                        continue;
                    return Math.Abs(point.X - rect.Right) <= EdgeGrab || Math.Abs(point.X - rect.X) <= EdgeGrab ? SizeWestEastCursor : SizeAllCursor;
                }
                return null;
            case Lane.Video or Lane.Sound:
                for (var i = 0; i < edit.Parts.Count - 1; i++)
                {
                    if (TransitionButton(i).Inflate(3).Contains(point))
                        return HandCursor;
                }
                if (EdgeAt(point) is not null)
                    return SizeWestEastCursor;
                var index = PartAt(TimeAt(point.X));
                if (index >= 0 && LaneAt(point) == Lane.Sound && !edit.Parts[index].Blank
                    && Math.Abs(point.Y - VolumeY(PartRect(index, SoundLane), edit.Parts[index].Volume)) <= 5)
                    return SizeNorthSouthCursor;
                if (index >= 0 && edit.Parts[index].Blank)
                    return HandCursor;
                return index >= 0 && LaneAt(point) == Lane.Video ? SizeAllCursor : null;
            case Lane.Music when MusicRect() is { } music && music.Contains(point) && edit.Music is { } song:
                return Math.Abs(point.Y - VolumeY(music, song.Volume)) <= 5 ? SizeNorthSouthCursor : SizeAllCursor;
            default:
                return null;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (Host is { } host)
        {
            switch (_drag)
            {
                case Drag.Pending when _pendingLane == Lane.Music:
                    host.SelectMusic();
                    break;
                case Drag.Pending when _index >= 0:
                    host.SelectPart(_index, _pendingMode);
                    break;
                case Drag.Move when _moveTo >= 0 && _moveTo != _index:
                    host.MovePart(_index, _moveTo);
                    break;
            }
            if (_drag is not (Drag.None or Drag.Pending or Drag.Playhead or Drag.Scroll))
                host.EndDrag();
        }
        _drag = Drag.None;
        _moveTo = -1;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_drag is not (Drag.None or Drag.Pending or Drag.Playhead or Drag.Scroll))
            Host?.EndDrag();
        _drag = Drag.None;
        _moveTo = -1;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        // Ctrl + wheel belongs to the window (everything larger or smaller).
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            var seconds = TimeAt(e.GetPosition(this).X);
            var x = XOf(seconds);
            Zoom = Math.Clamp(Zoom + Math.Sign(e.Delta.Y) * 0.08, 0, 1);
            _scroll = seconds - (x - Content.X) / PixelsPerSecond();
            ClampScroll();
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (!IsZoomed)
            return;
        var delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
        _scroll -= delta * 60 / PixelsPerSecond();
        ClampScroll();
        InvalidateVisual();
        e.Handled = true;
    }

    // ───────────────────────── Files dropped ─────────────────────────

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (!DraggedFiles.Offered(e.DataTransfer))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        e.DragEffects = DragDropEffects.Copy;
        _dropAt = DropTime(e.GetPosition(this).X);
        InvalidateVisual();
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var files = DraggedFiles.Local(e.DataTransfer);
        var x = e.GetPosition(this).X;
        _dropAt = -1;
        InvalidateVisual();
        if (files.Count == 0 || Host is not { } host)
            return;
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
        host.DropFiles(files, DropIndex(x));
    }

    /// <summary>The cut nearest the pointer, where dropped files go.</summary>
    private double DropTime(double x)
    {
        var index = DropIndex(x);
        var edit = Host!.Edit;
        return index >= edit.Parts.Count ? edit.Length : edit.PartStarts()[index];
    }

    private int DropIndex(double x)
    {
        if (Host is not { } host)
            return 0;
        var edit = host.Edit;
        var seconds = TimeAt(x);
        var starts = edit.PartStarts();
        for (var i = 0; i < edit.Parts.Count; i++)
        {
            if (seconds < starts[i] + edit.Parts[i].Length / 2)
                return i;
        }
        return edit.Parts.Count;
    }
}
