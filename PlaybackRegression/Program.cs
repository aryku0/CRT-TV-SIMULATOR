using System.Buffers;
using System.Reflection;
using System.Runtime.CompilerServices;
using DvdLogoApp;
using System.Diagnostics;

if (args.Length == 2)
{
    var arrivals = new List<double>();
    var clock = Stopwatch.StartNew();
    await using var source = new FfmpegVideoSource();
    source.FrameReady += (_, frame) =>
    {
        lock (arrivals) arrivals.Add(clock.Elapsed.TotalMilliseconds);
        source.StartSynchronizedAudio();
        frame.Release();
    };
    await source.StartAsync(args[0], args[1]);
    await Task.Delay(TimeSpan.FromSeconds(12));
    ReportCadence("Playback");
    await source.SkipAsync(TimeSpan.FromSeconds(10));
    lock (arrivals) arrivals.Clear();
    await Task.Delay(TimeSpan.FromSeconds(6));
    ReportCadence("Forward seek");
    await source.SkipAsync(TimeSpan.FromSeconds(-10));
    lock (arrivals) arrivals.Clear();
    await Task.Delay(TimeSpan.FromSeconds(6));
    ReportCadence("Backward seek");
    await source.StopAsync();
    return;

    void ReportCadence(string phase)
    {
        double[] samples;
        lock (arrivals) samples = arrivals.ToArray();
        var intervals = samples.Zip(samples.Skip(1), (a, b) => b - a).ToArray();
        var fps = samples.Length < 2 ? 0 : (samples.Length - 1) * 1000 / (samples[^1] - samples[0]);
        Console.WriteLine($"{phase}: frames={samples.Length}; FPS={fps:F1}; under 5ms={intervals.Count(x => x < 5)}; over 30ms={intervals.Count(x => x > 30)}; max={intervals.DefaultIfEmpty().Max():F1}ms");
        Check(fps > 55 && fps < 65 && intervals.Max() < 100, $"{phase} sustains playback without long stalls");
    }
}

const int frameSize = 640 * 424 * 4;
var read = typeof(FfmpegVideoSource).GetMethod("ReadExactlyAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
var rented = ArrayPool<byte>.Shared.Rent(frameSize);
try
{
    var data = new byte[frameSize * 2];
    Array.Fill(data, (byte)17, 0, frameSize);
    Array.Fill(data, (byte)93, frameSize, frameSize);
    using var input = new ShortReadStream(data);
    foreach (var expected in new byte[] { 17, 93 })
    {
        var count = await (Task<int>)read.Invoke(null, [input, rented.AsMemory(0, frameSize), CancellationToken.None])!;
        Check(count == frameSize, "One complete frame returned");
        Check(rented.AsSpan(0, frameSize).IndexOfAnyExcept(expected) < 0, "Frame boundary preserved across short reads");
    }
    var end = await (Task<int>)read.Invoke(null, [input, rented.AsMemory(0, frameSize), CancellationToken.None])!;
    Check(end == 0, "EOF returns zero");

    // Exercise buffer ownership without creating a GPU device or desktop window.
    var stage = (TvModelStageControl)RuntimeHelpers.GetUninitializedObject(typeof(TvModelStageControl));
    var field = typeof(TvModelStageControl).GetField("externalVideoPixels", BindingFlags.NonPublic | BindingFlags.Instance)!;
    stage.SetExternalVideoFrame(rented, 640, 424, 640 * 4);
    var owned = (byte[])field.GetValue(stage)!;
    rented[0] = 201;
    Check(!ReferenceEquals(owned, rented) && owned[0] == 93, "TV retains pixels after decoder buffer is reused");
    stage.SetExternalVideoFrame(rented, 640, 424, 640 * 4);
    Check(ReferenceEquals(owned, field.GetValue(stage)) && owned[0] == 201, "Owned buffer reused for subsequent frames");
    var releases = 0;
    var frame = new InputFrameEventArgs(rented, 640, 424, 640 * 4, releasePixels: _ => releases++);
    frame.Release();
    frame.Release();
    Check(releases == 1, "Frame released exactly once");
}
finally
{
    ArrayPool<byte>.Shared.Return(rented);
}
var shaderType = typeof(TvModelStageControl);
var textureWidth = (int)shaderType.GetField("ScreenTextureWidth", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
var textureHeight = (int)shaderType.GetField("ScreenTextureHeight", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
foreach (var curve in new[] { 0.0, 0.55, 1.0 })
{
    var idle = Enumerable.Repeat((byte)180, textureWidth * textureHeight * 4).ToArray();
    var video = (byte[])idle.Clone();
    shaderType.GetMethod("ApplyCrtShader", BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, [idle, curve, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1]);
    shaderType.GetMethod("ApplyFastExternalCrtShader", BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, [video, curve, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1]);
    Check(idle.SequenceEqual(video), $"Video and idle black borders match at curve {curve}");
}
Console.WriteLine("All playback regression checks passed.");

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine($"PASS: {name}");
}

sealed class ShortReadStream(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => base.ReadAsync(buffer[..Math.Min(buffer.Length, 7919)], cancellationToken);
}
