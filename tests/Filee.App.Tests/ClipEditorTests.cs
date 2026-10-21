// The clip window's editor with a painted stand-in for FFmpeg: opening it from the quick cut and going back, files
// added (videos on the timeline, a sound as the music), its keys, playing, the properties, the Export window's
// conversion, "Join videos", and Ctrl + wheel zoom.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Core.Presets;

namespace Filee.App.Tests;

public class ClipEditorTests
{
    private static void Pump() => RenderTests.Pump();

    [AvaloniaFact]
    public void The_editor_starts_from_the_quick_cut_and_goes_back_to_it()
    {
        ClipWindowTests.WithClipWindow("en", false, ["match.mp4"], (window, vm, _, _) =>
        {
            vm.MoveHandle(ClipHandle.Start, 10);
            vm.SetLength(20);
            vm.MovePlayhead(15);
            vm.Split();
            Assert.Equal(2, vm.Pieces.Count);

            vm.OpenEditor();
            Pump();
            var editor = Assert.IsType<ClipEditorViewModel>(vm.Editor);
            Assert.True(vm.IsEditorMode);
            Assert.False(vm.IsQuickMode);
            Assert.Equal(20, editor.Length, 6);
            Assert.Equal(2, editor.Edit.Parts.Count);

            // Still a quick cut: back at once, with its pieces.
            editor.Split();
            editor.Back();
            Assert.Null(vm.Editor);
            Assert.Equal(20, vm.Length, 6);
            Assert.True(vm.Pieces.Count >= 2);
        });
    }

    [AvaloniaFact]
    public void Going_back_asks_when_the_editor_has_more_than_a_quick_cut()
    {
        ClipWindowTests.WithClipWindow("en", false, ["match.mp4"], (window, vm, _, _) =>
        {
            vm.OpenEditor();
            var editor = vm.Editor!;
            editor.SelectPart(0, ClipSelect.Only);
            editor.PartReverse = true;
            editor.Back();
            Assert.True(editor.IsBackConfirmOpen);
            Assert.Same(editor, vm.Editor);
            editor.StayInEditorCommand.Execute(null);
            Assert.False(editor.IsBackConfirmOpen);
            editor.Back();
            editor.ConfirmBackCommand.Execute(null);
            Assert.Null(vm.Editor);
            Assert.True(vm.IsQuickMode);
        });
    }

    [AvaloniaFact]
    public void Files_added_go_on_the_timeline_and_under_it()
    {
        ClipWindowTests.WithClipWindow("en", false, ["match.mp4", "phone tall.mp4", "song.mp3", "notes.txt"], (window, vm, frames, _) =>
        {
            vm.OpenEditor();
            var editor = vm.Editor!;
            var before = editor.Length;
            IReadOnlyList<EditorMedia>? flown = null;
            editor.FilesAdded += media => flown = media;

            var added = editor.AddFilesAsync(ClipWindowTests.OtherFiles(vm), toTimeline: true).GetAwaiter().GetResult();

            Assert.Equal(2, added.Count); // the text file isn't a video or a sound
            Assert.Equal(3, editor.Files.Count);
            Assert.Same(added, flown);
            Assert.True(added.All(m => m.JustAdded));
            Assert.Equal(2, editor.Edit.Parts.Count);
            Assert.EndsWith("phone tall.mp4", editor.Edit.Parts[1].Source);
            Assert.Equal(before + ClipWindowTests.Length / 2, editor.Length, 6);
            Assert.True(editor.HasMusic);
            Assert.EndsWith("song.mp3", editor.Edit.Music!.Path);
            Assert.Contains("1", editor.Message); // "Not a video or a sound: 1"
            Assert.True(editor.Files.All(f => f.IsUsed));
            // One undo takes them all off again.
            editor.Undo();
            Assert.Single(editor.Edit.Parts);
            Assert.False(editor.HasMusic);
        });
    }

