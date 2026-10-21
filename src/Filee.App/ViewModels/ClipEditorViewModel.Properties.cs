// The editor's properties panel: what the selection is set to (a part's sound, speed, transition and frame; a text's
// words and look; the music's volume; the clip's frame, fades and sound), each change one undo step (a slider dragged
// too).

using System.Globalization;
using Filee.Core.Presets;

namespace Filee.App.ViewModels;

public sealed partial class ClipEditorViewModel
{
    /// <summary>Every property below, told again after each change.</summary>
    private static readonly string[] PropertyNames =
    [
        nameof(PartVolume), nameof(PartVolumeText), nameof(PartMuted), nameof(PartSpeed), nameof(PartSpeedEnd), nameof(PartReverse),
        nameof(PartZoom), nameof(PartZoomText), nameof(IsStillSelected), nameof(PartHold), nameof(PartHoldText), nameof(IsMovingSelected),
        nameof(HasTransitionSection), nameof(TransitionKind), nameof(TransitionSeconds), nameof(TransitionSecondsText), nameof(HasTransitionLength),
        nameof(TextContent), nameof(TextStyleChoice), nameof(TextSize), nameof(TextColor), nameof(TextLength), nameof(TextLengthText),
        nameof(MusicVolume), nameof(MusicVolumeText), nameof(MusicFade),
        nameof(AspectChoice), nameof(FitChoice), nameof(HasFadeIn), nameof(HasFadeOut),
        nameof(LevelLoudness), nameof(DuckMusic), nameof(ReduceNoise), nameof(LiftSpeech), nameof(HasOtherShapes),
        nameof(Is16x9), nameof(Is9x16), nameof(Is1x1), nameof(Is4x5), nameof(IsSourceShape),
    ];

    private ClipPart? FirstPart => Selection == EditorSelection.Parts && _selectedParts.Count > 0 && _selectedParts[0] < _project.Parts.Count
        ? _project.Parts[_selectedParts[0]] : null;

    private ClipText? CurrentText => SelectedText >= 0 && SelectedText < _project.Texts.Count ? _project.Texts[SelectedText] : null;

    private void ChangeSelectedParts(Action<ClipPart> change, string? join = null)
    {
        if (_syncing || Selection != EditorSelection.Parts || _selectedParts.Count == 0)
            return;
        StopPlaying();
        _project.ChangeParts(_selectedParts.ToList(), change, join);
    }

    // ───── A part ─────

    /// <summary>The speeds a part can play at.</summary>
    public IReadOnlyList<Choice<double>> Speeds { get; } =
        ClipPart.Speeds.Select(s => new Choice<double>(s, s.ToString("0.##", CultureInfo.CurrentCulture) + "×")).ToList();

    /// <summary>"Steady" and the speeds a part can change to by its end.</summary>
    public IReadOnlyList<Choice<double>> SpeedEnds { get; private set; } = [];

    public double PartVolume
    {
        get => FirstPart?.Volume ?? 1;
        set => ChangeSelectedParts(p =>
        {
            p.Volume = value;
            p.Muted = false;
        }, "volume");
    }

    public string PartVolumeText => $"{Math.Round(PartVolume * 100).ToString(CultureInfo.CurrentCulture)} %";

    public bool PartMuted
    {
        get => FirstPart?.Muted ?? false;
        set => ChangeSelectedParts(p => p.Muted = value);
    }

    public Choice<double>? PartSpeed
    {
        get => FirstPart is { } part ? Speeds.MinBy(s => Math.Abs(s.Value - part.PlaySpeed)) : null;
        set
        {
            if (value is { } speed)
                ChangeSelectedParts(p => p.Speed = speed.Value);
        }
    }

    public Choice<double>? PartSpeedEnd
    {
        get => FirstPart is { } part ? part.IsRamp ? SpeedEnds.Skip(1).MinBy(s => Math.Abs(s.Value - part.PlaySpeedEnd)) : SpeedEnds.FirstOrDefault() : null;
        set
        {
            if (value is { } speed)
                ChangeSelectedParts(p => p.SpeedEnd = speed.Value);
        }
    }

    public bool PartReverse
    {
        get => FirstPart?.Reverse ?? false;
        set => ChangeSelectedParts(p => p.Reverse = value && !p.IsStill);
    }

    public double PartZoom
    {
        get => FirstPart?.PlayZoom ?? 1;
        set => ChangeSelectedParts(p => p.Zoom = Math.Round(value, 2), "zoom");
    }

    public string PartZoomText => $"{Math.Round(PartZoom * 100).ToString(CultureInfo.CurrentCulture)} %";

    public bool IsStillSelected => FirstPart is { IsStill: true };

    /// <summary>A part that plays (not a still or a pause) is selected: its speed and direction can change.</summary>
    public bool IsMovingSelected => FirstPart is { Blank: false, IsStill: false };

    public double PartHold
    {
        get => FirstPart is { IsStill: true } part ? part.Hold : ClipProject.FreezeSeconds;
        set => ChangeSelectedParts(p =>
        {
            if (p.IsStill)
                p.Hold = Math.Round(value, 1);
        }, "hold");
    }

