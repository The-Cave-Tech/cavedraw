using Avalonia.Threading;

namespace VCCad.App.Capture;

/// <summary>The raw frame the recorder wants: BGRA pixels, and the shape they are in.</summary>
public readonly record struct CaptureFrame(byte[] Bgra, int Width, int Height, int Stride);

/// <summary>
/// The app's one recording, and the timer that drives it.
///
/// A single recording at a time, deliberately: two recorders writing the same window would double the cost of
/// the thing being measured and produce two files that disagree about what happened. Starting one while
/// another runs is an error the caller is told about, not a silent replacement.
///
/// The timer ticks on the UI thread because capturing the visual tree has to; encoding does not, and happens on
/// the recorder's own thread.
/// </summary>
public static class CanvasRecording
{
    private static VideoRecorder? _recorder;
    private static DispatcherTimer? _timer;
    private static Func<CaptureFrame?>? _source;

    public static bool IsRecording => _recorder is { IsRecording: true };

    /// <summary>The encoder that would be used, or null. Reported so a caller knows before starting.</summary>
    public static string? Encoder => VideoRecorder.FindEncoder();

    /// <summary>
    /// Starts recording, taking frames from <paramref name="source"/> at <paramref name="fps"/>.
    ///
    /// The first frame decides the recording's size, and the source keeps producing that size: a window
    /// resized mid-recording is captured at the size the recording started with, because a video whose frames
    /// change shape is not one an encoder can write.
    /// </summary>
    public static object Start(Func<CaptureFrame?> source, string path, int fps, string? encoder = null)
    {
        if (_recorder is { IsRecording: true })
        {
            throw new InvalidOperationException("A recording is already running. Stop it first.");
        }

        if (fps is < 1 or > 60)
        {
            throw new ArgumentOutOfRangeException(nameof(fps), fps, "Frame rate must be between 1 and 60.");
        }

        string? ffmpeg = VideoRecorder.FindEncoder(encoder);
        if (ffmpeg is null)
        {
            throw new InvalidOperationException(
                "No ffmpeg found to encode with. Set VCCAD_FFMPEG to its path, or put ffmpeg on the PATH. " +
                "Recording is FFV1, which ffmpeg provides.");
        }

        CaptureFrame? first = source();
        if (first is not { } frame)
        {
            throw new InvalidOperationException("The window has no size yet, so there is nothing to record.");
        }

        var recorder = new VideoRecorder();
        recorder.Start(path, frame.Width, frame.Height, fps, ffmpeg);
        recorder.AddFrame(frame.Bgra);

        _source = source;
        _recorder = recorder;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / fps) };
        timer.Tick += (_, _) => OnTick();
        timer.Start();
        _timer = timer;

        return new
        {
            recording = true,
            path = recorder.OutputPath,
            fps,
            width = frame.Width,
            height = frame.Height,
            encoder = ffmpeg,
        };
    }

    private static void OnTick()
    {
        if (_recorder is null || _source is null)
        {
            return;
        }

        // A capture that throws must not kill the recording: a frame is skipped and the count says so.
        try
        {
            if (_source() is { } frame)
            {
                _recorder.AddFrame(frame.Bgra);
            }
        }
        catch (Exception)
        {
            // Nothing to do: the recording continues and Stop() reports what it actually caught.
        }
    }

    /// <summary>Finishes the recording and reports what was written.</summary>
    public static CaptureResult Stop()
    {
        VideoRecorder? recorder = _recorder;
        if (recorder is null)
        {
            throw new InvalidOperationException("No recording is running.");
        }

        _timer?.Stop();
        _timer = null;
        _source = null;
        _recorder = null;

        return recorder.Stop();
    }

    public static object Status()
    {
        VideoRecorder? recorder = _recorder;
        if (recorder is null)
        {
            return new { recording = false, encoder = VideoRecorder.FindEncoder() };
        }

        return new
        {
            recording = recorder.IsRecording,
            path = recorder.OutputPath,
            fps = recorder.Fps,
            width = recorder.Width,
            height = recorder.Height,
            frames = recorder.Frames,
            dropped = recorder.Dropped,
        };
    }
}
