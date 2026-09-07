using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using NAudio.Wave;
using Windows.AI.MachineLearning;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace DvdLogoApp;

// A frame is always BGRA, which matches the texture format used by the 3D TV.
public sealed class InputFrameEventArgs : EventArgs
{
    private readonly Action<byte[]>? releasePixels;
    private int isReleased;

    public InputFrameEventArgs(
        byte[] pixels,
        int width,
        int height,
        int stride,
        long sequence = 0,
        Action<byte[]>? releasePixels = null)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
        Sequence = sequence;
        this.releasePixels = releasePixels;
    }

    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public long Sequence { get; }

    public void Release()
    {
        if (releasePixels is not null && Interlocked.Exchange(ref isReleased, 1) == 0)
        {
            releasePixels(Pixels);
        }
    }
}

public sealed class CaptureErrorEventArgs : EventArgs
{
    public CaptureErrorEventArgs(Exception exception)
    {
        Exception = exception;
    }

    public Exception Exception { get; }
}

// Plays a video through an FFmpeg executable and emits raw BGRA frames.
public sealed class FfmpegVideoSource : IAsyncDisposable
{
    private const int OutputWidth = 640;
    private const int OutputHeight = 424;
    private const int BytesPerPixel = 4;

    private Process? process;
    private Process? audioProcess;
    private CancellationTokenSource? cancellation;
    private Task? readTask;
    private Task? presentationTask;
    private Channel<InputFrameEventArgs>? localFrames;
    private bool localTimerResolutionActive;

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeEndPeriod(uint milliseconds);
    private Task? audioReadTask;
    private string? audioFilePath;
    private BufferedWaveProvider? audioBuffer;
    private VintageSpeakerWaveProvider? speakerProvider;
    private WaveOut? audioOutput;
    private int audioPlaybackStarted;
    private string? playbackFfmpegPath;
    private string? playbackVideoSource;
    private string? playbackAudioSource;
    private bool playbackIsFile;
    private TimeSpan playbackPosition;
    private readonly Stopwatch playbackClock = new();

    public event EventHandler<InputFrameEventArgs>? FrameReady;
    public event EventHandler? Ended;

    public async Task StartAsync(string ffmpegPath, string videoPath, CancellationToken cancellationToken = default)
    {
        playbackFfmpegPath = ffmpegPath;
        playbackVideoSource = videoPath;
        playbackAudioSource = videoPath;
        playbackIsFile = true;
        playbackPosition = TimeSpan.Zero;
        await StartCombinedAsync(
            ffmpegPath,
            videoPath,
            videoPath,
            isFile: true,
            startPosition: TimeSpan.Zero,
            cancellationToken).ConfigureAwait(false);
    }

    // Starts a direct media stream resolved from a YouTube URL.
    public async Task StartStreamAsync(string ffmpegPath, string streamUrl, CancellationToken cancellationToken = default)
    {
        playbackFfmpegPath = ffmpegPath;
        playbackVideoSource = streamUrl;
        playbackAudioSource = streamUrl;
        playbackIsFile = false;
        playbackPosition = TimeSpan.Zero;
        await StartCombinedAsync(
            ffmpegPath,
            streamUrl,
            streamUrl,
            isFile: false,
            startPosition: TimeSpan.Zero,
            cancellationToken).ConfigureAwait(false);
    }

    // Starts separate adaptive video and audio streams from YouTube.
    public async Task StartStreamPairAsync(
        string ffmpegPath,
        string videoStreamUrl,
        string audioStreamUrl,
        CancellationToken cancellationToken = default)
    {
        playbackFfmpegPath = ffmpegPath;
        playbackVideoSource = videoStreamUrl;
        playbackAudioSource = audioStreamUrl;
        playbackIsFile = false;
        playbackPosition = TimeSpan.Zero;
        await StartCombinedAsync(
            ffmpegPath,
            videoStreamUrl,
            audioStreamUrl,
            isFile: false,
            startPosition: TimeSpan.Zero,
            cancellationToken).ConfigureAwait(false);
    }

    public bool CanSeek => playbackFfmpegPath is not null
                           && playbackVideoSource is not null
                           && playbackAudioSource is not null;