    public string PartHoldText => _loc.Format("editor.seconds", PartHold.ToString("0.#", CultureInfo.CurrentCulture));

    /// <summary>Puts the picture of the selected parts back as it was: not zoomed, in the middle.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ResetFrame() => ChangeSelectedParts(p =>
    {
        p.Zoom = 1;
        p.FocusX = p.FocusY = 0.5;
    });

    // ───── A transition (selected, or of the one part selected) ─────

    /// <summary>The transitions offered.</summary>
    public IReadOnlyList<Choice<ClipTransitionKind>> Transitions { get; private set; } = [];

    /// <summary>The part whose transition into the next one the panel shows, or -1.</summary>
    private int TransitionIndex =>
        Selection == EditorSelection.Transition ? SelectedTransition
        : Selection == EditorSelection.Parts && _selectedParts.Count == 1 && _selectedParts[0] < _project.Parts.Count - 1 ? _selectedParts[0]
        : -1;

    public bool HasTransitionSection => TransitionIndex >= 0;

    public Choice<ClipTransitionKind>? TransitionKind
    {
        get => TransitionIndex >= 0 ? Transitions.FirstOrDefault(t => t.Value == _project.Parts[TransitionIndex].Transition) : null;
        set
        {
            if (_syncing || value is null || TransitionIndex < 0)
                return;
            StopPlaying();
            _project.ChangePart(TransitionIndex, p => p.Transition = value.Value);
        }
    }

    public bool HasTransitionLength => TransitionIndex >= 0 && _project.Parts[TransitionIndex].Transition != ClipTransitionKind.Cut;

    public double TransitionSeconds
    {
        get => TransitionIndex >= 0 ? ClipEdit.TransitionLength(_project.Parts[TransitionIndex]) : 0.5;
        set
        {
            if (_syncing || TransitionIndex < 0)
                return;
            StopPlaying();
            _project.ChangePart(TransitionIndex, p => p.TransitionSeconds = Math.Round(value, 1), "transition length");
        }
    }

    public string TransitionSecondsText => _loc.Format("editor.seconds", TransitionSeconds.ToString("0.#", CultureInfo.CurrentCulture));

    // ───── A text ─────

    /// <summary>The text styles offered.</summary>
    public IReadOnlyList<Choice<ClipTextStyle>> TextStyles { get; private set; } = [];

    /// <summary>The colours a text can have.</summary>
    public IReadOnlyList<string> TextColors => ClipText.Colors;

    public string TextContent
    {
        get => CurrentText?.Text ?? "";
        set
        {
            if (!_syncing && SelectedText >= 0)
                _project.ChangeText(SelectedText, t => t.Text = value ?? "", join: $"text words {SelectedText}");
        }
    }

    public Choice<ClipTextStyle>? TextStyleChoice
    {
        get => CurrentText is { } text ? TextStyles.FirstOrDefault(s => s.Value == text.Style) : null;
        set
        {
            if (_syncing || value is null || SelectedText < 0)
                return;
            // A new style takes its own place, size and colour.
            var styled = ClipText.Of(value.Value, "", 0);
            _project.ChangeText(SelectedText, t =>
            {
                t.Style = value.Value;
                t.Size = 0;
                t.Color = null;
                t.Y = styled.Y;
            });
        }
    }

    public double TextSize
    {
        get => CurrentText?.PlaySize ?? ClipText.DefaultSize(ClipTextStyle.Title);
        set
        {
            if (!_syncing && SelectedText >= 0)
                _project.ChangeText(SelectedText, t => t.Size = Math.Round(value, 3), join: $"text size slider {SelectedText}");
        }
    }

    public string TextColor => CurrentText?.PlayColor ?? "#FFFFFF";

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void SetTextColor(string? color)
    {
        if (SelectedText >= 0 && color is not null)
            _project.ChangeText(SelectedText, t => t.Color = color);
    }

    public double TextLength
    {
        get => CurrentText?.Length ?? 3;
        set
        {
            if (!_syncing && SelectedText >= 0)
                _project.ChangeText(SelectedText, t => t.Length = Math.Round(value, 1), join: $"text length slider {SelectedText}");
        }
    }

    public string TextLengthText => _loc.Format("editor.seconds", TextLength.ToString("0.#", CultureInfo.CurrentCulture));

    // ───── The music ─────

    public double MusicVolume
    {
        get => _project.Music?.Volume ?? ClipMusic.DefaultVolume;
        set
        {
            if (!_syncing)
                _project.ChangeMusic(m => m.Volume = value, join: "music volume");
        }
    }

    public string MusicVolumeText => $"{Math.Round(MusicVolume * 100).ToString(CultureInfo.CurrentCulture)} %";

    public bool MusicFade
    {
        get => _project.Music?.Fade ?? true;
        set
        {
            if (!_syncing)
                _project.ChangeMusic(m => m.Fade = value);
        }
    }

    // ───── The whole clip ─────

    /// <summary>The frame's shapes: the first video's own, then 16:9, 9:16, 1:1, 4:5.</summary>
    public IReadOnlyList<Choice<string>> Aspects { get; private set; } = [];

    /// <summary>How a picture of another shape fills the frame.</summary>
    public IReadOnlyList<Choice<ClipFit>> Fits { get; private set; } = [];

    public Choice<string>? AspectChoice
    {
        get => Aspects.FirstOrDefault(a => a.Value == (_project.Edit.Aspect ?? "")) ?? Aspects.FirstOrDefault();
        set
        {
            if (!_syncing && value is not null)
                SetAspect(value.Value);
        }
    }

    /// <summary>The frame's shape ("" for the first video's own).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    public void SetAspect(string? aspect)
    {
        StopPlaying();
        _project.ChangeClip(c => c.Aspect = string.IsNullOrEmpty(aspect) ? null : aspect);
    }

    public bool IsSourceShape => _project.Edit.Aspect is null;
    public bool Is16x9 => _project.Edit.Aspect == "16:9";
    public bool Is9x16 => _project.Edit.Aspect == "9:16";
    public bool Is1x1 => _project.Edit.Aspect == "1:1";
    public bool Is4x5 => _project.Edit.Aspect == "4:5";

    public Choice<ClipFit>? FitChoice
    {
        get => Fits.FirstOrDefault(f => f.Value == _project.Edit.Fit);
        set
        {
            if (_syncing || value is null)
                return;
            StopPlaying();
            _project.ChangeClip(c => c.Fit = value.Value);
        }
    }

    /// <summary>Some video doesn't have the frame's shape: how it fills the frame matters.</summary>
    public bool HasOtherShapes
    {
        get
        {
            var (width, height) = Canvas;
            var frame = (double)width / height;
            return _project.Parts.Where(p => !p.Blank).Select(p => MediaOf(p.Source)).OfType<EditorMedia>()
                .Any(m => m.Height > 0 && Math.Abs((double)m.Width / m.Height - frame) > 0.02);
        }
    }

    public bool HasFadeIn
    {
        get => _project.Edit.FadeIn > 0;
        set
        {
            if (!_syncing)
                _project.ChangeClip(c => c.FadeIn = value ? ClipWindowViewModel.FadeSeconds : 0);
        }
    }

    public bool HasFadeOut
    {
        get => _project.Edit.FadeOut > 0;
        set
        {
            if (!_syncing)
                _project.ChangeClip(c => c.FadeOut = value ? ClipWindowViewModel.FadeSeconds : 0);
        }
    }

    public bool LevelLoudness
    {
        get => _project.Edit.LevelLoudness;
        set
        {
            if (!_syncing)
                _project.ChangeClip(c => c.LevelLoudness = value);
        }
    }

    public bool DuckMusic
    {
        get => _project.Edit.DuckMusic;
        set
        {
            if (!_syncing)
                _project.ChangeClip(c => c.DuckMusic = value);
        }
    }

    public bool ReduceNoise
    {
        get => _project.Edit.ReduceNoise;
        set
        {
            if (!_syncing)
                _project.ChangeClip(c => c.ReduceNoise = value);
        }
    }

    public bool LiftSpeech
    {
        get => _project.Edit.LiftSpeech;
        set
        {
            if (!_syncing)
                _project.ChangeClip(c => c.LiftSpeech = value);
        }
    }

    /// <summary>The lists of choices, in the current language.</summary>
    private void BuildChoices()
    {
        SpeedEnds = [new Choice<double>(0, _loc["editor.speed_steady"]), .. Speeds];
        Transitions =
        [
            new(ClipTransitionKind.Cut, _loc["editor.transition_cut"]),
            new(ClipTransitionKind.Crossfade, _loc["editor.transition_crossfade"]),
            new(ClipTransitionKind.Black, _loc["editor.transition_black"]),
            new(ClipTransitionKind.Sound, _loc["editor.transition_sound"]),
        ];
        TextStyles =
        [
            new(ClipTextStyle.Title, _loc["editor.style_title"]),
            new(ClipTextStyle.Caption, _loc["editor.style_caption"]),
            new(ClipTextStyle.Label, _loc["editor.style_label"]),
        ];
        Aspects =
        [
            new("", _loc["editor.shape_source"]),
            .. ClipCanvas.Aspects.OfType<string>().Select(a => new Choice<string>(a, a)),
        ];
        Fits =
        [
            new(ClipFit.Bars, _loc["editor.fit_bars"]),
            new(ClipFit.Crop, _loc["editor.fit_crop"]),
            new(ClipFit.Blur, _loc["editor.fit_blur"]),
        ];
        BuildExportChoices();
        OnPropertyChanged(nameof(SpeedEnds));
        OnPropertyChanged(nameof(Transitions));
        OnPropertyChanged(nameof(TextStyles));
        OnPropertyChanged(nameof(Aspects));
        OnPropertyChanged(nameof(Fits));
    }
}