    [AvaloniaFact]
    public void The_editors_keys_cut_copy_add_text_and_undo()
    {
        ClipWindowTests.WithClipWindow("en", false, ["match.mp4"], (window, vm, _, _) =>
        {
            vm.OpenEditor();
            var editor = vm.Editor!;
            Pump();
            editor.Seek(10);
            ClipWindowTests.Press(window, Key.C);
            Assert.Equal(2, editor.Edit.Parts.Count);
            editor.SelectPart(0, ClipSelect.Only);
            ClipWindowTests.Press(window, Key.D, RawInputModifiers.Control);
            Assert.Equal(3, editor.Edit.Parts.Count);
            Assert.Equal(40, editor.Length, 6);
            ClipWindowTests.Press(window, Key.Delete);
            Assert.True(editor.HasGaps);
            ClipWindowTests.Press(window, Key.Delete, RawInputModifiers.Shift); // the gap selected closes
            Assert.False(editor.HasGaps);
            Assert.Equal(30, editor.Length, 6);
            ClipWindowTests.Press(window, Key.T);
            Assert.Single(editor.Edit.Texts);
            Assert.True(editor.HasTextSelection);
            ClipWindowTests.Press(window, Key.Escape); // leaves the text box
            ClipWindowTests.Press(window, Key.Escape);
            Assert.Equal(EditorSelection.None, editor.Selection);
            ClipWindowTests.Press(window, Key.Z, RawInputModifiers.Control);
            Assert.Empty(editor.Edit.Texts);
            ClipWindowTests.Press(window, Key.L);
            Assert.Equal(15, editor.Playhead, 6);
            ClipWindowTests.Press(window, Key.J);
            Assert.Equal(10, editor.Playhead, 6);
            ClipWindowTests.Press(window, Key.OemPeriod);
            Assert.Equal(10 + 1 / 30.0, editor.Playhead, 6);
            ClipWindowTests.Press(window, Key.F);
            Assert.True(editor.IsFullPreview);
            ClipWindowTests.Press(window, Key.Escape);
            Assert.False(editor.IsFullPreview);
        });
    }

    [AvaloniaFact]
    public void Playing_plays_the_editors_clip_from_the_playhead()
    {
        ClipWindowTests.WithClipWindow("en", false, ["match.mp4"], (window, vm, frames, _) =>
        {
            vm.OpenEditor();
            var editor = vm.Editor!;
            editor.Seek(5);
            ClipWindowTests.Press(window, Key.Space);
            ClipWindowTests.WaitFor(() => !editor.IsPlaying);
            var played = Assert.Single(frames.PlayedEditor);
            Assert.True(played.Editor);
            Assert.Equal(25, played.Length, 6);
            Assert.True(editor.Playhead > 5);
        });
    }

    [AvaloniaFact]
    public void The_properties_change_the_selection_and_the_clip()
    {
        ClipWindowTests.WithClipWindow("en", false, ["match.mp4"], (window, vm, _, _) =>
        {
            vm.OpenEditor();
            var editor = vm.Editor!;
            editor.Seek(10);
            editor.Split();
            editor.SelectPart(0, ClipSelect.Only);
            Assert.True(editor.HasPartSelection);
            Assert.True(editor.HasTransitionSection);
            editor.PartSpeed = editor.Speeds.First(s => s.Value == 2);
            editor.PartSpeedEnd = editor.SpeedEnds.First(s => s.Value == 0.5);
            editor.TransitionKind = editor.Transitions.First(t => t.Value == ClipTransitionKind.Crossfade);
            editor.PartVolume = 1.5;
            var part = editor.Edit.Parts[0];
            Assert.True(part.IsRamp);
            Assert.Equal((2.0, 0.5, 1.5), (part.PlaySpeed, part.PlaySpeedEnd, part.Volume));
            Assert.Equal(ClipTransitionKind.Crossfade, part.Transition);

            editor.ClearSelection();
            Assert.True(editor.HasNoSelection);
            editor.SetAspect("9:16");
            editor.LevelLoudness = true;
            editor.DuckMusic = true;
            Assert.Equal(("9:16", true, true), (editor.Edit.Aspect, editor.Edit.LevelLoudness, editor.Edit.DuckMusic));
            Assert.Equal((1080, 1920), editor.Canvas);
            Assert.True(editor.HasOtherShapes);
            Assert.True(editor.Is9x16);
        });
    }

