using System.Diagnostics;
using System.Text.Json;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VideoView.Core;
using VideoView.Core.Library;
using VideoView.Core.Media;
using VideoView.Platform.Audio;
using VideoView.Platform.Recording;
using VideoView.Platform.Video;

namespace VideoView.Tests;

internal static class Program
{
    private static int _checks;
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--fixture"))
        {
            using var form = new System.Windows.Forms.Form { Text = "VideoView TEST PATTERN", Width = 640, Height = 390, BackColor = System.Drawing.Color.SeaGreen };
            using var label = new System.Windows.Forms.Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 24), ForeColor = System.Drawing.Color.White, Text = "VideoView\nCAPTURE TEST\nNo personal content" };
            form.Controls.Add(label);
            using var output = new WaveOutEvent();
            output.Init(new SignalGenerator(48000, 2) { Gain = 0.003, Frequency = 660, Type = SignalGeneratorType.Sin });
            output.Play();
            using var timer = new System.Windows.Forms.Timer { Interval = 1000 };
            var elapsed = 0;
            timer.Tick += (_, _) => { label.Text = $"VideoView\nCAPTURE TEST\n{++elapsed:000} sec"; if (elapsed >= 300) form.Close(); };
            timer.Start(); System.Windows.Forms.Application.Run(form); return 0;
        }
        try { RunAsync(args).GetAwaiter().GetResult(); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    private static async Task RunAsync(string[] args)
    {
        if (args.Contains("--tone"))
        {
            using var output = new WaveOutEvent();
            output.Init(new SignalGenerator(48000, 2) { Gain = 0.005, Frequency = 660, Type = SignalGeneratorType.Sin });
            output.Play(); await Task.Delay(TimeSpan.FromSeconds(15)); return;
        }
        var root = FindRoot();
        var tools = new FfmpegTools(Path.Combine(root, ".tools", "ffmpeg", "bin", "ffmpeg.exe"), Path.Combine(root, ".tools", "ffmpeg", "bin", "ffprobe.exe"));
        if (args.Contains("--crash-worker"))
        {
            var destination = args[Array.IndexOf(args, "--crash-worker") + 1];
            var settings = Settings(destination, false);
            await using var session = new RecordingSession(settings, null, tools, () => [new ToneSource(440), new ToneSource(880)]);
            await session.StartAsync(true, true);
            await Task.Delay(1000);
            await session.AddBookmarkAsync();
            await Task.Delay(5000);
            File.WriteAllText(Path.Combine(destination, "ready"), "ready");
            await Task.Delay(Timeout.Infinite); return;
        }
        var work = Path.Combine(root, ".artifacts", "tests", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(work);
        Console.WriteLine("Artifacts: " + work);
        if (args.Contains("--preview-benchmark")) { await PreviewCacheTests.BenchmarkAsync(tools, Path.Combine(work, "preview-benchmark")); return; }
        if (args.Contains("--buffer-benchmark")) { await BufferOptimizationTests.BenchmarkAsync(Path.Combine(work, "buffer-benchmark")); return; }
        if (args.Contains("--buffer-soak"))
        {
            await BufferOptimizationTests.SoakAsync(tools, Path.Combine(work, "buffer-soak"), Assert);
            Console.WriteLine($"PASS: {_checks} silent buffer soak checks"); return;
        }
        if (args.Contains("--buffer-tests"))
        {
            await BufferOptimizationTests.RunAsync(tools, Path.Combine(work, "buffer"), Assert);
            Console.WriteLine($"PASS: {_checks} silent buffer checks"); return;
        }
        CoreTests(work);
        AudioTests.Run(Assert);
        await tools.CheckAsync();
        if (!args.Contains("--media-only") && !args.Contains("--native-only"))
            await BufferOptimizationTests.RunAsync(tools, Path.Combine(work, "buffer"), Assert);
        if (!args.Contains("--recording-only") && !args.Contains("--native-only")) await MediaTests.RunAsync(tools, Path.Combine(work, "media"), Assert);
        if (!args.Contains("--media-only") && !args.Contains("--native-only"))
        {
            await RecordingTestsAsync(tools, Path.Combine(work, "audio"), false, 9);
            await RecordingTestsAsync(tools, Path.Combine(work, "video"), true, 9);
            await CrashRecoveryTestAsync(tools, Path.Combine(work, "crash"));
            await LifecycleTestsAsync(tools, Path.Combine(work, "lifecycle"));
        }
        if (args.Contains("--native") || args.Contains("--native-only") || args.Contains("--silent-native"))
        { await NativeTestsAsync(!args.Contains("--silent-native")); await EditorTests.RunAsync(Assert); }
        if (args.Contains("--soak"))
        {
            var seconds = int.Parse(args[Array.IndexOf(args, "--soak") + 1]);
            await RecordingTestsAsync(tools, Path.Combine(work, "soak-audio"), false, seconds);
            await RecordingTestsAsync(tools, Path.Combine(work, "soak-video"), true, seconds);
        }
        Console.WriteLine($"PASS: {_checks} checks");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        Interlocked.Increment(ref _checks); Console.WriteLine("PASS: " + message);
    }
    private static void Throws(Action action, string message)
    { try { action(); } catch (ArgumentException) { Assert(true, message); return; } throw new Exception("FAIL: " + message); }
    private static void CoreTests(string work)
    {
        var settings = new AppSettings(); settings.Validate();
        Assert(settings.ReplaySeconds == 60 && !settings.AutoStartReplay && !settings.StartWithWindows, "defaults do not start capture");
        settings.AudioInputs = [new("all", "all", AudioKind.System), new("app", "app", AudioKind.Application, 42)];
        Throws(settings.Validate, "reject overlapping system/application audio");
        settings.AudioInputs = [new("mic1", "mic1", AudioKind.Microphone), new("mic2", "mic2", AudioKind.Microphone)];
        Throws(settings.Validate, "reject multiple microphones");
        settings.AudioInputs = []; settings.ReplaySeconds = 600; Throws(settings.Validate, "reject unsupported replay length");
        var segments = Enumerable.Range(0, 10).Select(i => new SegmentInfo($"segment{i}", i * 2, i * 2 + 2, 100)).ToArray();
        Assert(ReplayWindow.Select(segments, 17.2, 5).Select(s => s.Path).SequenceEqual(new[] { "segment6", "segment7", "segment8" }), "replay selects overlapping boundaries only");
        var expired = ReplayWindow.Expired(segments, 20, 6, 1000, new HashSet<string> { "segment0" });
        Assert(expired.Count == 6 && expired.All(s => s.Path != "segment0"), "pinned save segments survive retention");
        Assert(ReplayWindow.Expired(segments, 20, 300, 500, new HashSet<string>()).Count == 5, "byte cap removes oldest closed chunks");
        var file = Path.Combine(work, "settings.json");
        var store = new SettingsStore(file); store.Save(new AppSettings { ReplaySeconds = 300 });
        Assert(store.Load().ReplaySeconds == 300, "settings atomic round trip");
        File.WriteAllText(file, "broken"); var fallback = store.Load();
        Assert(fallback.ReplaySeconds == 60 && store.LoadWarning != null && File.ReadAllText(file) == "broken", "bad settings preserved with visible warning");
        var legacySettings = JsonSerializer.Deserialize<AppSettings>("{\"ReplaySeconds\":60}")!;
        Assert(legacySettings.HideCaptureBorder && legacySettings.CaptureCursor,
            "Existing settings adopt borderless capture without changing cursor behavior");
        Assert(legacySettings.BookmarkHotkey == "Ctrl+Shift+F6", "Existing settings acquire a separate recording bookmark shortcut");
        var mapped = BookmarkTimeline.Map([new("a", 0, 2, 1), new("b", 4, 6, 1)], 1, 6, 3, [0.5, 1.2, 2.5, 4.5, 6]);
        Assert(mapped.SequenceEqual([0.2, 1.5]), "Bookmark mapping accounts for trim offsets and excludes missing recovery segments");
        var library = new RecordingLibrary(Path.Combine(work, "edit-library"));
        var recording = new RecordingManifest { Title = "Original", DurationSeconds = 60, Tracks = [new(0, "Mic"), new(1, "Game")] };
        recording.MediaPath = library.CreateRecordingPath(recording.CreatedAt, ".mkv");
        File.WriteAllBytes(recording.MediaPath, [1, 3, 5, 7]); library.Save(recording);
        library.SetFavorite(recording.Id, true);
        library.Rename(recording.Id, "Renamed");
        library.SaveEdits(recording.Id, new RecordingEdits { Start = 4, End = 24, Position = 12,
            Tracks = [new(0, .35), new(1, 0, false)], Bookmarks = [20, 8, 8] });
        var edited = library.List().Single();
        Assert(edited.IsFavorite && edited.Title == "Renamed" && edited.Edits is { Start: 4, End: 24, Position: 12 },
            "Edit autosave preserves a concurrent title/favorite update and exact trim/playhead");
        Assert(edited.Edits!.Tracks[0].Gain == .35 && !edited.Edits.Tracks[1].Included && edited.Edits.Bookmarks.SequenceEqual([8d, 20]),
            "Independent gains, mute and ordered unique bookmarks survive a library reload");
        Assert(File.ReadAllBytes(recording.MediaPath).SequenceEqual(new byte[] { 1, 3, 5, 7 }), "Edit persistence never changes original media bytes");
        var invalidEdits = new RecordingEdits { Start = 50, End = 2, Position = double.NaN,
            Tracks = [new(0, double.PositiveInfinity), new(1, -2)], Bookmarks = [-3, double.NaN, 12, 80] }.Normalize(recording);
        Assert(invalidEdits.Start == 0 && invalidEdits.End == 60 && invalidEdits.Position == 0 &&
            invalidEdits.Tracks[0].Gain == 1 && invalidEdits.Tracks[1].Gain == 0 && invalidEdits.Bookmarks.SequenceEqual([12d]),
            "Damaged edit decisions recover to safe bounds without discarding the original");
        library.SetFavorite(recording.Id, false);
        Assert(!library.List().Single().IsFavorite && library.List().Single().Edits!.End == 24, "Removing a favorite preserves edits");
    }

    private static AppSettings Settings(string directory, bool video) => new()
    {
        OutputDirectory = Path.Combine(directory, "recordings"), BufferDirectory = Path.Combine(directory, "buffer"),
        Mode = video ? CaptureMode.ScreenAndAudio : CaptureMode.AudioOnly, Height = 720, Quality = Quality.Low,
        ReplaySeconds = 30,
        AudioInputs = [new("tone1", "Tone 440", AudioKind.Application, 1), new("tone2", "Tone 880", AudioKind.Application, 2)]
    };
    private static async Task RecordingTestsAsync(FfmpegTools tools, string directory, bool video, int seconds)
    {
        Directory.CreateDirectory(directory);
        var settings = Settings(directory, video);
        if (seconds >= 300) settings.ReplaySeconds = 300;
        var saved = new List<RecordingManifest>();
        var faults = new List<string>();
        var frames = new TestVideo();
        await using var session = new RecordingSession(settings, null, tools, () => [new ToneSource(440), new ToneSource(880)], video ? frames.Next : null);
        session.Saved += recording => saved.Add(recording);
        session.Faulted += message => { lock (faults) faults.Add(message); Console.Error.WriteLine("CAPTURE FAULT: " + message); };
        session.Status += Console.WriteLine;
        await session.StartAsync(true, true);
        var marks = new List<double>();
        for (var i = 0; i < seconds; i++)
        {
            await Task.Delay(1000);
            if (i == Math.Max(1, seconds - 8) || i == Math.Max(4, seconds - 5)) marks.Add(await session.AddBookmarkAsync());
            if (video && i == 0) frames.Resize(800, 450);
            if (video && i == 2) frames.Resize(360, 640);
            if (video && i == 5) frames.Resize(640, 360);
            lock (faults) Assert(faults.Count == 0, $"{(video ? "video" : "audio")} capture healthy @{i + 1}s");
        }
        var firstReplayStart = Math.Max(0, session.ElapsedSeconds - settings.ReplaySeconds);
        var replayTask = session.SaveReplayAsync();
        var laterMark = await session.AddBookmarkAsync();
        var replay = await replayTask;
        Assert(replay.Edits?.Bookmarks.Count == 2 && replay.Edits.Bookmarks.Zip(marks).All(p => Math.Abs(p.First - (p.Second - firstReplayStart)) < 0.1),
            "Replay keeps marks at their clip-relative times and excludes a mark after the save keypress");
        Assert(session.IsRunning && session.IsRecording && session.IsBuffering, "replay save leaves both modes running");
        var secondReplayStart = Math.Max(0, session.ElapsedSeconds - settings.ReplaySeconds);
        var second = await session.SaveReplayAsync();
        Assert(second.Edits?.Bookmarks.Count == 3 && Math.Abs(second.Edits.Bookmarks[^1] - (laterMark - secondReplayStart)) < 0.1,
            "Marking during replay export does not stop capture and appears in the next saved replay");
        Assert(replay.MediaPath != second.MediaPath && File.Exists(replay.MediaPath), "repeated replay saves use distinct files");
        Assert(replay.Tracks.Count == 2 && second.Tracks.Count == 2, "saved replay preserves two independent tracks");
        if (seconds >= 300) Assert(Math.Abs(replay.DurationSeconds - 300) < 0.15, "five minute replay length");
        else Assert(replay.DurationSeconds >= seconds - 1 && replay.DurationSeconds <= seconds + 3, "warm-up replay uses actual available length");
        var ending = session.SetContinuousAsync(false);
        var rejectedWhileEnding = false;
        try { await session.AddBookmarkAsync(); }
        catch (InvalidOperationException) { rejectedWhileEnding = true; }
        Assert(rejectedWhileEnding, "A mark after continuous stop is requested is rejected instead of silently omitted");
        await ending;
        Assert(session.IsRunning && session.IsBuffering && !session.IsRecording, "stop continuous preserves replay");
        await session.AddBookmarkAsync(); // Buffer-only mark must not leak into the next continuous archive.
        await session.SetContinuousAsync(true);
        await Task.Delay(1000);
        var restartedMark = await session.AddBookmarkAsync();
        await Task.Delay(1600);
        await session.StopAsync();
        Assert(saved.Count == 4, "two replays and two independent continuous recordings finalized");
        Assert(saved[^1].Edits?.Bookmarks.Count == 1 && Math.Abs(saved[^1].Edits!.Bookmarks[0] - restartedMark) < 0.1,
            "Restarted continuous recording rebases bookmarks and excludes earlier buffer-only marks");
        Assert(new RecordingLibrary(settings.OutputDirectory).List().Count == 4, "all recordings appear in library");
        foreach (var recording in saved)
        {
            var info = await tools.ProbeAsync(recording.MediaPath);
            Assert(info.Tracks.Count == 2 && info.HasVideo == video, "recorded media stream layout valid");
            await tools.RunAsync(["-v", "error", "-i", recording.MediaPath, "-map", "0", "-f", "null", "-"]);
        }
        Assert(saved[^1].DurationSeconds is >= 2.4 and < 4.5, "continuous restart excludes previous buffered footage");
        if (video && seconds < 300)
        {
            var pixels = Path.Combine(directory, "portrait.rgb");
            await tools.RunAsync(["-v", "error", "-ss", "4", "-i", replay.MediaPath, "-frames:v", "1",
                "-vf", "scale=64:36", "-pix_fmt", "rgb24", "-f", "rawvideo", pixels]);
            var rgb = await File.ReadAllBytesAsync(pixels);
            Assert(rgb.Length == 64 * 36 * 3 && rgb[(18 * 64 + 2) * 3 + 1] < 10 && rgb[(18 * 64 + 32) * 3 + 1] > 60,
                "Resized portrait content remains visible inside a stable letterboxed encoder canvas");
        }
        var previewPath = Path.Combine(directory, video ? "preview.mp4" : "preview.m4a");
        await new PreviewService(tools).CreateAsync(saved[0], [new(0, 0.5), new(1, 0, false)], previewPath);
        var previewInfo = await tools.ProbeAsync(previewPath);
        Assert(previewInfo.Tracks.Count == 1 && previewInfo.HasVideo == video && Math.Abs(previewInfo.DurationSeconds - saved[0].DurationSeconds) < 0.1,
            "fast preview preserves full seekable timeline with selected mixed audio");
    }

    private static async Task CrashRecoveryTestAsync(FfmpegTools tools, string directory)
    {
        Directory.CreateDirectory(directory);
        var process = new Process { StartInfo = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true } };
        process.StartInfo.ArgumentList.Add("--crash-worker"); process.StartInfo.ArgumentList.Add(directory); process.Start();
        try
        {
            var watch = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(directory, "ready")))
            {
                if (process.HasExited || watch.Elapsed > TimeSpan.FromSeconds(30)) throw new Exception("Crash worker failed to start.");
                await Task.Delay(100);
            }
            var active = await RecordingSession.RecoverAsync(Settings(directory, false), tools);
            Assert(active.Count == 0, "recovery skips active sessions");
            process.Kill(true); await process.WaitForExitAsync();
            var settings = Settings(directory, false);
            var journalPath = Directory.EnumerateFiles(Path.Combine(settings.OutputDirectory, ".sessions"), "session.json", SearchOption.AllDirectories).Single();
            var journal = JsonSerializer.Deserialize<RecordingSession.ContinuousJournal>(File.ReadAllText(journalPath))!;
            var uncopied = journal.Segments[^1];
            File.Delete(uncopied.Path); journal.Segments.RemoveAt(journal.Segments.Count - 1);
            File.WriteAllText(journalPath, JsonSerializer.Serialize(journal));
            var broken = Path.Combine(settings.OutputDirectory, ".sessions", "broken-test-journal");
            Directory.CreateDirectory(broken); File.WriteAllText(Path.Combine(broken, "session.json"), "broken");
            var warnings = new List<string>();
            var recovered = await RecordingSession.RecoverAsync(settings, tools, warnings: new InlineProgress<string>(warnings.Add));
            Assert(recovered.Count == 1 && recovered[0].Recovered && recovered[0].Tracks.Count == 2, "crash recovery retains finalized independent audio tracks");
            Assert(recovered[0].DurationSeconds >= uncopied.EndSeconds - 0.1, "crash recovers finalized buffer chunk missing from durable journal");
            Assert(recovered[0].Edits?.Bookmarks.Count == 1 && recovered[0].Edits!.Bookmarks[0] is > 0.8 and < 2,
                "Crash recovery restores a recording-time bookmark from the durable journal");
            Assert(warnings.Count > 0 && File.Exists(Path.Combine(broken, "session.json")), "damaged journal does not block healthy recovery and remains intact");
        }
        finally { if (!process.HasExited) process.Kill(true); process.Dispose(); }
    }

    private static async Task LifecycleTestsAsync(FfmpegTools tools, string directory)
    {
        Directory.CreateDirectory(directory);
        var failingSource = new FaultSource();
        var unusedSource = new FaultSource { FailAtStart = false };
        var failedSession = new RecordingSession(Settings(Path.Combine(directory, "startup"), false), null, tools, () => [failingSource, unusedSource]);
        failedSession.Faulted += _ => throw new InvalidOperationException("Deliberately throwing observer");
        var startFailed = false;
        try { await failedSession.StartAsync(true, true).WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (Exception) { startFailed = true; }
        try { await failedSession.DisposeAsync(); } catch (Exception) { }
        Assert(startFailed && !failingSource.Active && !unusedSource.Active, "startup failure and throwing observer still stop every source");

        var settings = Settings(Path.Combine(directory, "concurrent-stop"), false);
        await using (var session = new RecordingSession(settings, null, tools, () => [new ToneSource(440), new ToneSource(880)]))
        {
            await session.StartAsync(true, true);
            await Task.Delay(4500);
            var replay = session.SaveReplayAsync();
            var stopping = session.StopAsync();
            await Task.WhenAll(replay, stopping).WaitAsync(TimeSpan.FromSeconds(30));
            Assert(!session.IsRunning && (await replay).DurationSeconds >= 4.3, "stop drains final chunk while pending replay preserves its requested interval");
        }

        settings = Settings(Path.Combine(directory, "journal-failure"), false);
        var failure = new RecordingSession(settings, null, tools, () => [new ToneSource(440), new ToneSource(880)]);
        await failure.StartAsync(true, false);
        await Task.Delay(4300);
        var journal = Directory.EnumerateFiles(Path.Combine(settings.OutputDirectory, ".sessions"), "session.json", SearchOption.AllDirectories).Single();
        using (var lockedJournal = new FileStream(journal + ".tmp", FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            try { await failure.StopAsync().WaitAsync(TimeSpan.FromSeconds(25)); } catch (IOException) { }
            Assert(!failure.IsRunning, "journal failure cannot leave capture running after stop");
        }
        try { await failure.DisposeAsync(); } catch (IOException) { }
        var recovered = await RecordingSession.RecoverAsync(settings, tools);
        Assert(recovered.Count == 1, "journal write failure remains recoverable after lock release");

        var silentSettings = Settings(Path.Combine(directory, "video-without-audio"), true);
        silentSettings.AudioInputs = [];
        RecordingManifest? silentRecording = null;
        var picture = new TestVideo();
        await using (var silentSession = new RecordingSession(silentSettings, null, tools, () => [], picture.Next))
        {
            silentSession.Saved += item => silentRecording = item;
            await silentSession.StartAsync(true, false); await Task.Delay(2300); await silentSession.StopAsync();
        }
        var silentInfo = await tools.ProbeAsync(silentRecording!.MediaPath);
        Assert(silentInfo.HasVideo && silentInfo.Tracks.Count == 0, "explicit video-only recording works without any audio pipe");

        var oneSettings = Settings(Path.Combine(directory, "one-audio-track"), false);
        oneSettings.AudioInputs = [oneSettings.AudioInputs[0]];
        RecordingManifest? oneRecording = null;
        await using (var oneSession = new RecordingSession(oneSettings, null, tools, () => [new ToneSource(440)]))
        {
            oneSession.Saved += item => oneRecording = item;
            await oneSession.StartAsync(true, false); await Task.Delay(2300); await oneSession.StopAsync();
        }
        Assert(oneRecording!.Tracks.Count == 1, "single source audio-only capture uses one intact track");
    }

    private static async Task NativeTestsAsync(bool playAudio)
    {
        if (playAudio)
        {
            using var tone = new Process { StartInfo = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true } };
            tone.StartInfo.ArgumentList.Add("--tone"); tone.Start();
            using var source = AudioSourceFactory.Create(new($"test:{tone.Id}", "Own test tone", AudioSourceKind.Application, tone.Id));
            var errors = new List<string>(); source.Faulted += error => { lock (errors) errors.Add(error); };
            source.Start();
            var samples = new float[960]; double energy = 0; var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(4))
            {
                source.Read(samples, 0, samples.Length); energy += samples.Sum(x => (double)x * x);
                await Task.Delay(10);
            }
            source.Stop();
            if (!tone.HasExited) tone.Kill(true);
            lock (errors) Assert(errors.Count == 0, "native process loopback starts without errors: " + string.Join(";", errors));
            Assert(energy > 0.00001, "native process loopback captures own test tone");
        }
        else Console.WriteLine("SKIP: audible process-loopback check (--silent-native); file-based audio checks still run.");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var form = new System.Windows.Forms.Form { Text = "VideoView capture test", Width = 480, Height = 300, BackColor = System.Drawing.Color.SeaGreen };
            form.Shown += async (_, _) =>
            {
                try { await VideoTests.NativeSmokeAsync(form.Handle, Assert).ConfigureAwait(false); completion.SetResult(); }
                catch (Exception e) { completion.SetException(e); }
                finally { if (!form.IsDisposed && form.IsHandleCreated) form.BeginInvoke(form.Close); }
            };
            System.Windows.Forms.Application.Run(form);
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task;
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "VideoView.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class ToneSource(double frequency) : IAudioSource
    {
        private long _samples;
        public string Name => "Tone " + frequency;
        public event Action<string>? Faulted { add { } remove { } }
        public float Peak => 0.1f;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public int Read(float[] buffer, int offset, int count)
        {
            for (var i = 0; i < count; i += 2)
            {
                var sample = (float)(0.1 * Math.Sin(2 * Math.PI * frequency * _samples++ / 48000));
                buffer[offset + i] = sample; buffer[offset + i + 1] = sample;
            }
            return count;
        }
    }
    private sealed class TestVideo
    {
        private VideoFrame _current = null!;
        private long _sequence;
        public TestVideo() => Resize(640, 360);
        public void Resize(int width, int height)
        {
            var pixels = new byte[width * height * 4];
            for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 65; pixels[i + 1] = 135; pixels[i + 2] = (byte)((i / 4) % 255); pixels[i + 3] = 255; }
            Volatile.Write(ref _current, new(pixels, width, height, 0));
        }
        public VideoFrame Next() => Volatile.Read(ref _current) with { Sequence = Interlocked.Increment(ref _sequence) };
    }
    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
    private sealed class FaultSource : IAudioSource
    {
        public bool FailAtStart { get; init; } = true;
        public bool Active { get; private set; }
        public string Name => "Fault source";
        public float Peak => 0;
        public event Action<string>? Faulted;
        public void Start() { Active = true; if (FailAtStart) Faulted?.Invoke("Synthetic startup failure"); }
        public void Stop() => Active = false;
        public void Dispose() => Stop();
        public int Read(float[] buffer, int offset, int count) { Array.Clear(buffer, offset, count); return count; }
    }
}
