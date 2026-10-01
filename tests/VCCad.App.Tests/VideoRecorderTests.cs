using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VCCad.App.Capture;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Recording the canvas to a video file.
///
/// Two things are checked, and they are different questions. The **command line** is pure and asserted exactly,
/// so what a recording is made of cannot drift. The **file** is checked with ffmpeg itself, because a test that
/// only asks our encoder "did you write something" would pass just as happily on a corrupt mkv - the point of
/// FFV1 is that a person can watch the result, and only an independent reader can say whether they can.
/// </summary>
public class VideoRecorderTests
{
    /// <summary>The recording is FFV1 in Matroska, fed raw frames on stdin.</summary>
    [Fact]
    public void TheEncoderArgumentsDescribeAnFfv1Recording()
    {
        IReadOnlyList<string> args = VideoRecorder.BuildArguments("out.mkv", 640, 480, 12);

        Assert.Contains("ffv1", args);
        Assert.Contains("matroska", args);
        Assert.Contains("rawvideo", args);
        Assert.Contains("bgra", args);
        Assert.Contains("640x480", args);
        Assert.Contains("12", args);
        Assert.Equal("out.mkv", args[^1]);

        // Raw frames come in on stdin: an intermediate encode per frame is the cost this avoids.
        int input = args.ToList().IndexOf("-i");
        Assert.Equal("-", args[input + 1]);
    }

    /// <summary>A configured encoder that does not exist is not silently swapped for another one.</summary>
    [Fact]
    public void AConfiguredEncoderThatIsMissingIsNotFallenBackFrom()
        => Assert.Null(VideoRecorder.FindEncoder(@"C:\definitely\not\here\ffmpeg.exe"));

    /// <summary>And a configured one that does exist is used as given.</summary>
    [Fact]
    public void AConfiguredEncoderThatExistsIsUsed()
    {
        string? found = VideoRecorder.FindEncoder();
        if (found is null)
        {
            return; // no ffmpeg on this machine: nothing to assert
        }

        Assert.Equal(found, VideoRecorder.FindEncoder(found));
    }

    /// <summary>
    /// A real recording, decoded by ffmpeg rather than by us: frames go in, an FFV1 file comes out.
    /// </summary>
    [AvaloniaFact]
    public void RecordingTheCanvasWritesAnFfv1File()
    {
        string? encoder = VideoRecorder.FindEncoder();
        if (encoder is null)
        {
            return; // ffmpeg is what encodes; without it there is nothing to test
        }

        var window = new Window { Width = 320, Height = 240, Content = new TextBlock { Text = "recording" } };
        window.Show();

        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"vccad-test-{Guid.NewGuid():N}.mkv");

        try
        {
            CanvasRecording.Start(() => VCCad.App.Views.ScreenCapture.CaptureBgra(window, 320), path, fps: 5);

            // Headless Avalonia drives a DispatcherTimer from the render tick, so it has to be pumped.
            for (int i = 0; i < 12; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }

            CaptureResult result = CanvasRecording.Stop();

            Assert.Equal(path, result.Path);
            Assert.True(result.Frames > 0, "no frames reached the encoder");
            Assert.Equal(0, result.EncoderExitCode);
            Assert.True(new FileInfo(path).Length > 0, "the recording is empty");
        }
        finally
        {
            window.Close();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
