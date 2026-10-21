// What the editor's timeline and preview ask of the editor (ClipEditorViewModel): the clip, its pictures and sound,
// the selection, and every edit a drag or a click makes. The controls only draw and hit-test.

using Avalonia.Media.Imaging;
using Filee.Core.Presets;

namespace Filee.App.Controls;

/// <summary>The editor as the timeline sees it.</summary>
public interface IEditorTimelineHost
{
    /// <summary>Raised when anything the timeline shows changed (the clip, the selection, pictures, the playhead).</summary>
    event Action? TimelineChanged;

    /// <summary>The clip as it is now (read only).</summary>
    ClipEdit Edit { get; }

    /// <summary>Seconds into the clip.</summary>
    double Playhead { get; }

    IReadOnlyCollection<int> SelectedParts { get; }
    int SelectedText { get; }
    bool IsMusicSelected { get; }

    /// <summary>The part whose transition into the next one is selected, or -1.</summary>
    int SelectedTransition { get; }

    /// <summary>A small picture of <paramref name="source"/> near <paramref name="seconds"/>, or null while none is there.</summary>
    Bitmap? PictureAt(string? source, double seconds);

    /// <summary>The loudness of <paramref name="source"/>'s sound over the whole video (0 to 1), empty without one.</summary>
    IReadOnlyList<float> LoudnessOf(string? source);

    double DurationOf(string? source);

    /// <summary>The loudness of the music's whole file.</summary>
    IReadOnlyList<float> MusicLoudness { get; }

    string GapText { get; }
    string NoMusicText { get; }

    void Seek(double seconds);
    void SelectPart(int index, ClipSelect mode);
    void SelectText(int index);
    void SelectMusic();
    void SelectTransition(int index);
    void ClearSelection();
    void MovePart(int from, int to);
    /// <summary>An edge of part <paramref name="index"/> dragged so that it plays <paramref name="length"/> seconds.</summary>
    void DragEdge(int index, bool end, double length);
    void MoveText(int index, double start);
    void ResizeText(int index, double start, double length);
    void MoveMusic(double offset);
    void SetPartVolume(int index, double volume);
    void SetMusicVolume(double volume);
    void CloseGap(int index);

    /// <summary>A drag ended: the next change is its own undo step.</summary>
    void EndDrag();

    /// <summary>Files dropped on the timeline, to go in as part <paramref name="at"/> on.</summary>
    void DropFiles(IReadOnlyList<string> files, int at);
}

/// <summary>The editor as the preview sees it.</summary>
public interface IEditorPreviewHost
{
    /// <summary>Raised when the picture, the frame or the texts changed.</summary>
    event Action? PreviewChanged;

    /// <summary>A frame of a video (placed by <see cref="PicturePart"/>), or the clip as it plays (<see cref="IsComposed"/>).</summary>
    Bitmap? Picture { get; }

    /// <summary>The picture already is the whole frame (the clip playing).</summary>
    bool IsComposed { get; }

    /// <summary>The frame's size in pixels (its shape).</summary>
    (int Width, int Height) Canvas { get; }

    ClipFit Fit { get; }

    /// <summary>The part the picture is of: its zoom and focus place it; null for black (a pause).</summary>
    ClipPart? PicturePart { get; }

    /// <summary>The video's picture size as shown, for placing it.</summary>
    (int Width, int Height) PictureSize { get; }

    /// <summary>The texts shown now, with their index in the clip.</summary>
    IReadOnlyList<(int Index, ClipText Text)> VisibleTexts { get; }

    int SelectedText { get; }

    /// <summary>True when a part is selected whose picture is larger than the frame: it can be dragged.</summary>
    bool CanMovePicture { get; }

    void SelectText(int index);
    void MoveText(int index, double x, double y);
    void MovePicture(double focusX, double focusY);
    void EndDrag();
    void ToggleFullPreview();
    void EditText(int index);
}