    public async Task<bool> SkipAsync(TimeSpan amount, CancellationToken cancellationToken = default)
    {
        if (!CanSeek)
        {
            return false;
        }

        var target = playbackPosition + playbackClock.Elapsed + amount;
        if (target < TimeSpan.Zero)
        {
            target = TimeSpan.Zero;
        }

        playbackPosition = target;
        await StartCombinedAsync(
            playbackFfmpegPath!,
            playbackVideoSource!,
            playbackAudioSource!,
            playbackIsFile,
            target,
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task StartCombinedAsync(
        string ffmpegPath,
        string videoSource,
        string audioSource,
        bool isFile,
        TimeSpan startPosition,
        CancellationToken cancellationToken)
    {
        await StopAsync().ConfigureAwait(false);

        cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"DvdLogoAudio_{Guid.NewGuid():N}.pcm");
        audioFilePath = outputPath;

        var waveFormat = new WaveFormat(44_100, 16, 2);
        audioBuffer = new BufferedWaveProvider(waveFormat)
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
        speakerProvider = new VintageSpeakerWaveProvider(audioBuffer);
        audioOutput = new WaveOut
        {
            BufferMilliseconds = 80,
            NumberOfBuffers = 3
        };
        audioOutput.Init(speakerProvider);

        process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = isFile
                    ? BuildCombinedFileArguments(videoSource, outputPath, startPosition)
                    : BuildCombinedStreamArguments(videoSource, audioSource, outputPath, startPosition),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            },
            EnableRaisingEvents = true
        };
        process.Exited += Process_Exited;

        if (!process.Start())
        {
            throw new InvalidOperationException("FFmpeg could not be started.");
        }

