using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VideoView.Core;
using VideoView.Core.Media;
using VideoView.Platform.Audio;
using VideoView.Platform.Recording;
using VideoView.Platform.Video;

namespace VideoView.Tests;

/// <summary>Silent, owned-file-only buffer checks. No audio device, capture target, player,
/// tone output, or user process is opened. Program.cs integration is intentionally external.</summary>
public static class BufferOptimizationTests
{
    public static async Task RunAsync(FfmpegTools tools, string work, Action<bool, string> check)
    {
        Directory.CreateDirectory(work);
        var csvRoot = Path.Combine(work, "csv");
        Directory.CreateDirectory(csvRoot);
        var first = Path.Combine(csvRoot, "part_00000000.mkv");
        await File.WriteAllBytesAsync(first, [1, 2, 3]);
        var csv = Path.Combine(csvRoot, "segments.csv");
        var tail = new SegmentCsvTail();
        await File.WriteAllTextAsync(csv, "part_00000000.mkv,0,");
        check((await tail.ReadAsync(csvRoot)).Count == 0 && tail.PendingBytes > 0,
            "Buffer CSV: incomplete row is deferred");
        await File.AppendAllTextAsync(csv, "2\r\n");
        var rows = await tail.ReadAsync(csvRoot);
        check(rows.Count == 1 && rows[0].Bytes == 3 && rows[0].Duration == 2,
            "Buffer CSV: fragmented row commits exactly once");
        var csvHash = await HashAsync(csv);
        var idleRows = 0;
        for (var i = 0; i < 100; i++) idleRows += (await tail.ReadAsync(csvRoot)).Count;
        check(idleRows == 0, "Buffer CSV: 100 unchanged polls read no rows");
        check(tail.BytesRead == new FileInfo(csv).Length && tail.RowsRead == 1 && tail.PendingBytes == 0,
            "Buffer CSV: 100 idle polls reread zero bytes and retain no line memory");
        check(csvHash.AsEnumerable().SequenceEqual(await HashAsync(csv)), "Buffer CSV: recovery log is never rewritten");
        await File.WriteAllTextAsync(csv, "");
        await ExpectFailureAsync(() => tail.ReadAsync(csvRoot), check, "Buffer CSV: truncation fails closed");
        foreach (var malformed in new[] { "../../outside.mkv,0,2", "part_00000000.mkv,NaN,2",
                     "part_00000000.mkv,4,2", "part_00000000.mkv,0,Infinity", new string('x', 17000) })
        {
            await File.WriteAllTextAsync(csv, malformed + "\n");
            await ExpectFailureAsync(() => new SegmentCsvTail().ReadAsync(csvRoot), check,
                "Buffer CSV: malformed path/time/oversized line rejected");
        }

        var source = Path.Combine(work, "source.bin");
        await File.WriteAllBytesAsync(source, Enumerable.Range(0, 1024 * 1024).Select(i => (byte)(i * 31)).ToArray());
        var sourceHash = await HashAsync(source);
        var store = new CommittedSegmentStore();
        var linked = Path.Combine(work, "linked.bin");
        await store.PromoteAsync(source, linked);
        check(store.HardLinkCount == 1 && store.CopiedBytes == 0 && store.CopyCount == 0,
            "Buffer promotion: same-volume NTFS commits a hard link without duplicate writes");
        await store.PromoteAsync(source, linked);
        check(store.HardLinkCount == 1 && sourceHash.AsEnumerable().SequenceEqual(await HashAsync(linked)),
            "Buffer promotion: idempotent existing destination verified by content");
        var copied = Path.Combine(work, "copied.bin");
        await store.PromoteAsync(source, copied, allowHardLink: false);
        check(store.CopyCount == 1 && store.CopiedBytes == new FileInfo(source).Length &&
              sourceHash.AsEnumerable().SequenceEqual(await HashAsync(copied)), "Buffer promotion: copy fallback commits identical bytes");
        var wrong = Path.Combine(work, "wrong.bin");
        await File.WriteAllBytesAsync(wrong, new byte[new FileInfo(source).Length]);
        var wrongHash = await HashAsync(wrong);
        await ExpectFailureAsync(() => store.PromoteAsync(source, wrong), check,
            "Buffer promotion: same-length corrupt destination is not silently reused");
        check(wrongHash.AsEnumerable().SequenceEqual(await HashAsync(wrong)) && sourceHash.AsEnumerable().SequenceEqual(await HashAsync(source)),
            "Buffer promotion: conflict preserves both existing files");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await ExpectFailureAsync(() => store.PromoteAsync(source, Path.Combine(work, "cancelled.bin"), cancelled.Token),
                check, "Buffer promotion: cancellation does not publish destination");
        }
        check(!Directory.EnumerateFiles(work, "*.copying").Any(), "Buffer promotion: no partial copy remains");
        File.Delete(source);
        check(sourceHash.AsEnumerable().SequenceEqual(await HashAsync(linked)) && sourceHash.AsEnumerable().SequenceEqual(await HashAsync(copied)),
            "Buffer eviction: deleting only the source ring entry preserves linked and copied archives");
        await RunSilentRecordingAsync(tools, Path.Combine(work, "short-recording"), check, 12, false);
        await CapacityFailureAsync(tools, Path.Combine(work, "capacity-fault"), check);
        await FailedPromotionDuringStopAsync(tools, Path.Combine(work, "promotion-stop-race"), check);
        await CriticalRegressionsAsync(tools, Path.Combine(work, "critical-regressions"), check);
    }

    public static async Task CriticalRegressionsAsync(FfmpegTools tools, string work, Action<bool, string> check)
    {
        Directory.CreateDirectory(work);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        object? Field(RecordingSession session, string name) => typeof(RecordingSession).GetField(name, flags)!.GetValue(session);
        void Set(RecordingSession session, string name, object? value) => typeof(RecordingSession).GetField(name, flags)!.SetValue(session, value);
        Task Read(RecordingSession session) => (Task)typeof(RecordingSession).GetMethod("ReadSegmentsAsync", flags)!.Invoke(session, null)!;

        var settings = new AppSettings { Mode = CaptureMode.AudioOnly, OutputDirectory = Path.Combine(work, "output"),
            BufferDirectory = Path.Combine(work, "buffers"), AudioInputs = [new("silent-regression", "Synthetic silence", AudioKind.Application, 1)] };
        var ring = Path.Combine(settings.BufferDirectory, Guid.NewGuid().ToString("N"));
        var archive = Path.Combine(settings.OutputDirectory, ".sessions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ring); Directory.CreateDirectory(archive);
        await tools.RunAsync(["-v", "error", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-t", "4",
            "-c:a", "aac", "-f", "segment", "-segment_time", "2", "-reset_timestamps", "1", "-segment_list_type", "csv",
            "-segment_list", Path.Combine(ring, "segments.csv"), Path.Combine(ring, "part_%08d.mkv")]);
        var session = new RecordingSession(settings, null, tools, () => [new SilenceSource()]);
        var journal = new RecordingSession.ContinuousJournal { Directory = archive, BufferDirectory = ring,
            StartSeconds = 0, Tracks = [new(0, "Synthetic silence")] };
        Set(session, "_directory", ring); Set(session, "_continuous", journal);
        Set(session, "_protectedFrom", 0d); Set(session, "_protectedUntil", double.MaxValue);
        var obstructed = Path.Combine(archive, "part_00000001.mkv");
        await File.WriteAllBytesAsync(obstructed, [1]);
        await ExpectFailureAsync(() => Read(session), check, "P1 retry: promotion failure occurs after CSV advance");
        var tail = (SegmentCsvTail)Field(session, "_csvTail")!;
        var consumed = tail.BytesRead;
        check(journal.Segments.Count == 1 && ((System.Collections.IDictionary)Field(session, "_segmentPins")!).Count > 0,
            "P1 retry: failed and subsequent promotions remain pinned rather than disappearing");
        File.Delete(obstructed);
        await Read(session);
        check(tail.BytesRead == consumed && journal.Segments.Count == tail.RowsRead && journal.Segments.Select(s => s.Path).Distinct().Count() == tail.RowsRead,
            "P1 retry: final drain retries consumed CSV rows exactly once without rereading the log");
        check(((System.Collections.IDictionary)Field(session, "_segmentPins")!).Count == 0,
            "P1 retry: pins release only after file and journal commit");

        var readGate = (SemaphoreSlim)Field(session, "_readGate")!;
        var wait = typeof(RecordingSession).GetMethod("WaitForClosedSegmentAsync", flags)!;
        await (Task)wait.Invoke(session, [1d, CancellationToken.None, true])!;
        Task blockedReader;
        object snapshot;
        var originalCount = journal.Segments.Count;
        try
        {
            blockedReader = Read(session);
            await Task.Delay(30);
            check(!blockedReader.IsCompleted && readGate.CurrentCount == 0,
                "P1 detach: verified boundary retains reader gate and blocks new maintenance promotion");
            snapshot = typeof(RecordingSession).GetMethod("SnapshotContinuous", flags)!.Invoke(session, [journal])!;
            Set(session, "_continuous", null);
        }
        finally { readGate.Release(); }
        await blockedReader;
        var segments = (System.Collections.IEnumerable)snapshot.GetType().GetProperty("Segments")!.GetValue(snapshot)!;
        var savedSegments = journal.Segments.ToArray();
        journal.Segments.Clear();
        check(segments.Cast<SegmentInfo>().Count() == originalCount,
            "P1 detach: finalization owns immutable segments, independent of mutable journal list");
        journal.Segments.AddRange(savedSegments);
        Set(session, "_continuous", journal);

        // Even if retry succeeds, a capture fault cannot publish an automatically finalized
        // shortened archive or remove its recovery journal on the subsequent Dispose drain.
        var published = 0;
        session.Saved += _ => published++;
        Set(session, "_faulted", 1);
        ((Stopwatch)Field(session, "_clock")!).Start();
        await Task.Delay(200);
        await ExpectFailureAsync(() => session.DisposeAsync().AsTask(), check,
            "P1 finalize: prior capture fault makes stop report recovery-required failure");
        check(published == 0 && File.Exists(Path.Combine(archive, "session.json")) && Directory.Exists(ring),
            "P1 finalize: faulted drain never publishes incomplete recording or deletes recovery sources");
        var recovered = await RecordingSession.RecoverAsync(settings, tools);
        check(recovered.Count == 1 && recovered[0].Recovered,
            "P1 finalize: preserved journal remains recoverable after the failed finalization");
    }

    private static async Task FailedPromotionDuringStopAsync(FfmpegTools tools, string work, Action<bool, string> check)
    {
        var settings = new AppSettings
        {
            OutputDirectory = Path.Combine(work, "recordings"), BufferDirectory = Path.Combine(work, "ring"),
            Mode = CaptureMode.AudioOnly, ReplaySeconds = 300,
            AudioInputs = [new("silent-stop", "Synthetic silence", AudioKind.Application, 1)]
        };
        var session = new RecordingSession(settings, null, tools, () => [new SilenceSource()]);
        await session.StartAsync(false, true);
        await Task.Delay(4500);
        var ring = Directory.EnumerateDirectories(settings.BufferDirectory).Single();
        var first = Directory.EnumerateFiles(ring, "part_*.mkv").Order().First();
        var hash = await HashAsync(first);
        // A file at the requested snapshot parent forces promotion setup to fail. Request
        // normal stop while SaveReplay waits for its last closed segment, as the UI can do.
        await File.WriteAllTextAsync(Path.Combine(settings.OutputDirectory, ".sessions"), "owned test obstruction");
        var saving = session.SaveReplayAsync();
        var stopping = session.StopAsync();
        await ExpectFailureAsync(() => saving, check, "Buffer stop race: failed snapshot is reported as failure");
        await stopping.WaitAsync(TimeSpan.FromSeconds(20));
        try { await session.DisposeAsync(); } catch (IOException) { }
        check(Directory.Exists(ring) && File.Exists(first) && hash.AsEnumerable().SequenceEqual(await HashAsync(first)),
            "Buffer stop race: late promotion failure prevents normal-stop disposal from deleting recovery sources");
    }

    private static async Task CapacityFailureAsync(FfmpegTools tools, string work, Action<bool, string> check)
    {
        var settings = new AppSettings
        {
            OutputDirectory = Path.Combine(work, "recordings"), BufferDirectory = Path.Combine(work, "ring"),
            Mode = CaptureMode.AudioOnly, ReplaySeconds = 300, BufferLimitBytes = 256L * 1024 * 1024,
            AudioInputs = [new("silent-cap", "Synthetic silence", AudioKind.Application, 1)]
        };
        var session = new RecordingSession(settings, null, tools, () => [new SilenceSource()]);
        var fault = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Faulted += message => fault.TrySetResult(message);
        await session.StartAsync(true, true);
        await Task.Delay(2500);
        var ring = Directory.EnumerateDirectories(settings.BufferDirectory).Single();
        var oversized = Path.Combine(ring, "part_99999999.mkv");
        // Hold the promotion/maintenance gate to reproduce a destination drive stalled during
        // a pinned replay save. The guard must not depend on that gate making progress.
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var readGate = (SemaphoreSlim)typeof(RecordingSession).GetField("_readGate", flags)!.GetValue(session)!;
        var stateGate = (SemaphoreSlim)typeof(RecordingSession).GetField("_segmentGate", flags)!.GetValue(session)!;
        await readGate.WaitAsync();
        try
        {
            await stateGate.WaitAsync();
            try
            {
                typeof(RecordingSession).GetField("_protectedFrom", flags)!.SetValue(session, 0d);
                typeof(RecordingSession).GetField("_protectedUntil", flags)!.SetValue(session, double.MaxValue);
            }
            finally { stateGate.Release(); }
            using (var file = new FileStream(oversized, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                file.SetLength(settings.BufferLimitBytes + 65L * 1024 * 1024);
            var message = await fault.Task.WaitAsync(TimeSpan.FromSeconds(4));
            check(message.Contains("한도 초과") && message.Contains("64MiB"),
                "Buffer cap: independent watchdog reports explicit cap failure while pinned maintenance is blocked");
            var encoder = (Process)typeof(RecordingSession).GetField("_encoder", flags)!.GetValue(session)!;
            await encoder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4));
            check(encoder.HasExited, "Buffer cap: encoder stops before the blocked promotion gate is released");
        }
        finally
        {
            readGate.Release();
            try { await session.StopAsync(); } catch (IOException) { }
            try { await session.DisposeAsync(); } catch (IOException) { }
        }
        check(Directory.Exists(ring) && Directory.EnumerateFiles(Path.Combine(settings.OutputDirectory, ".sessions"),
            "session.json", SearchOption.AllDirectories).Any(), "Buffer cap: fault preserves ring and recovery journal");
        // Remove only this deliberately oversized test file, then recover the real closed media.
        File.Delete(oversized);
        var csv = Path.Combine(ring, "segments.csv");
        var csvHash = await HashAsync(csv);
        var first = Directory.EnumerateFiles(ring, "part_*.mkv").Order().First();
        var firstHash = await HashAsync(first);
        var recovered = await RecordingSession.RecoverAsync(settings, tools);
        check(recovered.Count == 1 && recovered[0].Recovered,
            "Buffer cap: closed media recovers after capacity failure");
        check(csvHash.AsEnumerable().SequenceEqual(await HashAsync(csv)) && firstHash.AsEnumerable().SequenceEqual(await HashAsync(first)),
            "Buffer cap: recovery preserves source bytes and original CSV");
        await tools.RunAsync(["-v", "error", "-i", recovered[0].MediaPath, "-map", "0", "-f", "null", "-"]);
        check(true, "Buffer cap: recovered media decodes without audio output");
    }

    /// <summary>Call with seconds >=305 for the final five-minute replay test. The file records
    /// deterministic synthetic pixels and zero PCM only; it never accesses speakers or microphones.</summary>
    public static async Task SoakAsync(FfmpegTools tools, string work, Action<bool, string> check, int seconds = 305)
    {
        if (seconds < 305) throw new ArgumentOutOfRangeException(nameof(seconds));
        await RunSilentRecordingAsync(tools, work, check, seconds, true);
    }

    private static async Task RunSilentRecordingAsync(FfmpegTools tools, string work, Action<bool, string> check,
        int seconds, bool video)
    {
        Directory.CreateDirectory(work);
        var settings = new AppSettings
        {
            OutputDirectory = Path.Combine(work, "recordings"), BufferDirectory = Path.Combine(work, "ring"),
            Mode = video ? CaptureMode.ScreenAndAudio : CaptureMode.AudioOnly,
            Height = 720, FramesPerSecond = 30, Quality = Quality.Standard,
            ReplaySeconds = seconds >= 305 ? 300 : 30, BufferLimitBytes = 256L * 1024 * 1024,
            AudioInputs = [new("silent-fixture", "Synthetic silence", AudioKind.Application, 1)]
        };
        var pixels = new byte[640 * 360 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        { pixels[i] = (byte)(i / 4 % 251); pixels[i + 1] = 99; pixels[i + 2] = 35; pixels[i + 3] = 255; }
        long sequence = 0;
        var faults = new ConcurrentQueue<string>();
        var saved = new ConcurrentQueue<RecordingManifest>();
        var snapshots = new List<object>();
        var markLatency = new List<double>();
        var saveLatency = new List<double>();
        var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        await using var session = new RecordingSession(settings, null, tools, () => [new SilenceSource()],
            video ? () => new VideoFrame(pixels, 640, 360, Interlocked.Increment(ref sequence)) : null);
        session.Faulted += faults.Enqueue;
        session.Saved += saved.Enqueue;
        await session.StartAsync(true, true);
        var started = Stopwatch.StartNew();
        var marked = false;
        Task<RecordingManifest>? earlySave = null;
        while (started.Elapsed.TotalSeconds < seconds)
        {
            await Task.Delay(1000);
            check(faults.IsEmpty, "Silent buffer healthy @" + (int)started.Elapsed.TotalSeconds + "s");
            if (!marked && started.Elapsed.TotalSeconds >= Math.Min(5, seconds / 2d))
            {
                earlySave = session.SaveReplayAsync();
                var timer = Stopwatch.StartNew();
                await session.AddBookmarkAsync();
                markLatency.Add(timer.Elapsed.TotalMilliseconds);
                marked = true;
            }
            var stats = session.BufferStats;
            process.Refresh();
            snapshots.Add(new { Seconds = started.Elapsed.TotalSeconds, Stats = stats,
                ManagedBytes = GC.GetTotalMemory(false), WorkingSetBytes = process.WorkingSet64,
                ProcessCpuSeconds = (process.TotalProcessorTime - cpuStart).TotalSeconds,
                session.RepeatedFrames, session.LateFrames });
            if ((int)started.Elapsed.TotalSeconds % 30 == 0)
                Console.WriteLine(JsonSerializer.Serialize(snapshots[^1]));
        }
        if (earlySave != null) await earlySave;
        var beforeSave = session.BufferStats;
        check(beforeSave.CsvRowsRead >= seconds / 2 - 3, "Silent buffer: complete segment history observed");
        check(beforeSave.ClosedSegments <= settings.ReplaySeconds / 2 + 3, "Silent buffer: ring segment metadata remains bounded");
        check(beforeSave.BufferBytes <= settings.BufferLimitBytes + 64L * 1024 * 1024,
            "Silent buffer: ring stays within configured cap plus open-segment reserve");
        if (seconds >= 305)
        {
            check(beforeSave.RetainedSeconds >= 299.9 && beforeSave.EvictedSegments > 0,
                "Silent buffer: full five-minute window retained while old segments actually expire");
            check(beforeSave.DiskAuditCount < beforeSave.MaintenanceCount / 3,
                "Silent buffer: full disk scans are throttled while maintenance continues");
        }
        for (var i = 0; i < 2; i++)
        {
            var timer = Stopwatch.StartNew();
            var saving = session.SaveReplayAsync();
            var mark = Stopwatch.StartNew();
            await session.AddBookmarkAsync();
            markLatency.Add(mark.Elapsed.TotalMilliseconds);
            var replay = await saving;
            saveLatency.Add(timer.Elapsed.TotalMilliseconds);
            var info = await tools.ProbeAsync(replay.MediaPath);
            check(session.IsRunning && session.IsBuffering && session.IsRecording,
                "Silent buffer: repeated save and bookmark leave capture running");
            check(Math.Abs(info.DurationSeconds - Math.Min(settings.ReplaySeconds, replay.DurationSeconds)) < .2,
                "Silent buffer: saved replay duration probes correctly");
            if (seconds >= 305) check(Math.Abs(info.DurationSeconds - 300) < .2,
                "Silent buffer: exact five-minute replay saved");
            await tools.RunAsync(["-v", "error", "-i", replay.MediaPath, "-map", "0", "-f", "null", "-"]);
            check(true, "Silent buffer: saved video/audio decode fully without playback");
        }
        var finalStats = session.BufferStats;
        await session.StopAsync();
        check(faults.IsEmpty && saved.Count == 4, "Silent buffer: three replay files and continuous original finalized");
        var original = saved.Last();
        check(original.DurationSeconds >= seconds - .2 && original.Edits?.Bookmarks.Count == 3,
            "Silent buffer: continuous original and every recorded bookmark survive ring eviction");
        check(finalStats.HardLinkCount > 0 && finalStats.CopyCount == 0,
            "Silent buffer: same-volume recording and replay promotion avoid payload copies");
        await File.WriteAllTextAsync(Path.Combine(work, "soak-results.json"), JsonSerializer.Serialize(new
        {
            Synthetic = true, Silent = true, RequestedSeconds = seconds, WallSeconds = started.Elapsed.TotalSeconds,
            ParentProcessOnlyCpu = true, Encoder = session.EncoderName, FrameSource = "640x360 deterministic pixels; output 720p30",
            FinalStats = finalStats, BookmarkLatencyMilliseconds = markLatency, ReplaySaveMilliseconds = saveLatency,
            Snapshots = snapshots, Recordings = saved.ToArray(), Faults = faults.ToArray()
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Buffer results: " + Path.Combine(work, "soak-results.json"));
    }

    public static async Task BenchmarkAsync(string work)
    {
        Directory.CreateDirectory(work);
        // Model one hour of 2s segments and ten 200ms maintenance polls per segment.
        // Both paths stat each newly closed owned file. Baseline matches old ReadToEnd/Split.
        const int segmentCount = 1800, pollsPerSegment = 10;
        var csvRoot = Path.Combine(work, "csv");
        Directory.CreateDirectory(csvRoot);
        for (var i = 0; i < segmentCount; i++) await File.WriteAllBytesAsync(Path.Combine(csvRoot, $"part_{i:00000000}.mkv"), [42]);
        async Task<object> MeasureCsvAsync(bool incremental)
        {
            var csv = Path.Combine(csvRoot, "segments.csv");
            await File.WriteAllTextAsync(csv, "");
            var tail = new SegmentCsvTail();
            long bytes = 0, records = 0;
            var readLines = 0;
            var allocatedStart = GC.GetTotalAllocatedBytes(true);
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < segmentCount; i++)
            {
                await File.AppendAllTextAsync(csv, $"part_{i:00000000}.mkv,{i * 2},{i * 2 + 2}\n");
                for (var poll = 0; poll < pollsPerSegment; poll++)
                {
                    if (incremental) { records += (await tail.ReadAsync(csvRoot)).Count; continue; }
                    using var stream = new FileStream(csv, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    bytes += stream.Length;
                    using var reader = new StreamReader(stream);
                    var lines = (await reader.ReadToEndAsync()).Split('\n');
                    for (; readLines < lines.Length - 1; readLines++)
                    {
                        var line = lines[readLines].Trim();
                        var last = line.LastIndexOf(',');
                        var previous = line.LastIndexOf(',', last - 1);
                        var path = Path.Combine(csvRoot, Path.GetFileName(line[..previous].Trim('"')));
                        if (double.TryParse(line[(previous + 1)..last], CultureInfo.InvariantCulture, out _) &&
                            double.TryParse(line[(last + 1)..], CultureInfo.InvariantCulture, out _) && File.Exists(path))
                        { _ = new FileInfo(path).Length; records++; }
                    }
                }
            }
            watch.Stop();
            if (records != segmentCount) throw new InvalidOperationException("Benchmark lost CSV rows.");
            return new { Milliseconds = watch.Elapsed.TotalMilliseconds, BytesRead = incremental ? tail.BytesRead : bytes,
                AllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocatedStart, Rows = records };
        }
        var csvBaseline = await MeasureCsvAsync(false);
        var csvOptimized = await MeasureCsvAsync(true);
        const int files = 150, size = 2 * 1024 * 1024;
        var payloadRoot = Path.Combine(work, "payloads");
        Directory.CreateDirectory(payloadRoot);
        var payload = new byte[size];
        new Random(907).NextBytes(payload);
        var sources = Enumerable.Range(0, files).Select(i => Path.Combine(payloadRoot, $"part_{i:00000000}.mkv")).ToArray();
        foreach (var source in sources) await File.WriteAllBytesAsync(source, payload);
        var baselineTimes = new List<double>();
        var optimizedTimes = new List<double>();
        long linkedCount = 0, copiedBytes = 0;
        for (var round = 0; round < 3; round++)
        {
            // Alternate order to reduce cache/order bias. Payload count and hashes match.
            foreach (var optimized in round % 2 == 0 ? new[] { false, true } : new[] { true, false })
            {
                var store = new CommittedSegmentStore();
                var destinationRoot = Path.Combine(work, (optimized ? "links" : "copies") + round);
                Directory.CreateDirectory(destinationRoot);
                var watch = Stopwatch.StartNew();
                foreach (var source in sources)
                {
                    var destination = Path.Combine(destinationRoot, Path.GetFileName(source));
                    if (optimized) await store.PromoteAsync(source, destination);
                    else { File.Copy(source, destination + ".copying", true); File.Move(destination + ".copying", destination, true); }
                }
                watch.Stop();
                (optimized ? optimizedTimes : baselineTimes).Add(watch.Elapsed.TotalMilliseconds);
                if (optimized) { linkedCount += store.HardLinkCount; copiedBytes += store.CopiedBytes; }
                foreach (var i in new[] { 0, files / 2, files - 1 })
                    if (!(await HashAsync(sources[i])).AsEnumerable().SequenceEqual(await HashAsync(Path.Combine(destinationRoot, Path.GetFileName(sources[i])))))
                        throw new InvalidOperationException("Benchmark changed source/destination content.");
                // Only the exact destinations just created by this benchmark are removed.
                foreach (var source in sources) File.Delete(Path.Combine(destinationRoot, Path.GetFileName(source)));
            }
        }
        var result = new
        {
            Workload = "Synthetic owned files; accelerated 1h CSV polling; 150 x 2MiB segment promotion, three alternating-order rounds",
            CsvBaseline = csvBaseline, CsvOptimized = csvOptimized,
            PromotionBaselineMilliseconds = baselineTimes, PromotionOptimizedMilliseconds = optimizedTimes,
            BaselinePayloadBytesWritten = (long)files * size * 3, OptimizedPayloadBytesWritten = copiedBytes,
            OptimizedHardLinks = linkedCount, SourcesPreserved = true,
            Caveat = "Warm OS caches; no durable flush or game FPS benchmark. Timing includes filesystem metadata; CSV allocations are process-wide."
        };
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(work, "benchmark.json"), json);
        Console.WriteLine(json);
    }

    private static async Task<byte[]> HashAsync(string path)
    { await using var stream = File.OpenRead(path); return await SHA256.HashDataAsync(stream); }

    private static async Task ExpectFailureAsync(Func<Task> action, Action<bool, string> check, string message)
    {
        try { await action(); }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException) { check(true, message); return; }
        check(false, message);
    }

    private sealed class SilenceSource : IAudioSource
    {
        public string Name => "Synthetic silence";
        public float Peak => 0;
        public event Action<string>? Faulted { add { } remove { } }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public int Read(float[] buffer, int offset, int count) { Array.Clear(buffer, offset, count); return count; }
    }
}