    [AvaloniaFact]
    public void Export_starts_the_conversion_with_the_choices_and_moves_on()
    {
        ClipWindowTests.WithClipWindow("en", false, ["match.mp4"], (window, vm, frames, exports) =>
        {
            var closed = false;
            vm.CloseRequested += () => closed = true;
            vm.OpenEditor();
            var editor = vm.Editor!;
            editor.Seek(10);
            editor.Split();
            ClipWindowTests.WaitFor(() => editor.HasGpu);
            Assert.Contains("NVIDIA", editor.GpuText);
            editor.OpenExport();
            editor.ExportPreset = editor.ExportPresets.First(p => p.Value == "shorts");
            Assert.Equal("9:16", editor.Edit.Aspect); // Shorts are upright
            editor.ExportFormat = editor.ExportFormats.First(f => f.Value == "mp4-hevc");
            editor.ExportLimit = editor.ExportLimits.First(l => l.Value == 25);
            editor.ExportSeparate = true;
            Assert.Contains("1080×1920", editor.ExportSummary);

            ClipWindowTests.Press(window, Key.Enter);

            var (path, conversion) = Assert.Single(exports);
            Assert.EndsWith("match.mp4", path);
            Assert.Equal("mp4", conversion.TargetFormat);
            Assert.Equal(VideoCodec.Hevc, conversion.Media.VideoCodec);
            Assert.True(conversion.Media.HardwareEncoding);
            var edit = conversion.Media.Clip!;
            Assert.True(edit.Editor);
            Assert.Equal((1080, 25.0, true), (edit.Resolution, edit.MaxMegabytes, edit.Separate));
            Assert.Equal(2, edit.Parts.Count);
            // The last video: the window closes.
            Assert.True(closed);
        });
    }

    [AvaloniaFact]
    public void Joining_videos_opens_the_editor_with_all_of_them()
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateTempSubdirectory("filee-join-").FullName;
        try
        {
            var files = new[] { "one.mp4", "two.mp4", "three tall.mp4" }.Select(n => Path.Combine(folder, n)).ToList();
            foreach (var file in files)
                File.WriteAllBytes(file, [0]);
            var frames = new ClipWindowTests.PaintedFrames(ClipWindowTests.Length);
            var tool = BuiltInData.CreatePresets().Single(p => p.Id == "tool-clip");
            var vm = new ClipWindowViewModel([files[0]], tool, AppHost.Get<ILocalizer>(), AppHost.Get<Filee.Core.Settings.UserDataStore>(), frames,
                _ => "mp4", (_, _) => { }, joining: files.Skip(1).ToList());
            ClipWindowTests.WaitFor(() => vm.Editor is { Edit.Parts.Count: 3 });
            var editor = vm.Editor!;
            Assert.Equal(ClipWindowTests.Length * 2.5, editor.Length, 6); // whole, whole, and the upright one of half the length
            Assert.Null(editor.Edit.Parts[0].Source);
            vm.Dispose();

            // The order window: sorted the way people count, different shapes asked about.
            var join = new JoinVideosViewModel(files, AppHost.Get<ILocalizer>(), frames);
            ClipWindowTests.WaitFor(() => join.IsReady);
            Assert.Equal(["one.mp4", "three tall.mp4", "two.mp4"], join.Files.Select(f => f.Name));
            Assert.True(join.Differ);
            join.SelectedFile = join.Files[2];
            join.MoveUpCommand.Execute(null);
            join.Fit = join.Fits.First(f => f.Value == ClipFit.Crop);
            join.JoinCommand.Execute(null);
            var choice = join.Result!;
            Assert.False(choice.OpenInEditor);
            Assert.Equal(["one.mp4", "two.mp4", "three tall.mp4"], choice.Files.Select(Path.GetFileName));
            Assert.Equal(ClipFit.Crop, choice.Edit.Fit);
            Assert.Equal([null, files[1], files[2]], choice.Edit.Parts.Select(p => p.Source));
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public void Ctrl_and_the_wheel_or_plus_minus_zoom_every_window()
    {
        TestServices.EnsureInitialized("en");
        var window = new Window { Content = new Border { Width = 100, Height = 100 }, Width = 300, Height = 300 };
        UiZoom.Track(window);
        window.Show();
        try
        {
            UiZoom.Set(1, remember: false);
            var scaled = Assert.IsType<LayoutTransformControl>(window.Content);
            window.KeyPress(Key.OemPlus, RawInputModifiers.Control, PhysicalKey.None, null);
            Assert.Equal(1.1, UiZoom.Scale, 6);
            Assert.Equal(1.1, Assert.IsType<ScaleTransform>(scaled.LayoutTransform).ScaleX, 6);
            window.MouseWheel(new Point(50, 50), new Vector(0, 1), RawInputModifiers.Control);
            Assert.Equal(1.2, UiZoom.Scale, 6);
            window.KeyPress(Key.D0, RawInputModifiers.Control, PhysicalKey.None, null);
            Assert.Equal(1, UiZoom.Scale, 6);
            Assert.Null(scaled.LayoutTransform);
            UiZoom.Set(99, remember: false);
            Assert.Equal(UiZoom.Max, UiZoom.Scale);
        }
        finally
        {
            UiZoom.Set(1, remember: false);
            window.Close();
        }
    }
}
