// The clip window's editor (its second mode, CapCut-like in Filee's style): the videos and the music in Files, the
// clip on a timeline of tracks (texts, pictures, the parts' sound, the music), the preview with the player, the
// selection's properties, and the Export window. The editing itself is ClipProject; this keeps what is shown in step
// with it, reads the videos (their pictures and loudness), plays the clip and starts the conversion that writes it.

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Engines.Media;
using Microsoft.Extensions.Logging;

namespace Filee.App.ViewModels;

/// <summary>What the editor's selection is.</summary>
public enum EditorSelection
{
    None,
    Parts,
    Text,
    Music,
    Transition,
}

/// <summary>A video or a sound in the editor's Files.</summary>
public sealed partial class EditorMedia(string path, bool isAudio) : ObservableObject
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public bool IsAudio { get; } = isAudio;
    public bool IsVideo => !IsAudio;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    private double _duration;

    /// <summary>"1920×1080 · 60 fps", or the kind of sound.</summary>
    [ObservableProperty] private string _details = "";

    [ObservableProperty] private Bitmap? _thumbnail;

    /// <summary>On the timeline (a video) or under it (the music).</summary>
    [ObservableProperty] private bool _isUsed;

    /// <summary>Just dropped in: its card flies in.</summary>
    [ObservableProperty] private bool _justAdded;

    public string DurationText => ClipRange.Display(Duration);

    internal int Width { get; set; }
    internal int Height { get; set; }
    internal double FrameRate { get; set; }
    internal bool HasAudio { get; set; }

    /// <summary>Small pictures over the whole video, for the timeline.</summary>
    internal Bitmap?[] Strip { get; set; } = [];

    /// <summary>The sound's loudness over the whole file.</summary>
    internal float[] Loudness { get; set; } = [];
}

public sealed partial class ClipEditorViewModel : ObservableObject, IEditorTimelineHost, IEditorPreviewHost, IDisposable
{
    /// <summary>Small pictures taken over each video for the timeline.</summary>
    private const int StripCount = 24;

    private const int StripHeight = 96;

    /// <summary>Steps of a video's loudness drawn under its parts.</summary>
    private const int LoudnessSteps = 600;

    /// <summary>FFmpeg runs at the same time while pictures are taken.</summary>
    private const int ParallelPictures = 3;

    /// <summary>How long a short message stays.</summary>
    private static readonly TimeSpan MessageTime = TimeSpan.FromSeconds(4);

    private readonly string _main;
    private readonly string _mainKey;
    private readonly Preset _tool;
    private readonly ILocalizer _loc;
    private readonly UserDataStore _store;
    private readonly IVideoFrameSource _frames;
    private readonly Action<string, Preset> _export;
    private readonly ILogger? _log;
    private readonly string _workFolder;
    private readonly Dictionary<string, EditorMedia> _media = new(StringComparer.OrdinalIgnoreCase);
    private readonly ClipProject _project;
    private readonly CancellationTokenSource _life = new();
    private readonly List<int> _selectedParts = [];
    private List<ClipPart> _clipboard = [];
    private bool _syncing;
    private bool _closed;

    /// <param name="mainPath">The video the window opened (the conversion's input; parts without a source are of it).</param>
    /// <param name="main">What was read of it.</param>
    /// <param name="start">The clip to start from (the quick cut so far).</param>
    /// <param name="tool">The "Cut a clip" preset: name, output rule and media options.</param>
    /// <param name="export">Starts the conversion that writes the clip (the file, the conversion's preset).</param>
    public ClipEditorViewModel(string mainPath, VideoSummary main, ClipEdit start, Preset tool, ILocalizer loc, UserDataStore store,
        IVideoFrameSource frames, Action<string, Preset> export, string workFolder, ILogger? log = null)
    {
        _main = mainPath;
        _mainKey = Path.GetFullPath(mainPath);
        _tool = tool;
        _loc = loc;
        _store = store;
        _frames = frames;
        _export = export;
        _workFolder = Path.Combine(workFolder, "editor");
        _log = log;
        var media = new EditorMedia(_mainKey, isAudio: false);
        Describe(media, main);
        _media[_mainKey] = media;
        Files.Add(media);
        var edit = start.Clone();
        edit.Editor = true;
        if (edit.Music is { } music && !_media.ContainsKey(Path.GetFullPath(music.Path)))
        {
            var song = new EditorMedia(Path.GetFullPath(music.Path), isAudio: true) { Duration = music.Length, Details = _loc["editor.sound"] };
            _media[song.Path] = song;
            Files.Add(song);
            _ = LoadMusicLoudnessAsync(song.Path, music.Length);
        }
        _project = new ClipProject(edit, DurationOf);
        _project.Changed += OnProjectChanged;
        LoadExportSettings();
        loc.LanguageChanged += OnLanguageChanged;
        Directory.CreateDirectory(_workFolder);
        _ = LoadPicturesAsync(media);
        _ = FindGpuAsync();
        Sync();
        RefreshPreview(immediately: true);
    }

