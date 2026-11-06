using System.Diagnostics;
using System.Text.Json;
using VideoView.Core;
using VideoView.Core.Media;
using VideoView.Platform.Recording;
using VideoView.Platform.Video;
using Forms = System.Windows.Forms;

namespace VideoView.Tests;

// Capture only our own animated window. No microphone, desktop capture or audio output.
internal static class CaptureBenchmark
{
    public static Task RunAsync(FfmpegTools tools, string work, bool gpuConversion, int fps, int height)
    {
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var form = new Pattern();
            using var timer = new Forms.Timer { Interval = 15 };
            timer.Tick += (_, _) => { form.Step++; form.Invalidate(); };
            form.Shown += async (_, _) =>
            {
                try
                {
                    timer.Start();
                    var target = new CaptureTarget("benchmark", "VideoView synthetic window", CaptureTargetKind.Window, form.Handle);
                    await Task.Run(() => MeasureAsync(tools, work, target, gpuConversion, fps, height));
                    complete.TrySetResult();
                }
                catch (Exception ex) { complete.TrySetException(ex); }
                finally { timer.Stop(); form.Close(); }
            };
            Forms.Application.Run(form);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return complete.Task;
    }

    private sealed class Pattern : Forms.Form
    {
        public int Step;
        public Pattern()
        {
            FormBorderStyle = Forms.FormBorderStyle.None; Width = 2560; Height = 1440;
            Text = "VideoView performance test"; DoubleBuffered = true;
        }
        protected override bool ShowWithoutActivation => true;
        protected override void OnPaint(Forms.PaintEventArgs e)
        {
            e.Graphics.Clear(System.Drawing.Color.DarkSlateBlue);
            for (int x = -240; x < Width; x += 240)
                e.Graphics.FillRectangle(System.Drawing.Brushes.Turquoise, x + Step * 7 % 240, 0, 85, Height);
        }
    }

    private static async Task MeasureAsync(FfmpegTools tools, string work, CaptureTarget target, bool gpuConversion, int fps, int height)
    {
        using var process = Process.GetCurrentProcess();
        var settings = new AppSettings { OutputDirectory = Path.Combine(work, "recordings"), BufferDirectory = Path.Combine(work, "ring"),
            Mode = CaptureMode.ScreenAndAudio, Height = height, FramesPerSecond = fps, ReplaySeconds = 60,
            UseGpuVideoConversion = gpuConversion };
        RecordingManifest? saved = null;
        await using var session = new RecordingSession(settings, target, tools);
        session.Saved += recording => saved = recording;
        var startup = Stopwatch.StartNew();
        await session.StartAsync(true, true); startup.Stop();
        try
        {
            using var encoder = Process.GetProcessById(session.EncoderProcessId);
            await Task.Delay(2000);
            var initial = session.PreviewFrame ?? throw new IOException("No capture frame");
            var appCpu = process.TotalProcessorTime; var encoderCpu = encoder.TotalProcessorTime;
            var late = session.LateFrames; var repeats = session.RepeatedFrames;
            var gen2 = GC.CollectionCount(2); var allocated = GC.GetTotalAllocatedBytes(true);
            var watch = Stopwatch.StartNew(); await Task.Delay(10000); watch.Stop();
            if (!session.IsRunning || !session.IsBuffering || !session.IsRecording) throw new IOException("Recording did not stay active.");
            var result = new { SourceWidth = initial.Width, SourceHeight = initial.Height, settings.Height, settings.FramesPerSecond,
                Encoder = session.EncoderName, Conversion = session.VideoProcessing, StartupMs = startup.ElapsedMilliseconds, Seconds = watch.Elapsed.TotalSeconds,
                AppCpuCores = (process.TotalProcessorTime - appCpu).TotalSeconds / watch.Elapsed.TotalSeconds,
                EncoderCpuCores = (encoder.TotalProcessorTime - encoderCpu).TotalSeconds / watch.Elapsed.TotalSeconds,
                AllocatedMiBPerSec = (GC.GetTotalAllocatedBytes(true) - allocated) / 1048576d / watch.Elapsed.TotalSeconds,
                Gen2Collections = GC.CollectionCount(2) - gen2, LateFrames = session.LateFrames - late, RepeatedFrames = session.RepeatedFrames - repeats };
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(work, "capture-benchmark.json"), json); Console.WriteLine(json);
            var replay = await session.SaveReplayAsync();
            if (!session.IsRecording || !session.IsBuffering || !File.Exists(replay.MediaPath))
                throw new IOException("Replay interrupted recording.");
        }
        finally { await session.StopAsync(); }
        if (saved == null) throw new IOException("Missing continuous recording");
        var info = await tools.ProbeAsync(saved.MediaPath);
        if (!info.HasVideo || info.Height != height || Math.Abs(info.FramesPerSecond - fps) > .01 || info.DurationSeconds < 10)
            throw new IOException("Unexpected benchmark recording");
        await tools.RunAsync(["-hide_banner", "-v", "error", "-xerror", "-i", saved.MediaPath, "-map", "0:v:0", "-f", "null", "-"]);
        var swatch = Path.Combine(work, "pattern.rgb");
        await tools.RunAsync(["-hide_banner", "-v", "error", "-ss", "3", "-i", saved.MediaPath, "-frames:v", "1",
            "-vf", "scale=1:1:flags=area", "-f", "rawvideo", "-pix_fmt", "rgb24", swatch]);
        var rgb = File.ReadAllBytes(swatch);
        if (rgb.Length != 3 || rgb[2] <= rgb[0] + 30 || rgb[1] <= rgb[0] + 15)
            throw new IOException("Decoded recording lost the colors of the animated pattern: " + string.Join(",", rgb));
        Console.WriteLine("PASS: continuous recording, simultaneous replay save, complete video decode.");
    }
}