        _ = DrainErrorOutputAsync(process.StandardError, cancellation.Token);
        audioPlaybackStarted = 0;
        localTimerResolutionActive = isFile && timeBeginPeriod(1) == 0;
        localFrames = isFile ? Channel.CreateBounded<InputFrameEventArgs>(120) : null;
        readTask = ReadFramesAsync(process, cancellation.Token);
        audioReadTask = ReadAudioFileAsync(outputPath, cancellation.Token);
        presentationTask = localFrames is null ? null : PresentLocalFramesAsync(localFrames, cancellation.Token);
        playbackClock.Restart();
    }

    public void StartSynchronizedAudio()
    {
        if (Interlocked.Exchange(ref audioPlaybackStarted, 1) != 0)
        {
            return;
        }

        if (!playbackIsFile) audioBuffer?.ClearBuffer();
        audioOutput?.Play();
    }

    private async Task StartCoreAsync(
        string ffmpegPath,
        string videoArguments,
        string audioArguments,
        CancellationToken cancellationToken)
    {
        await StopAsync().ConfigureAwait(false);

        cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await TryStartAudioAsync(ffmpegPath, audioArguments, cancellation.Token).ConfigureAwait(false);

        process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = videoArguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            },
            EnableRaisingEvents = true
        };
        process.Exited += Process_Exited;

        if (!process.Start())
        {
            throw new InvalidOperationException("FFmpeg could not be started.");
        }

        _ = DrainErrorOutputAsync(process.StandardError, cancellation.Token);
        readTask = ReadFramesAsync(process, cancellation.Token);
        audioOutput?.Play();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        audioOutput?.Stop();
        Interlocked.Exchange(ref audioPlaybackStarted, 0);
        cancellation?.Cancel();

        StopProcess(process);
        StopProcess(audioProcess);
        playbackClock.Stop();

        await IgnoreReaderAsync(readTask).ConfigureAwait(false);
        await IgnoreReaderAsync(presentationTask).ConfigureAwait(false);
        if (localFrames is not null)
        {
            while (localFrames.Reader.TryRead(out var pending)) pending.Release();
        }
        localFrames = null;
        presentationTask = null;
        if (localTimerResolutionActive)
        {
            timeEndPeriod(1);
            localTimerResolutionActive = false;
        }
        await IgnoreReaderAsync(audioReadTask).ConfigureAwait(false);

        process?.Dispose();
        audioProcess?.Dispose();
        audioOutput?.Dispose();
        process = null;
        audioProcess = null;
        readTask = null;
        audioReadTask = null;
        audioOutput = null;
        audioBuffer = null;
        speakerProvider = null;
        var audioFileToDelete = audioFilePath;
        audioFilePath = null;
        cancellation?.Dispose();
        cancellation = null;

        if (audioFileToDelete is not null)
        {
            TryDeleteFile(audioFileToDelete);
        }
    }

    private async Task TryStartAudioAsync(string ffmpegPath, string audioArguments, CancellationToken cancellationToken)
    {
        try
        {
            var waveFormat = new WaveFormat(44_100, 16, 2);
            audioBuffer = new BufferedWaveProvider(waveFormat)
            {
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };
            speakerProvider = new VintageSpeakerWaveProvider(audioBuffer);
            audioOutput = new WaveOut
            {
                BufferMilliseconds = 80,
                NumberOfBuffers = 3
            };
            audioOutput.Init(speakerProvider);

            audioProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = audioArguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            if (!audioProcess.Start())
            {
                throw new InvalidOperationException("FFmpeg audio could not be started.");
            }

            _ = DrainErrorOutputAsync(audioProcess.StandardError, cancellationToken);
            audioReadTask = ReadAudioAsync(audioProcess, cancellationToken);
            await WaitForAudioPrebufferAsync(cancellationToken).ConfigureAwait(false);

        }
        catch (Exception)
        {
            // Video remains usable when a file has no audio stream or audio is unavailable.
            audioOutput?.Stop();
            audioOutput?.Dispose();
            audioOutput = null;
            audioProcess?.Dispose();
            audioProcess = null;
            audioBuffer = null;
            speakerProvider = null;
        }
    }

    private async Task WaitForAudioPrebufferAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1.25);
        var target = TimeSpan.FromMilliseconds(250);

        while (!cancellationToken.IsCancellationRequested
               && DateTime.UtcNow < deadline
               && (audioBuffer?.BufferedDuration ?? TimeSpan.Zero) < target)
        {
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReadAudioAsync(Process ffmpeg, CancellationToken cancellationToken)
    {
        await ReadAudioAsync(ffmpeg.StandardOutput.BaseStream, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadAudioAsync(Stream audioStream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await audioStream.ReadAsync(buffer, cancellationToken)
                    .ConfigureAwait(false);

                if (bytesRead == 0)
                {
                    break;
                }

                audioBuffer?.AddSamples(buffer, 0, bytesRead);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when switching inputs.
        }
        catch (IOException)
        {
            // Expected when FFmpeg closes its pipe.
        }
    }

    private async Task ReadAudioFileAsync(string path, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        long position = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!File.Exists(path))
                {
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                using var audioFile = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 8192,
                    useAsync: true);
                audioFile.Position = position;

                while (!cancellationToken.IsCancellationRequested)
                {
                    if (playbackIsFile && audioBuffer?.BufferedDuration.TotalSeconds >= 0.5)
                    {
                        await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    var available = audioFile.Length - audioFile.Position;
                    if (available <= 0)
                    {
                        await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var count = (int)Math.Min(buffer.Length, available);
                    var bytesRead = await audioFile.ReadAsync(buffer.AsMemory(0, count), cancellationToken)
                        .ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    position += bytesRead;
                    audioBuffer?.AddSamples(buffer, 0, bytesRead);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when switching inputs.
        }
        catch (IOException)
        {
            // Expected when FFmpeg closes or replaces the output file.
        }
    }

    private async Task ReadFramesAsync(Process ffmpeg, CancellationToken cancellationToken)
    {
        var frameSize = OutputWidth * OutputHeight * BytesPerPixel;
        var frame = ArrayPool<byte>.Shared.Rent(frameSize);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await ReadExactlyAsync(ffmpeg.StandardOutput.BaseStream, frame.AsMemory(0, frameSize), cancellationToken)
                    .ConfigureAwait(false);

                if (bytesRead != frameSize)
                {
                    break;
                }

                var decoded = new InputFrameEventArgs(
                        frame,
                        OutputWidth,
                        OutputHeight,
                        OutputWidth * BytesPerPixel,
                        releasePixels: ReturnPooledFrame);
                frame = ArrayPool<byte>.Shared.Rent(frameSize);
                if (localFrames is not null)
                {
                    try { await localFrames.Writer.WriteAsync(decoded, cancellationToken).ConfigureAwait(false); }
                    catch { decoded.Release(); throw; }
                }
                else if (FrameReady is { } handler) handler(this, decoded);
                else decoded.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when a new input is selected.
        }
        catch (IOException)
        {
            // Expected when FFmpeg closes its pipe.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
            localFrames?.Writer.TryComplete();
            if (!cancellationToken.IsCancellationRequested)
            {
                Ended?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private static void ReturnPooledFrame(byte[] pixels)
    {
        ArrayPool<byte>.Shared.Return(pixels);
    }

    private async Task PresentLocalFramesAsync(Channel<InputFrameEventArgs> frames, CancellationToken token)
    {
        long frameNumber = 0;
        var presentationClock = new Stopwatch();
        double clockCorrection = 0;
        double nextClockCheck = 1;
        try
        {
            // Decode ahead to absorb input packet bursts without discarding frames.
            while (frames.Reader.Count < 30 && readTask?.IsCompleted != true)
                await Task.Delay(5, token).ConfigureAwait(false);
            await foreach (var frame in frames.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                var handedOff = false;
                try
                {
                    var targetSeconds = frameNumber++ / 60.0;
                    while (targetSeconds > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!presentationClock.IsRunning && Volatile.Read(ref audioPlaybackStarted) != 0)
                            presentationClock.Start();
                        var elapsed = presentationClock.Elapsed.TotalSeconds;
                        var seconds = elapsed + clockCorrection;
                        // Device positions are quantized. Use a monotonic clock
                        // between periodic audio checks instead of copying those jumps.
                        if (elapsed >= nextClockCheck)
                        {
                            var audioSeconds = (audioOutput?.GetPosition() ?? 0) / 176400.0;
                            if (Math.Abs(audioSeconds - seconds) > 0.08)
                                clockCorrection += audioSeconds - seconds;
                            nextClockCheck = elapsed + 1;
                        }
                        if (seconds >= targetSeconds) break;
                        await Task.Delay(2, token).ConfigureAwait(false);
                    }
                    if (FrameReady is { } handler)
                    {
                        handedOff = true;
                        handler(this, frame);
                    }
                }
                finally { if (!handedOff) frame.Release(); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static async Task<int> ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;

        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.Slice(offset), cancellationToken)
                .ConfigureAwait(false);

            if (count == 0)
            {
                break;
            }

            offset += count;
        }

        return offset;
    }

    private static async Task DrainErrorOutputAsync(StreamReader errorReader, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await errorReader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }
        }
    }

    private static string BuildArguments(string videoPath)
    {
        var quotedPath = $"\"{videoPath.Replace("\"", "\\\"")}\"";
        return $"-hide_banner -loglevel error -re -stream_loop -1 -i {quotedPath} " +
               "-an -vf \"scale=640:424:force_original_aspect_ratio=decrease,pad=640:424:(ow-iw)/2:(oh-ih)/2:color=black,fps=60\" " +
               "-f rawvideo -pix_fmt bgra -";
    }

    private static string BuildCombinedFileArguments(string videoPath, string audioPath, TimeSpan startPosition)
    {
        var quotedPath = QuoteArgument(videoPath);
        var seek = FormatSeek(startPosition);
        var videoOutput = "-map 0:v:0 -an -vf \"setpts=PTS-STARTPTS,scale=640:424:force_original_aspect_ratio=decrease,pad=640:424:(ow-iw)/2:(oh-ih)/2:color=black,fps=60\" " +
                          "-f rawvideo -pix_fmt bgra pipe:1";
        var audioOutput = $"-map 0:a:0? -vn -af \"asetpts=PTS-STARTPTS,aresample=async=1:first_pts=0\" -ar 44100 -ac 2 -flush_packets 1 -f s16le {QuotePath(audioPath)}";
        return $"-hide_banner -loglevel error -ss {seek} -stream_loop -1 -i {quotedPath} {videoOutput} {audioOutput}";
    }

    private static string BuildCombinedStreamArguments(
        string videoStreamUrl,
        string audioStreamUrl,
        string audioPath,
        TimeSpan startPosition)
    {
        var seek = FormatSeek(startPosition);
        var videoInput = $"-reconnect 1 -reconnect_streamed 1 -reconnect_at_eof 1 -reconnect_delay_max 5 -ss {seek} -re -i {QuoteArgument(videoStreamUrl)}";
        var hasSeparateAudio = !string.Equals(videoStreamUrl, audioStreamUrl, StringComparison.Ordinal);
        var audioInput = hasSeparateAudio
            ? $" -reconnect 1 -reconnect_streamed 1 -reconnect_at_eof 1 -reconnect_delay_max 5 -ss {seek} -re -i {QuoteArgument(audioStreamUrl)}"
            : string.Empty;
        var audioMap = hasSeparateAudio ? "1:a:0" : "0:a:0?";
        var videoOutput = "-map 0:v:0 -an -vf \"setpts=PTS-STARTPTS,scale=640:424:force_original_aspect_ratio=decrease,pad=640:424:(ow-iw)/2:(oh-ih)/2:color=black,fps=60\" " +
                          "-f rawvideo -pix_fmt bgra pipe:1";
        var audioOutput = $"-map {audioMap} -vn -af \"asetpts=PTS-STARTPTS,aresample=async=1:first_pts=0\" -ar 44100 -ac 2 -flush_packets 1 -f s16le {QuotePath(audioPath)}";
        return $"-hide_banner -loglevel error {videoInput}{audioInput} {videoOutput} {audioOutput}";
    }

    private static string QuotePath(string path)
    {
        return $"\"{path.Replace("\"", "\\\"")}\"";
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The temporary audio file can remain if FFmpeg still owns it.
        }
    }

    private static string FormatSeek(TimeSpan position)
    {
        return Math.Max(0, position.TotalSeconds).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string BuildAudioArguments(string videoPath)
    {
        var quotedPath = $"\"{videoPath.Replace("\"", "\\\"")}\"";
        return $"-hide_banner -loglevel error -re -stream_loop -1 -i {quotedPath} " +
               "-vn -af \"aresample=async=1\" -ar 44100 -ac 2 -f s16le -";
    }

    private static string BuildStreamArguments(string streamUrl)
    {
        var quotedUrl = QuoteArgument(streamUrl);
        return $"-hide_banner -loglevel error -reconnect 1 -reconnect_streamed 1 -reconnect_at_eof 1 -reconnect_delay_max 5 -re -i {quotedUrl} " +
               "-an -vf \"scale=640:424:force_original_aspect_ratio=decrease,pad=640:424:(ow-iw)/2:(oh-ih)/2:color=black,fps=60\" " +
               "-f rawvideo -pix_fmt bgra -";
    }

    private static string BuildStreamAudioArguments(string streamUrl)
    {
        var quotedUrl = QuoteArgument(streamUrl);
        return $"-hide_banner -loglevel error -reconnect 1 -reconnect_streamed 1 -reconnect_at_eof 1 -reconnect_delay_max 5 -re -i {quotedUrl} " +
               "-vn -af \"aresample=async=1\" -ar 44100 -ac 2 -f s16le -";
    }

    private static string QuoteArgument(string value)
    {
        return $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }

    private static void StopProcess(Process? processToStop)
    {
        if (processToStop is not { HasExited: false })
        {
            return;
        }

        try
        {
            processToStop.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process can exit between the HasExited check and Kill.
        }
    }

    private static async Task IgnoreReaderAsync(Task? readerTask)
    {
        if (readerTask is null)
        {
            return;
        }

        try
        {
            await readerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when switching inputs.
        }
        catch (IOException)
        {
            // Expected when FFmpeg closes its pipe.
        }
    }

    private void Process_Exited(object? sender, EventArgs e)
    {
        if (cancellation?.IsCancellationRequested != true)
        {
            Ended?.Invoke(this, EventArgs.Empty);
        }
    }
}