    public event Action? TimelineChanged;
    public event Action? PreviewChanged;

    /// <summary>The clip went to the conversion: the window moves on to the next video or closes.</summary>
    public event Action? Exported;

    /// <summary>Back to the quick cut.</summary>
    public event Action? BackRequested;

    /// <summary>A text's words are to be typed (the properties' text box gets the focus).</summary>
    public event Action? TextEditRequested;

    /// <summary>Files just added (their cards fly in, a check draws itself).</summary>
    public event Action<IReadOnlyList<EditorMedia>>? FilesAdded;

    /// <summary>The videos and the sounds of the clip.</summary>
    public ObservableCollection<EditorMedia> Files { get; } = [];

    public ClipEdit Edit => _project.Edit;
    public IReadOnlyCollection<int> SelectedParts => _selectedParts;
    public string GapText => _loc["clip.close_gap"];
    public string NoMusicText => _loc["clip.no_music_yet"];

    /// <summary>The window's video, by name.</summary>
    public string Title => Path.GetFileName(_main);

    // ───────────────────────── State shown ─────────────────────────

    /// <summary>Seconds into the clip: where the blade cuts, playing starts and texts are added.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    private double _playhead;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    [NotifyPropertyChangedFor(nameof(LengthText))]
    private double _length;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPartSelection))]
    [NotifyPropertyChangedFor(nameof(HasTextSelection))]
    [NotifyPropertyChangedFor(nameof(HasMusicSelection))]
    [NotifyPropertyChangedFor(nameof(HasTransitionSection))]
    [NotifyPropertyChangedFor(nameof(HasNoSelection))]
    [NotifyPropertyChangedFor(nameof(IsMusicSelected))]
    private EditorSelection _selection;

    [ObservableProperty] private int _selectedText = -1;
    [ObservableProperty] private int _selectedTransition = -1;
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canRedo;
    [ObservableProperty] private bool _hasGaps;
    [ObservableProperty] private bool _hasMusic;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayToolTip))]
    private bool _isPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BusyText))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isBuffering;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BusyText))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isFindingSilence;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BusyText))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private int _reading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = "";

    /// <summary>The preview fills the window (F, a double click); Esc goes back.</summary>
    [ObservableProperty] private bool _isFullPreview;

    /// <summary>The window is narrow: Files and Properties slide out on demand.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFiles))]
    [NotifyPropertyChangedFor(nameof(ShowProperties))]
    private bool _isNarrow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFiles))]
    private bool _isFilesOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowProperties))]
    private bool _isPropertiesOpen;

    /// <summary>How far the timeline is zoomed in: 0 the whole clip, 1 closest.</summary>
    [ObservableProperty] private double _timelineZoom;

    /// <summary>The question before going back to the quick cut loses what only the editor has.</summary>
    [ObservableProperty] private bool _isBackConfirmOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionTitle))]
    private string _selectionDetail = "";

    public bool ShowFiles => !IsNarrow || IsFilesOpen;
    public bool ShowProperties => !IsNarrow || IsPropertiesOpen;
    public bool HasPartSelection => Selection == EditorSelection.Parts && _selectedParts.Count > 0;
    public bool HasTextSelection => Selection == EditorSelection.Text && SelectedText >= 0;
    public bool HasMusicSelection => Selection == EditorSelection.Music && HasMusic;
    public bool HasNoSelection => Selection == EditorSelection.None || Selection == EditorSelection.Parts && _selectedParts.Count == 0;
    public bool IsMusicSelected => Selection == EditorSelection.Music;
    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public bool IsBusy => IsBuffering || IsFindingSilence || Reading > 0;
    public string BusyText => IsBuffering ? _loc["clip.preparing"]
        : IsFindingSilence ? _loc["clip.finding_silence"]
        : Reading > 0 ? _loc["editor.reading"]
        : "";

    public string TimeText => $"{ClipRange.Display(Playhead)} / {ClipRange.Display(Length)}";
    public string LengthText => ClipRange.Display(Length);
    public string PlayToolTip => _loc[IsPlaying ? "clip.pause" : "clip.play"] + " (Space, K)";

    /// <summary>What the properties are of: "Part 2 of 5", "Text", "Music", "Transition", "Clip".</summary>
    public string SelectionTitle => Selection switch
    {
        EditorSelection.Parts when _selectedParts.Count > 1 => _loc.Format("clip.selected_count", _selectedParts.Count),
        EditorSelection.Parts when _selectedParts.Count == 1 => _loc.Format("editor.part_of", _selectedParts[0] + 1, _project.Parts.Count),
        EditorSelection.Text => _loc["editor.text"],
        EditorSelection.Music => _loc["editor.music"],
        EditorSelection.Transition => _loc.Format("editor.transition_between", SelectedTransition + 1, SelectedTransition + 2),
        _ => _loc["editor.clip"],
    };

    // ───────────────────────── Files ─────────────────────────

    /// <summary>How long the video <paramref name="source"/> is (null: the window's video).</summary>
    public double DurationOf(string? source) => MediaOf(source)?.Duration ?? 0;

    private EditorMedia? MediaOf(string? source) =>
        _media.TryGetValue(source is null ? _mainKey : Path.GetFullPath(source), out var media) ? media : null;

    /// <summary>The part's video: null for the window's own, else its full path.</summary>
    private string? SourceKey(string path) => string.Equals(Path.GetFullPath(path), _mainKey, StringComparison.OrdinalIgnoreCase) ? null : Path.GetFullPath(path);

    private void Describe(EditorMedia media, VideoSummary video)
    {
        media.Duration = video.Duration;
        media.Width = video.Width;
        media.Height = video.Height;
        media.FrameRate = video.FrameRate;
        media.HasAudio = video.HasAudio;
        media.Details = $"{video.Width}×{video.Height} · {Math.Round(video.FrameRate).ToString(CultureInfo.CurrentCulture)} {_loc["editor.fps"]}";
    }

    /// <summary>
    /// Adds files to Files: videos and sounds (anything else is skipped with a message). With
    /// <paramref name="toTimeline"/> the videos also go on the timeline (at part <paramref name="at"/>, else at the
    /// end) and a sound becomes the music. Returns the files added.
    /// </summary>
    public async Task<IReadOnlyList<EditorMedia>> AddFilesAsync(IReadOnlyList<string> paths, bool toTimeline, int? at = null)
    {
        var added = new List<EditorMedia>();
        var ready = new List<EditorMedia>();
        var failed = 0;
        Reading++;
        try
        {
            foreach (var path in paths.Where(p => !Directory.Exists(p)))
            {
                var full = Path.GetFullPath(path);
                if (_media.TryGetValue(full, out var known))
                {
                    ready.Add(known);
                    continue;
                }
                var media = await ReadMediaAsync(full);
                if (_closed)
                    return added;
                if (media is null)
                {
                    failed++;
                    continue;
                }
                _media[full] = media;
                Files.Add(media);
                media.JustAdded = true;
                added.Add(media);
                ready.Add(media);
                if (media.IsVideo)
                    _ = LoadPicturesAsync(media);
            }
        }
        finally
        {
            Reading--;
        }
        if (toTimeline && ready.Count > 0)
            PutOnTimeline(ready, at);
        if (failed > 0)
            ShowMessage(_loc.Format("editor.cannot_open", failed));
        else if (added.Count > 0)
            ShowMessage(_loc.Format("editor.added", added.Count));
        if (added.Count > 0)
            FilesAdded?.Invoke(added);
        return added;
    }

    private async Task<EditorMedia?> ReadMediaAsync(string path)
    {
        // A file known to be something else (a document, a picture) isn't read at all.
        if (Filee.Core.Formats.FormatRegistry.Detect(path)?.Category is { } category
            && category is not (Filee.Core.Formats.FormatCategory.Video or Filee.Core.Formats.FormatCategory.Audio))
            return null;
        try
        {
            var video = await _frames.ProbeAsync(path, _life.Token);
            if (video.Duration > 0)
            {
                var media = new EditorMedia(path, isAudio: false);
                Describe(media, video);
                return media;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            _log?.LogDebug(ex, "{File} is not a video for the editor", path);
        }
        try
        {
            var length = await _frames.ReadSoundLengthAsync(path, _life.Token);
            if (length > 0)
                return new EditorMedia(path, isAudio: true) { Duration = length, Details = _loc["editor.sound"] };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            _log?.LogDebug(ex, "{File} has no sound for the editor either", path);
        }
        return null;
    }

    /// <summary>Puts videos on the timeline (one undo step) and a sound under it as the music.</summary>
    private void PutOnTimeline(IReadOnlyList<EditorMedia> media, int? at)
    {
        StopPlaying();
        var videos = media.Where(m => m.IsVideo).ToList();
        var sound = media.FirstOrDefault(m => m.IsAudio);
        _project.Batch(() =>
        {
            var index = Math.Clamp(at ?? _project.Parts.Count, 0, _project.Parts.Count);
            foreach (var video in videos)
                _project.AddVideo(SourceKey(video.Path), index++);
            if (sound is not null)
                _project.SetMusic(new ClipMusic { Path = sound.Path, Length = sound.Duration, Offset = 0 });
        });
        if (sound is not null)
            _ = LoadMusicLoudnessAsync(sound.Path, sound.Duration);
    }

    /// <summary>A card's "+" or a double click: a video to the end of the timeline, a sound as the music.</summary>
    [RelayCommand]
    public void UseMedia(EditorMedia? media)
    {
        if (media is not null)
            PutOnTimeline([media], media.IsVideo ? _project.IndexAt(Playhead) + 1 : null);
    }

    /// <summary>The pictures and the loudness of a video, a few at a time, each shown as soon as it is there.</summary>
    private async Task LoadPicturesAsync(EditorMedia media)
    {
        var ct = _life.Token;
        var folder = Path.Combine(_workFolder, "strip-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        media.Strip = new Bitmap?[StripCount];
        _ = LoadLoudnessAsync(media, ct);
        using var gate = new SemaphoreSlim(ParallelPictures);
        // The middle picture first: it is the card's.
        var order = Enumerable.Range(0, StripCount).OrderBy(i => Math.Abs(i - StripCount / 2)).ToList();
        var tasks = order.Select(async i =>
        {
            await gate.WaitAsync(ct);
            string? file = null;
            try
            {
                var output = Path.Combine(folder, $"strip_{i:00}.jpg");
                if (await _frames.WriteFrameAsync(media.Path, VideoFrames.ThumbnailTime(i, StripCount, media.Duration), output, StripHeight, ct))
                    file = output;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                _log?.LogDebug(ex, "No timeline picture {Index} of {File}", i, media.Path);
            }
            finally
            {
                gate.Release();
            }
            if (file is null || ct.IsCancellationRequested || _closed || Load(file) is not { } bitmap)
                return;
            media.Strip[i] = bitmap;
            media.Thumbnail ??= bitmap;
            TimelineChanged?.Invoke();
            if (Picture is null && !IsPlaying)
                RefreshPreview(immediately: false);
        }).ToList();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // The editor closed.
        }
    }

    private async Task LoadLoudnessAsync(EditorMedia media, CancellationToken ct)
    {
        if (!media.HasAudio)
            return;
        try
        {
            media.Loudness = await _frames.ReadLoudnessAsync(media.Path, 0, media.Duration, LoudnessSteps, _workFolder, ct);
            TimelineChanged?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogDebug(ex, "No loudness of {File}", media.Path);
        }
    }

    private async Task LoadMusicLoudnessAsync(string path, double length)
    {
        try
        {
            var loudness = await _frames.ReadLoudnessAsync(path, 0, length, LoudnessSteps, _workFolder, _life.Token);
            if (_media.TryGetValue(Path.GetFullPath(path), out var media))
                media.Loudness = loudness;
            TimelineChanged?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogDebug(ex, "No loudness of the music {File}", path);
        }
    }

    public Bitmap? PictureAt(string? source, double seconds)
    {
        if (MediaOf(source) is not { Strip.Length: > 0 } media || media.Duration <= 0)
            return null;
        var strip = media.Strip;
        var index = Math.Clamp((int)(seconds / media.Duration * strip.Length), 0, strip.Length - 1);
        for (var d = 0; d < strip.Length; d++)
        {
            if (index - d >= 0 && strip[index - d] is { } before)
                return before;
            if (index + d < strip.Length && strip[index + d] is { } after)
                return after;
        }
        return null;
    }

    public IReadOnlyList<float> LoudnessOf(string? source) => MediaOf(source)?.Loudness ?? [];

    public IReadOnlyList<float> MusicLoudness =>
        _project.Music is { } music && _media.TryGetValue(Path.GetFullPath(music.Path), out var media) ? media.Loudness : [];

    // ───────────────────────── After each change ─────────────────────────

    private void OnProjectChanged()
    {
        // Indices past the end after parts went away.
        _selectedParts.RemoveAll(i => i >= _project.Parts.Count);
        if (SelectedText >= _project.Texts.Count)
            SelectedText = -1;
        if (SelectedTransition >= _project.Parts.Count - 1)
            SelectedTransition = -1;
        Sync();
        RefreshPreview(immediately: false);
    }

    /// <summary>Everything shown of the clip, after a change.</summary>
    private void Sync()
    {
        _syncing = true;
        try
        {
            Length = _project.Length;
            if (Playhead > Length)
                Playhead = Length;
            CanUndo = _project.CanUndo;
            CanRedo = _project.CanRedo;
            HasGaps = _project.HasGaps;
            HasMusic = _project.Music is not null;
            if (Selection == EditorSelection.Music && !HasMusic)
                Selection = EditorSelection.None;
            foreach (var media in Files)
                media.IsUsed = media.IsVideo
                    ? _project.Parts.Any(p => !p.Blank && string.Equals(p.Source ?? _mainKey, media.Path, StringComparison.OrdinalIgnoreCase))
                    : _project.Music is { } music && string.Equals(Path.GetFullPath(music.Path), media.Path, StringComparison.OrdinalIgnoreCase);
            UpdateSelectionDetail();
            foreach (var name in PropertyNames)
                OnPropertyChanged(name);
        }
        finally
        {
            _syncing = false;
        }
        TimelineChanged?.Invoke();
        UpdateExportSummary();
    }

    private void UpdateSelectionDetail()
    {
        SelectionDetail = Selection switch
        {
            EditorSelection.Parts when _selectedParts.Count == 1 && _selectedParts[0] < _project.Parts.Count
                => PartDetail(_project.Parts[_selectedParts[0]]),
            EditorSelection.Text when SelectedText >= 0 && SelectedText < _project.Texts.Count
                => $"{ClipRange.Display(_project.Texts[SelectedText].Start)} · {ClipRange.Display(_project.Texts[SelectedText].Length)}",
            EditorSelection.Music when _project.Music is { } music => Path.GetFileName(music.Path),
            EditorSelection.None or EditorSelection.Parts => $"{_project.Parts.Count(p => !p.Blank)} · {LengthText}",
            _ => "",
        };
        OnPropertyChanged(nameof(SelectionTitle));
    }

    private string PartDetail(ClipPart part) => part.Blank
        ? _loc["editor.pause"] + " · " + ClipRange.Display(part.Length)
        : $"{Path.GetFileName(part.Source ?? _main)} · {ClipRange.Display(part.Length)}";

    // ───────────────────────── Selection ─────────────────────────

    public void SelectPart(int index, ClipSelect mode)
    {
        if (index < 0 || index >= _project.Parts.Count)
            return;
        if (Selection != EditorSelection.Parts)
            _selectedParts.Clear();
        switch (mode)
        {
            case ClipSelect.Toggle:
                if (!_selectedParts.Remove(index))
                    _selectedParts.Add(index);
                break;
            case ClipSelect.Range when _selectedParts.Count > 0:
                var anchor = _selectedParts[^1];
                for (var i = Math.Min(anchor, index); i <= Math.Max(anchor, index); i++)
                {
                    if (!_selectedParts.Contains(i))
                        _selectedParts.Add(i);
                }
                break;
            default:
                _selectedParts.Clear();
                _selectedParts.Add(index);
                break;
        }
        _selectedParts.Sort();
        SetSelection(EditorSelection.Parts);
    }

    public void SelectText(int index)
    {
        if (index < 0 || index >= _project.Texts.Count)
            return;
        SelectedText = index;
        SetSelection(EditorSelection.Text);
    }

    public void SelectMusic()
    {
        if (HasMusic)
            SetSelection(EditorSelection.Music);
    }

    public void SelectTransition(int index)
    {
        if (index < 0 || index >= _project.Parts.Count - 1)
            return;
        SelectedTransition = index;
        SetSelection(EditorSelection.Transition);
    }

    [RelayCommand]
    public void ClearSelection()
    {
        _selectedParts.Clear();
        SetSelection(EditorSelection.None);
    }

    [RelayCommand]
    public void SelectAll()
    {
        _selectedParts.Clear();
        _selectedParts.AddRange(Enumerable.Range(0, _project.Parts.Count));
        SetSelection(EditorSelection.Parts);
    }

    private void SetSelection(EditorSelection selection)
    {
        if (selection != EditorSelection.Text)
            SelectedText = -1;
        if (selection != EditorSelection.Transition)
            SelectedTransition = -1;
        if (selection != EditorSelection.Parts)
            _selectedParts.Clear();
        Selection = selection;
        _syncing = true;
        try
        {
            UpdateSelectionDetail();
            foreach (var name in PropertyNames)
                OnPropertyChanged(name);
            OnPropertyChanged(nameof(HasPartSelection));
            OnPropertyChanged(nameof(HasNoSelection));
            OnPropertyChanged(nameof(HasTransitionSection));
        }
        finally
        {
            _syncing = false;
        }
        TimelineChanged?.Invoke();
        PreviewChanged?.Invoke();
    }

    /// <summary>The parts the part actions apply to: the selection, else the one at the playhead.</summary>
    public IReadOnlyList<int> TargetParts =>
        Selection == EditorSelection.Parts && _selectedParts.Count > 0 ? _selectedParts.ToList()
        : _project.IndexAt(Playhead) is >= 0 and var at ? [at] : [];

    // ───────────────────────── Timeline (host) ─────────────────────────

    public void Seek(double seconds)
    {
        var wasPlaying = IsPlaying;
        StopPlaying();
        Playhead = Math.Clamp(seconds, 0, Length);
        RefreshPreview(immediately: false);
        TimelineChanged?.Invoke();
        if (wasPlaying)
            _ = PlayAsync();
    }

    public void MovePart(int from, int to)
    {
        StopPlaying();
        if (!_project.Move(from, to))
            return;
        _selectedParts.Clear();
        _selectedParts.Add(to);
        SetSelection(EditorSelection.Parts);
    }

    public void DragEdge(int index, bool end, double length)
    {
        StopPlaying();
        _project.TrimEdge(index, end, length);
        // The picture at the edge being dragged.
        var starts = _project.Starts();
        if (index < _project.Parts.Count)
            Playhead = Math.Clamp(end ? starts[index] + _project.Parts[index].Length - 0.01 : starts[index], 0, Length);
        RefreshPreview(immediately: false);
    }

    public void MoveText(int index, double start) =>
        _project.ChangeText(index, t => t.Start = start, join: $"text move {index}");

    public void ResizeText(int index, double start, double length) =>
        _project.ChangeText(index, t =>
        {
            t.Start = start;
            t.Length = length;
        }, join: $"text size {index}");

    public void MoveMusic(double offset) => _project.ChangeMusic(m => m.Offset = offset, join: "music move");

    public void SetPartVolume(int index, double volume)
    {
        var targets = _selectedParts.Contains(index) ? _selectedParts.ToList() : [index];
        _project.ChangeParts(targets, p =>
        {
            p.Volume = volume;
            p.Muted = false;
        }, join: "volume line");
    }

    public void SetMusicVolume(double volume) => _project.ChangeMusic(m => m.Volume = volume, join: "music volume line");

    public void CloseGap(int index) => _project.CloseGap(index);

    public void EndDrag() => _project.Seal();

    public void DropFiles(IReadOnlyList<string> files, int at) => _ = AddFilesAsync(files, toTimeline: true, at);

    // ───────────────────────── Editing ─────────────────────────

    /// <summary>The blade (C): cuts the part at the playhead in two.</summary>
    [RelayCommand]
    public void Split()
    {
        StopPlaying();
        if (!_project.Split(Playhead))
            ShowMessage(_loc["clip.cannot_cut"]);
    }

    /// <summary>Delete: parts leave a gap (Shift: what follows closes up); a text, the music or a transition goes.</summary>
    public void Delete(bool closeUp)
    {
        StopPlaying();
        switch (Selection)
        {
            case EditorSelection.Text when SelectedText >= 0:
                _project.RemoveTexts([SelectedText]);
                ClearSelection();
                return;
            case EditorSelection.Music:
                _project.SetMusic(null);
                ClearSelection();
                return;
            case EditorSelection.Transition when SelectedTransition >= 0:
                _project.ChangePart(SelectedTransition, p => p.Transition = ClipTransitionKind.Cut);
                return;
        }
        var targets = TargetParts;
        if (targets.Count == 0)
            return;
        // Gaps selected close; parts that play are cut out.
        if (targets.All(i => _project.Parts[i].Blank))
            closeUp = true;
        if (!_project.Remove(targets, closeUp))
        {
            ShowMessage(_loc["clip.last_piece"]);
            return;
        }
        if (closeUp)
            ClearSelection();
        else
            SetSelection(EditorSelection.Parts);
    }

    [RelayCommand]
    private void DeleteSelected() => Delete(closeUp: false);

    [RelayCommand]
    private void DeleteRipple() => Delete(closeUp: true);

    [RelayCommand]
    public void CloseGaps() => _project.CloseGaps();

    /// <summary>Ctrl+C: the selected parts (or the one at the playhead) are kept to paste.</summary>
    [RelayCommand]
    public void Copy()
    {
        if (Selection == EditorSelection.Text && SelectedText >= 0)
        {
            _textClipboard = _project.Texts[SelectedText].Clone();
            _clipboard = [];
            return;
        }
        _textClipboard = null;
        _clipboard = TargetParts.Select(i => _project.Parts[i].Clone()).ToList();
    }

    private ClipText? _textClipboard;

    /// <summary>Ctrl+V: what was copied goes in after the part at the playhead (a text, at the playhead).</summary>
    [RelayCommand]
    public void Paste()
    {
        StopPlaying();
        if (_textClipboard is { } text)
        {
            var copy = text.Clone();
            copy.Start = Playhead;
            SelectText(_project.AddText(copy));
            return;
        }
        if (_clipboard.Count == 0)
            return;
        var at = _project.IndexAt(Playhead) + 1;
        var first = _project.Insert(_clipboard, at);
        SelectRange(first, _clipboard.Count);
    }

    /// <summary>Ctrl+D: copies of the selected parts right after them.</summary>
    [RelayCommand]
    public void Duplicate()
    {
        StopPlaying();
        if (Selection == EditorSelection.Text && SelectedText >= 0)
        {
            var copy = _project.Texts[SelectedText].Clone();
            copy.Start = Math.Min(Length - ClipRange.MinLength, copy.Start + copy.Length);
            SelectText(_project.AddText(copy));
            return;
        }
        var targets = TargetParts;
        if (targets.Count == 0)
            return;
        var parts = targets.Select(i => _project.Parts[i]).ToList();
        var first = _project.Insert(parts, targets[^1] + 1);
        SelectRange(first, parts.Count);
    }

    private void SelectRange(int first, int count)
    {
        if (first < 0)
            return;
        _selectedParts.Clear();
        _selectedParts.AddRange(Enumerable.Range(first, count));
        SetSelection(EditorSelection.Parts);
    }

    /// <summary>A still of the picture at the playhead, two seconds long.</summary>
    [RelayCommand]
    public void Freeze()
    {
        StopPlaying();
        var index = _project.Freeze(Playhead);
        if (index < 0)
            ShowMessage(_loc["editor.cannot_freeze"]);
        else
        {
            _selectedParts.Clear();
            _selectedParts.Add(index);
            SetSelection(EditorSelection.Parts);
        }
    }

    /// <summary>The selected parts play backwards (again: forwards).</summary>
    [RelayCommand]
    public void ToggleReverse()
    {
        var targets = TargetParts.Where(i => !_project.Parts[i].Blank && !_project.Parts[i].IsStill).ToList();
        if (targets.Count == 0)
            return;
        var reverse = !_project.Parts[targets[0]].Reverse;
        _project.ChangeParts(targets, p => p.Reverse = reverse);
    }

    /// <summary>A text of a style at the playhead, selected for typing.</summary>
    [RelayCommand]
    public void AddText(string? style)
    {
        StopPlaying();
        var kind = Enum.TryParse<ClipTextStyle>(style, true, out var parsed) ? parsed : ClipTextStyle.Title;
        var key = kind switch
        {
            ClipTextStyle.Caption => "editor.caption_sample",
            ClipTextStyle.Label => "editor.label_sample",
            _ => "editor.title_sample",
        };
        var start = Playhead >= Length - ClipRange.MinLength ? 0 : Playhead;
        SelectText(_project.AddText(ClipText.Of(kind, _loc[key], start, Math.Min(3, Math.Max(1, Length - start)))));
        TextEditRequested?.Invoke();
    }

    [RelayCommand]
    public void Undo()
    {
        StopPlaying();
        _project.Undo();
    }

    [RelayCommand]
    public void Redo()
    {
        StopPlaying();
        _project.Redo();
    }

    /// <summary>Takes the silent stretches out of the parts (each video read once).</summary>
    [RelayCommand]
    public async Task RemoveSilenceAsync()
    {
        if (IsFindingSilence)
            return;
        StopPlaying();
        IsFindingSilence = true;
        try
        {
            var silences = new Dictionary<string, IReadOnlyList<ClipRange>>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in _project.Parts.Where(p => !p.Blank).Select(p => p.Source ?? _mainKey).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var media = MediaOf(source == _mainKey ? null : source);
                silences[source] = media is { HasAudio: true }
                    ? await _frames.FindSilencesAsync(media.Path, 0, media.Duration, _life.Token)
                    : [];
            }
            var removed = _project.RemoveSilences(source => silences.TryGetValue(source ?? _mainKey, out var found) ? found : []);
            ShowMessage(removed > 0 ? _loc.Format("clip.silence_removed", removed) : _loc["clip.no_silence"]);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogWarning(ex, "Could not find the silences of the clip");
            ShowMessage(_loc["clip.no_silence"]);
        }
        finally
        {
            IsFindingSilence = false;
        }
    }

    /// <summary>Back to the quick cut: at once when the clip is one, else after asking.</summary>
    [RelayCommand]
    public void Back()
    {
        StopPlaying();
        if (ToQuickCut() is not null)
            BackRequested?.Invoke();
        else
            IsBackConfirmOpen = true;
    }

    [RelayCommand]
    private void ConfirmBack()
    {
        IsBackConfirmOpen = false;
        BackRequested?.Invoke();
    }

    [RelayCommand]
    private void StayInEditor() => IsBackConfirmOpen = false;

    /// <summary>The clip as a quick cut of the window's video, or null when it uses what only the editor has.</summary>
    public ClipCutter? ToQuickCut() => ClipCutter.FromEdit(DurationOf(null), _project.ToEdit());

    /// <summary>The clip as it is (for a quick cut that starts over from it).</summary>
    public ClipEdit ToEdit() => _project.ToEdit();

    [RelayCommand]
    private void ToggleFiles()
    {
        IsFilesOpen = !IsFilesOpen;
        if (IsFilesOpen)
            IsPropertiesOpen = false;
    }

    [RelayCommand]
    private void ToggleProperties()
    {
        IsPropertiesOpen = !IsPropertiesOpen;
        if (IsPropertiesOpen)
            IsFilesOpen = false;
    }

    [RelayCommand]
    private void ZoomTimelineIn() => TimelineZoom = Math.Min(1, TimelineZoom + 0.15);

    [RelayCommand]
    private void ZoomTimelineOut() => TimelineZoom = Math.Max(0, TimelineZoom - 0.15);

    // ───────────────────────── Messages and the end ─────────────────────────

    private void ShowMessage(string text)
    {
        Message = text;
        _ = ClearMessageLaterAsync(text);
    }

    private async Task ClearMessageLaterAsync(string text)
    {
        try
        {
            await Task.Delay(MessageTime, _life.Token);
        }
        catch (OperationCanceledException)
        {
        }
        if (Message == text)
            Message = "";
    }

    private static Bitmap? Load(string file)
    {
        try
        {
            return new Bitmap(file);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The work folder goes when the window closes.
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var media in Files.Where(m => m.IsAudio))
                media.Details = _loc["editor.sound"];
            BuildChoices();
            Sync();
            OnPropertyChanged(nameof(BusyText));
            OnPropertyChanged(nameof(PlayToolTip));
            OnPropertyChanged(nameof(GpuText));
        });
    }

    public void Dispose()
    {
        if (_closed)
            return;
        _closed = true;
        StopPlaying();
        _life.Cancel();
        _previewCts?.Cancel();
        _loc.LanguageChanged -= OnLanguageChanged;
        _project.Changed -= OnProjectChanged;
        DisposePictures();
        foreach (var media in Files)
        {
            foreach (var bitmap in media.Strip)
                bitmap?.Dispose();
            media.Strip = [];
            media.Thumbnail = null;
        }
        _life.Dispose();
    }
}
