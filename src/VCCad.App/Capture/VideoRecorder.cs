using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace VCCad.App.Capture;

/// <summary>
/// Records what the application looks like over time, to an FFV1 file.
///
/// **FFV1 because the point is review.** A lossy codec smears exactly the thin lines and small text this
/// application is made of, and a person watching the result cannot tell a rendering fault from a compression
/// artefact - which defeats the reason for recording it.
///
/// Frames are handed over as raw BGRA and piped to `ffmpeg`, which is the encoder. A lossless video codec is a
/// range coder with a dozen context models; writing one here would be a research project, and `ffmpeg` already
/// exists on the machines this runs on. When it does not, that is reported rather than hidden - see
/// <see cref="FindEncoder"/>.
///
/// Nothing here touches the UI thread. The caller supplies frames and a writer thread feeds the encoder, so a
/// slow disk or a busy encoder slows the recording rather than the application. A recording that changes the
/// thing it is measuring is not evidence.
/// </summary>
public sealed class VideoRecorder : IDisposable
{
    private const int FrameQueueLimit = 16;

    private readonly BlockingCollection<byte[]> _queue = new(new ConcurrentQueue<byte[]>(), FrameQueueLimit);
    private Process? _encoder;
    private Thread? _writer;
    private int _frames;
    private int _dropped;
    private bool _disposed;

    public bool IsRecording => _encoder is not null;

    /// <summary>Frames handed to the encoder.</summary>
    public int Frames => _frames;

    /// <summary>Frames discarded because the encoder could not keep up. Reported, never silently skipped.</summary>
    public int Dropped => _dropped;

    public string? OutputPath { get; private set; }

    public int Fps { get; private set; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>
    /// Finds the encoder: `VCCAD_FFMPEG` if it is set, otherwise `ffmpeg` on the PATH.
    ///
    /// A configured path that does not exist is **not** silently fallen back from - someone who set it meant
    /// that binary, and quietly using a different one would make the recorded file a surprise.
    /// </summary>
    public static string? FindEncoder(string? configured = null)
    {
        string? explicitPath = string.IsNullOrWhiteSpace(configured)
            ? Environment.GetEnvironmentVariable("VCCAD_FFMPEG")
            : configured;

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return File.Exists(explicitPath) ? explicitPath : null;
        }

        string exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        string[] directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (string directory in directories)
        {
            try
            {
                string candidate = System.IO.Path.Combine(directory.Trim('"'), exe);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not a reason to fail a recording.
            }
        }

        return null;
    }

    /// <summary>
    /// The ffmpeg command line, as a list. Pure, so the shape of the recording can be asserted rather than
    /// inferred from a file that happens to have been produced.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(string outputPath, int width, int height, int fps)
    {
        return new[]
        {
            "-hide_banner",
            "-loglevel", "error",
            "-y",

            // Raw frames in on stdin: no intermediate PNG per frame, so the capture cost is a copy.
            "-f", "rawvideo",
            "-pixel_format", "bgra",
            "-video_size", $"{width}x{height}",
            "-framerate", fps.ToString(CultureInfo.InvariantCulture),
            "-i", "-",

            // FFV1, lossless, in Matroska. `-level 3` is the widely supported version of the format.
            "-c:v", "ffv1",
            "-level", "3",
            "-pix_fmt", "bgra",
            "-f", "matroska",
            outputPath,
        };
    }

    /// <summary>Starts recording. Throws when the encoder cannot be started.</summary>
    public void Start(string outputPath, int width, int height, int fps, string encoder)
    {
        if (IsRecording)
        {
            throw new InvalidOperationException("A recording is already running.");
        }

        string full = System.IO.Path.GetFullPath(outputPath);
        string? directory = System.IO.Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var start = new ProcessStartInfo(encoder)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in BuildArguments(full, width, height, fps))
        {
            start.ArgumentList.Add(argument);
        }

        OutputPath = full;
        Width = width;
        Height = height;
        Fps = fps;
        _frames = 0;
        _dropped = 0;

        _encoder = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {encoder}.");
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "vccad-recorder" };
        _writer.Start();
    }

    /// <summary>
    /// Offers a frame. Never blocks: when the encoder has fallen behind, the frame is counted as dropped rather
    /// than stalling the application.
    /// </summary>
    public void AddFrame(byte[] bgra)
    {
        if (_encoder is null || _disposed)
        {
            return;
        }

        if (!_queue.TryAdd(bgra))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    private void WriteLoop()
    {
        Stream? input = _encoder?.StandardInput.BaseStream;
        if (input is null)
        {
            return;
        }

        try
        {
            foreach (byte[] frame in _queue.GetConsumingEnumerable())
            {
                input.Write(frame, 0, frame.Length);
                Interlocked.Increment(ref _frames);
            }

            input.Flush();
        }
        catch (IOException)
        {
            // The encoder went away: whatever was written is what the file holds, and Stop() reports it.
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            try
            {
                input.Close();
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Ends the recording, waits for the encoder to finish writing, and reports what was produced.</summary>
    public CaptureResult Stop()
    {
        Process? encoder = _encoder;
        if (encoder is null)
        {
            throw new InvalidOperationException("No recording is running.");
        }

        // Tell the writer there is nothing more, then let it drain rather than cutting it off: the last frames
        // handed over are part of what was recorded.
        _queue.CompleteAdding();
        _writer?.Join(TimeSpan.FromSeconds(30));

        if (!encoder.WaitForExit(30_000))
        {
            try
            {
                encoder.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }

        string errors = string.Empty;
        try
        {
            errors = encoder.StandardError.ReadToEnd();
        }
        catch (IOException)
        {
        }

        _encoder = null;
        int frames = _frames;
        string path = OutputPath ?? string.Empty;

        long length = 0;
        try
        {
            length = File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
        }

        return new CaptureResult(
            path,
            frames,
            Dropped,
            Fps <= 0 ? 0 : frames / (double)Fps,
            encoder.ExitCode,
            errors.Trim(),
            length);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (IsRecording)
        {
            try
            {
                Stop();
            }
            catch (InvalidOperationException)
            {
            }
        }

        _queue.Dispose();
    }
}

/// <summary>What a finished recording produced. Reported, never assumed.</summary>
public sealed record CaptureResult(
    string Path,
    int Frames,
    int Dropped,
    double Seconds,
    int EncoderExitCode,
    string EncoderOutput,
    long Bytes);