// Gives decoded audio the narrow, slightly compressed character of a small CRT speaker.
internal sealed class VintageSpeakerWaveProvider : IWaveProvider
{
    private readonly IWaveProvider source;
    private float speakerState;

    public VintageSpeakerWaveProvider(IWaveProvider source)
    {
        this.source = source;
    }

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(byte[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    public int Read(Span<byte> buffer)
    {
        var bytesRead = source.Read(buffer);
        var sampleCount = bytesRead - (bytesRead % 4);

        for (var index = 0; index < sampleCount; index += 4)
        {
            var left = ReadPcmSample(buffer, index) / 32768f;
            var right = ReadPcmSample(buffer, index + 2) / 32768f;
            var mono = (left * 0.46f) + (right * 0.46f);

            // A soft one-pole filter, mild saturation, and narrow stereo image.
            speakerState += (mono - speakerState) * 0.34f;
            var filtered = (mono * 0.64f) + (speakerState * 0.36f);
            var compressed = MathF.Tanh(filtered * 1.35f) * 0.82f;
            var pcm = (short)Math.Clamp((int)MathF.Round(compressed * 32767f), short.MinValue, short.MaxValue);

            buffer[index] = (byte)pcm;
            buffer[index + 1] = (byte)(pcm >> 8);
            buffer[index + 2] = buffer[index];
            buffer[index + 3] = buffer[index + 1];
        }

        return bytesRead;
    }

    private static short ReadPcmSample(Span<byte> buffer, int index)
    {
        return (short)(buffer[index] | (buffer[index + 1] << 8));
    }
}

// Uses the Windows Graphics Capture picker to capture a display or application window.
public sealed class WindowsGraphicsCaptureSource : IDisposable
{
    private Direct3D11CaptureFramePool? framePool;
    private GraphicsCaptureSession? session;
    private IDirect3DDevice? device;
    private LearningModelDevice? learningDevice;
    private PrintWindowCaptureSource? printWindowSource;
    private CancellationTokenSource? captureCancellation;
    private SemaphoreSlim? frameSignal;
    private Task? captureTask;
    private long frameSequence;
    private bool isClosed;

    public event EventHandler<InputFrameEventArgs>? FrameReady;
    public event EventHandler<CaptureErrorEventArgs>? CaptureFailed;

    public async Task<bool> PickAndStartAsync(IntPtr ownerWindowHandle)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new PlatformNotSupportedException("Windows Graphics Capture is not supported on this PC.");
        }

        Stop();
        isClosed = false;

        var picker = new GraphicsCapturePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerWindowHandle);
        var item = await picker.PickSingleItemAsync();

        if (item is null)
        {
            return false;
        }

        // Chromium can stop painting an occluded window after this app covers
        // it. PrintWindow renders window targets off-screen; WGC remains the
        // path for monitor targets, which do not have a top-level HWND here.
        var windowHandle = FindTopLevelWindow(item.DisplayName);
        if (windowHandle != IntPtr.Zero)
        {
            printWindowSource = new PrintWindowCaptureSource(windowHandle);
            printWindowSource.FrameReady += PrintWindowSource_FrameReady;
            printWindowSource.CaptureFailed += PrintWindowSource_CaptureFailed;
            printWindowSource.Start();
            return true;
        }

        // LearningModelDevice exposes a Windows-compatible D3D11 device without
        // adding another graphics backend to the Avalonia application.
        learningDevice = new LearningModelDevice(LearningModelDeviceKind.DirectX);
        device = learningDevice.Direct3D11Device;
        framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            item.Size);
        frameSignal = new SemaphoreSlim(0);
        framePool.FrameArrived += FramePool_FrameArrived;
        session = framePool.CreateCaptureSession(item);
        session.DirtyRegionMode = GraphicsCaptureDirtyRegionMode.ReportAndRender;
        session.MinUpdateInterval = TimeSpan.Zero;
        session.StartCapture();
        captureCancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref frameSequence, 0);
        captureTask = CaptureLoopAsync(framePool, frameSignal, captureCancellation.Token);
        frameSignal.Release();
        return true;
    }

    public void Dispose()
    {
        Stop();
    }

    private void PrintWindowSource_FrameReady(object? sender, InputFrameEventArgs e)
    {
        FrameReady?.Invoke(this, e);
    }

    private void PrintWindowSource_CaptureFailed(object? sender, CaptureErrorEventArgs e)
    {
        CaptureFailed?.Invoke(this, e);
    }

    private void FramePool_FrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (isClosed)
        {
            return;
        }

        try
        {
            frameSignal?.Release();
        }
        catch (ObjectDisposedException)
        {
            // Expected while a capture is being stopped.
        }
    }

    private async Task CaptureLoopAsync(
        Direct3D11CaptureFramePool sender,
        SemaphoreSlim signal,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!isClosed && !cancellationToken.IsCancellationRequested)
            {
                await signal.WaitAsync(cancellationToken).ConfigureAwait(false);

                // Drain any frames that arrived while the previous bitmap was
                // being copied. Only the newest surface is useful to the TV.
                var newestFrame = sender.TryGetNextFrame();
                if (newestFrame is null)
                {
                    continue;
                }

                while (true)
                {
                    var nextFrame = sender.TryGetNextFrame();
                    if (nextFrame is null)
                    {
                        break;
                    }

                    newestFrame.Dispose();
                    newestFrame = nextFrame;
                }

                using (newestFrame)
                using (var softwareBitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(newestFrame.Surface))
                {
                    var bitmap = softwareBitmap;
                    SoftwareBitmap? convertedBitmap = null;

                    if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
                    {
                        convertedBitmap = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                        bitmap = convertedBitmap;
                    }

                    try
                    {
                        var pixels = CopyBitmapPixels(bitmap, out var width, out var height, out var stride);
                        var sequence = Interlocked.Increment(ref frameSequence);
                        FrameReady?.Invoke(this, new InputFrameEventArgs(pixels, width, height, stride, sequence));
                    }
                    finally
                    {
                        convertedBitmap?.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when switching away from a live capture.
        }
        catch (ObjectDisposedException)
        {
            // Expected when the frame pool is disposed during shutdown.
        }
        catch (Exception exception) when (!isClosed && !cancellationToken.IsCancellationRequested)
        {
            // Report a live capture failure instead of silently leaving a blank TV.
            CaptureFailed?.Invoke(this, new CaptureErrorEventArgs(exception));
        }
    }

    private void Stop()
    {
        isClosed = true;

        if (printWindowSource is not null)
        {
            printWindowSource.FrameReady -= PrintWindowSource_FrameReady;
            printWindowSource.CaptureFailed -= PrintWindowSource_CaptureFailed;
            printWindowSource.Dispose();
            printWindowSource = null;
        }

        captureCancellation?.Cancel();
        captureCancellation?.Dispose();
        captureCancellation = null;
        captureTask = null;

        try
        {
            frameSignal?.Release();
        }
        catch (ObjectDisposedException)
        {
            // Expected when stopping an already closed capture.
        }

        if (framePool is not null)
        {
            framePool.FrameArrived -= FramePool_FrameArrived;
            framePool.Dispose();
            framePool = null;
        }

        frameSignal?.Dispose();
        frameSignal = null;

        session?.Dispose();
        session = null;
        device = null;
        learningDevice = null;
    }

    private static IntPtr FindTopLevelWindow(string displayName)
    {
        var exactMatch = IntPtr.Zero;
        var partialMatch = IntPtr.Zero;

        EnumWindows((windowHandle, _) =>
        {
            if (!IsWindowVisible(windowHandle))
            {
                return true;
            }

            var title = GetWindowTitle(windowHandle);
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            if (string.Equals(title, displayName, StringComparison.Ordinal))
            {
                exactMatch = windowHandle;
                return false;
            }

            if (partialMatch == IntPtr.Zero
                && (title.Contains(displayName, StringComparison.OrdinalIgnoreCase)
                    || displayName.Contains(title, StringComparison.OrdinalIgnoreCase)))
            {
                partialMatch = windowHandle;
            }

            return true;
        }, IntPtr.Zero);

        return exactMatch != IntPtr.Zero ? exactMatch : partialMatch;
    }

    private static string GetWindowTitle(IntPtr windowHandle)
    {
        var length = GetWindowTextLength(windowHandle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var title = new StringBuilder(length + 1);
        GetWindowText(windowHandle, title, title.Capacity);
        return title.ToString();
    }

    private delegate bool EnumWindowsCallback(IntPtr windowHandle, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr windowHandle, StringBuilder title, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr windowHandle);

    private static byte[] CopyBitmapPixels(SoftwareBitmap bitmap, out int width, out int height, out int stride)
    {
        width = bitmap.PixelWidth;
        height = bitmap.PixelHeight;

        // CopyToBuffer is the supported WinRT path for reading a SoftwareBitmap.
        // It avoids relying on the underlying COM object implementing a projected
        // IMemoryBufferByteAccess interface, which is not guaranteed for WGC frames.
        stride = checked(width * 4);
        var length = checked(height * stride);
        var buffer = new Windows.Storage.Streams.Buffer(checked((uint)length));
        bitmap.CopyToBuffer(buffer);

        var pixels = new byte[length];
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(pixels);
        return pixels;
    }
}

// Captures an occluded top-level window into an off-screen 32-bit DIB.
internal sealed class PrintWindowCaptureSource : IDisposable
{
    private const uint PrintWindowFullContent = 0x00000002;
    private readonly IntPtr windowHandle;
    private CancellationTokenSource? cancellation;
    private Task? captureTask;
    private long frameSequence;
    private bool isClosed;

    public PrintWindowCaptureSource(IntPtr windowHandle)
    {
        this.windowHandle = windowHandle;
    }

    public event EventHandler<InputFrameEventArgs>? FrameReady;
    public event EventHandler<CaptureErrorEventArgs>? CaptureFailed;

    public void Start()
    {
        cancellation = new CancellationTokenSource();
        isClosed = false;
        captureTask = CaptureLoopAsync(cancellation.Token);
    }

    public void Dispose()
    {
        isClosed = true;
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
        captureTask = null;
    }

    private async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!isClosed && !cancellationToken.IsCancellationRequested)
            {
                if (TryCapture(out var pixels, out var width, out var height, out var stride))
                {
                    var sequence = Interlocked.Increment(ref frameSequence);
                    FrameReady?.Invoke(this, new InputFrameEventArgs(pixels, width, height, stride, sequence));
                }

                await Task.Delay(TimeSpan.FromMilliseconds(1000.0 / 60.0), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when switching inputs.
        }
        catch (Exception exception) when (!isClosed && !cancellationToken.IsCancellationRequested)
        {
            CaptureFailed?.Invoke(this, new CaptureErrorEventArgs(exception));
        }
    }

    private bool TryCapture(out byte[] pixels, out int width, out int height, out int stride)
    {
        pixels = Array.Empty<byte>();
        width = 0;
        height = 0;
        stride = 0;

        if (!GetWindowRect(windowHandle, out var windowRect))
        {
            return false;
        }

        width = windowRect.Right - windowRect.Left;
        height = windowRect.Bottom - windowRect.Top;
        if (width <= 0 || height <= 0 || width > 16_384 || height > 16_384)
        {
            return false;
        }

        stride = checked(width * 4);
        var length = checked(stride * height);
        var bitmapInfo = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = 0
            }
        };

        var memoryDc = CreateCompatibleDC(IntPtr.Zero);
        if (memoryDc == IntPtr.Zero)
        {
            return false;
        }

        var bitmap = CreateDibSection(memoryDc, ref bitmapInfo, 0, out var pixelPointer, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero || pixelPointer == IntPtr.Zero)
        {
            DeleteDC(memoryDc);
            return false;
        }

        var previousObject = SelectObject(memoryDc, bitmap);
        try
        {
            if (!PrintWindow(windowHandle, memoryDc, PrintWindowFullContent))
            {
                return false;
            }

            pixels = new byte[length];
            Marshal.Copy(pixelPointer, pixels, 0, length);
            return true;
        }
        finally
        {
            SelectObject(memoryDc, previousObject);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint ImageSize;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr windowHandle, out WindowRect windowRect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr windowHandle, IntPtr deviceContext, uint flags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", EntryPoint = "CreateDIBSection")]
    private static extern IntPtr CreateDibSection(
        IntPtr deviceContext,
        ref BitmapInfo bitmapInfo,
        uint usage,
        out IntPtr bits,
        IntPtr section,
        uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr graphicsObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr graphicsObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr deviceContext);
}
