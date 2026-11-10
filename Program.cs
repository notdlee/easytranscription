// Meeting transcriber — Foundry Local live speech-to-text for BOTH sides of a call.
//
// Based on Microsoft's "11-foundrylocal-live-transcription" sample (MIT license,
// see LICENSE). Changes from the original:
//   - Captures your microphone AND your PC's speaker output, mixed into one stream
//     (Foundry Local allows only one live session). Each line is labeled ME or THEM
//     by which source was louder while it was spoken.
//   - Saves every transcribed line to a text file in C:\Users\Dlee\Local\Records.
//   - Keeps the downloaded model by default (no "delete model?" prompt).
//     Set FOUNDRY_LOCAL_CLEANUP_MODEL=true if you ever want it deleted.
//   - Run with --mic-only to transcribe just your microphone.
//
// Tip: wear headphones. Without them, your mic also hears the other people
// through your speakers and their words get transcribed twice.

using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Threading.Channels;

// WasapiLoopbackCapture is marked obsolete in NAudio 3 but still works for whole-PC audio.
#pragma warning disable CS0618

const string SpeechModelEnvVar = "FOUNDRY_LOCAL_SPEECH_MODEL";
const string SpeechLanguageEnvVar = "FOUNDRY_LOCAL_SPEECH_LANGUAGE";
const string CleanupModelEnvVar = "FOUNDRY_LOCAL_CLEANUP_MODEL";
const string DefaultModelAlias = "nemotron-speech-streaming-en-0.6b";
const string DefaultLanguage = "en";
const int SampleRate = 16000;
const int ChunkBytes = SampleRate * 2 / 10; // 100 ms of 16-bit mono audio
const int MaxNameLength = 30;

var modelAlias = FirstNonEmpty(Environment.GetEnvironmentVariable(SpeechModelEnvVar)) ?? DefaultModelAlias;
var language = FirstNonEmpty(Environment.GetEnvironmentVariable(SpeechLanguageEnvVar)) ?? DefaultLanguage;
var deleteModelAtEnd = string.Equals(Environment.GetEnvironmentVariable(CleanupModelEnvVar), "true", StringComparison.OrdinalIgnoreCase);
var micOnly = args.Any(a => a.Equals("--mic-only", StringComparison.OrdinalIgnoreCase));

Console.WriteLine("===========================================================");
Console.WriteLine("   Meeting Transcriber (Foundry Local, fully on-device)");
Console.WriteLine("===========================================================");
Console.WriteLine();
Console.WriteLine($"Model:    {modelAlias}");
Console.WriteLine($"Language: {language}");
Console.WriteLine($"Sources:  {(micOnly ? "microphone only" : "microphone (ME) + PC audio (THEM)")}");
Console.WriteLine();

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("This app is Windows-only.");
    return 1;
}

if (WaveIn.DeviceCount == 0)
{
    Console.Error.WriteLine("No microphone/input device was detected. Connect a microphone and try again.");
    return 1;
}

// --- Transcript file ---
// Change this path if you want transcripts saved somewhere else.
var transcriptDir = @"C:\Users\Dlee\Local\Records";
Directory.CreateDirectory(transcriptDir);
var transcriptPath = Path.Combine(transcriptDir, $"meeting_{DateTime.Now:yyyy-MM-dd_HHmm}.txt");
var transcript = new StreamWriter(transcriptPath, append: true) { AutoFlush = true };
var outputLock = new object();

void PrintLine(string label, ConsoleColor color, string text)
{
    lock (outputLock)
    {
        Console.ForegroundColor = color;
        Console.Write($"[{label}] ");
        Console.ResetColor();
        Console.WriteLine(text);
        transcript.WriteLine($"{DateTime.Now:HH:mm:ss} [{label}] {text}");
    }
}

var config = new Configuration
{
    AppName = "dotnet-local-ai-live-transcription",
    LogLevel = LogLevel.Information
};

await FoundryLocalManager.CreateAsync(config, NullLogger.Instance);
var manager = FoundryLocalManager.Instance;
IModel? loadedModel = null;

try
{
    // --- Register execution providers (GPU/CPU acceleration backends) ---
    var executionProviders = manager.DiscoverEps();
    if (executionProviders.Length > 0)
    {
        Console.WriteLine("Downloading/registering execution providers:");
        var activeEp = string.Empty;
        await manager.DownloadAndRegisterEpsAsync((epName, percent) =>
        {
            if (!string.Equals(activeEp, epName, StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(activeEp))
                {
                    Console.WriteLine();
                }

                activeEp = epName;
            }

            Console.Write($"\r  {epName.PadRight(MaxNameLength)}  {percent,6:F1}%");
        });
        Console.WriteLine();
        Console.WriteLine();
    }

    // --- Resolve the streaming speech model from the Foundry Local catalog ---
    var catalog = await manager.GetCatalogAsync();
    var model = await catalog.GetModelAsync(modelAlias);
    if (model is null)
    {
        Console.Error.WriteLine($"Model '{modelAlias}' was not found in the Foundry Local catalog.");
        return 1;
    }

    var cpuVariant = model.Variants.FirstOrDefault(v => v.Info.Runtime?.DeviceType == DeviceType.CPU);
    if (cpuVariant is not null)
    {
        model.SelectVariant(cpuVariant);
    }

    // --- Download on first run only; later runs use the cache ---
    await model.DownloadAsync(progress =>
    {
        Console.Write($"\rDownloading model: {progress,6:F2}%");
        if (progress >= 100f)
        {
            Console.WriteLine();
        }
    });

    Console.Write($"Loading model '{model.Id}'...");
    await model.LoadAsync();
    loadedModel = model;
    Console.WriteLine("done.");

    var audioClient = await model.GetAudioClientAsync();

    // Foundry Local allows only ONE live session at a time, so both sources are
    // mixed into a single stream. To still label who spoke, the app remembers how
    // loud the mic and the PC audio were for every 100 ms of the stream, then labels
    // each transcribed line by whichever source was louder while it was spoken.
    var session = audioClient.CreateLiveTranscriptionSession();
    session.Settings.SampleRate = SampleRate;
    session.Settings.Channels = 1;
    session.Settings.Language = language;
    await session.StartAsync(CancellationToken.None);

    var audioChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(50) { FullMode = BoundedChannelFullMode.DropOldest });
    var appendTask = PumpAudio(audioChannel, session);
    var loudness = new LoudnessLog();
    var readTask = ReadResults(session, loudness, micOnly);

    // ----- PC audio (THEM): converted to 16 kHz mono and held until the mic asks for it -----
    WasapiLoopbackCapture? loopback = null;
    BufferedWaveProvider? pcBuffer = null;
    if (!micOnly)
    {
        try
        {
            loopback = new WasapiLoopbackCapture();
            var src = loopback.WaveFormat;
            if (src.BitsPerSample != 32 && src.BitsPerSample != 16)
            {
                throw new NotSupportedException($"Unsupported PC audio format: {src}");
            }

            var monoBuffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(src.SampleRate, 1), TimeSpan.FromSeconds(10))
            {
                ReadFully = false,
                DiscardOnBufferOverflow = true
            };
            var converted = new WdlResamplingSampleProvider(monoBuffer.ToSampleProvider(), SampleRate).ToWaveProvider16();
            // Windows sends nothing while the PC is silent; ReadFully makes gaps read as silence.
            pcBuffer = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, 1), TimeSpan.FromSeconds(10))
            {
                ReadFully = true,
                DiscardOnBufferOverflow = true
            };
            var pcBufferLocal = pcBuffer;
            var readBuffer = new byte[ChunkBytes];

            loopback.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0)
                {
                    return;
                }

                var mono = ToMonoFloatBytes(e.Buffer, e.BytesRecorded, src.Channels, src.BitsPerSample);
                monoBuffer.AddSamples(mono, 0, mono.Length);
                int n;
                while ((n = converted.Read(readBuffer.AsSpan())) > 0)
                {
                    pcBufferLocal.AddSamples(readBuffer, 0, n);
                }
            };
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Could not capture PC audio: {ex.Message}");
            Console.WriteLine("Continuing with microphone only.");
            Console.ResetColor();
            loopback?.Dispose();
            loopback = null;
            pcBuffer = null;
        }
    }

    // ----- Microphone (ME): its steady timing paces the mix -----
    using var waveIn = new WaveIn
    {
        WaveFormat = new WaveFormat(SampleRate, 16, 1),
        BufferMilliseconds = 100
    };
    waveIn.DataAvailable += (_, e) =>
    {
        if (e.BytesRecorded <= 0)
        {
            return;
        }

        var mic = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, mic, 0, e.BytesRecorded);

        var pc = new byte[e.BytesRecorded];
        if (pcBuffer is not null)
        {
            // If PC audio has piled up (clock drift), drop the backlog so it stays in sync.
            if (pcBuffer.BufferedDuration > TimeSpan.FromMilliseconds(500))
            {
                pcBuffer.ClearBuffer();
            }

            pcBuffer.Read(pc.AsSpan());
        }

        loudness.Add(Rms(mic), Rms(pc), e.BytesRecorded / 2);
        audioChannel.Writer.TryWrite(Mix(mic, pc));
    };

    Console.WriteLine();
    Console.WriteLine("===========================================================");
    Console.WriteLine("  LIVE TRANSCRIPTION ACTIVE");
    Console.WriteLine("  [ME] = your microphone   [THEM] = audio from your PC");
    Console.WriteLine($"  Saving to: {transcriptPath}");
    Console.WriteLine("  Press ENTER to stop.");
    Console.WriteLine("===========================================================");
    Console.WriteLine();

    try
    {
        loopback?.StartRecording();
        waveIn.StartRecording();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to start audio capture: {ex.Message}");
        return 1;
    }

    Console.ReadLine();

    // --- Shut down cleanly: stop capture, flush audio, collect last results ---
    waveIn.StopRecording();
    loopback?.StopRecording();
    audioChannel.Writer.Complete();
    await appendTask;
    await session.StopAsync(CancellationToken.None);
    await readTask;
    loopback?.Dispose();
}
finally
{
    Console.WriteLine();
    Console.WriteLine($"Transcript saved to: {transcriptPath}");
    transcript.Dispose();

    if (loadedModel is not null)
    {
        await loadedModel.UnloadAsync();
        if (deleteModelAtEnd)
        {
            Console.WriteLine("Removing model from local cache...");
            await loadedModel.RemoveFromCacheAsync();
        }
    }

    Console.WriteLine("Model unloaded.");
    manager.Dispose();
}

return 0;

// Sends queued audio chunks into a transcription session.
static Task PumpAudio(Channel<byte[]> channel, Microsoft.AI.Foundry.Local.OpenAI.LiveAudioTranscriptionSession session) =>
    Task.Run(async () =>
    {
        await foreach (var chunk in channel.Reader.ReadAllAsync())
        {
            await session.AppendAsync(chunk);
        }
    });

// Prints each transcribed line, labeled by whichever source was louder, and saves it.
Task ReadResults(Microsoft.AI.Foundry.Local.OpenAI.LiveAudioTranscriptionSession session, LoudnessLog loudness, bool micOnlyMode) =>
    Task.Run(async () =>
    {
        try
        {
            await foreach (var result in session.GetStream(CancellationToken.None))
            {
                var text = result.Content?[0]?.Text?.Trim();
                if (!result.IsFinal || string.IsNullOrEmpty(text))
                {
                    continue;
                }

                var isMe = micOnlyMode || loudness.MicWasLouder(result.StartTime, result.EndTime);
                PrintLine(isMe ? "ME" : "THEM", isMe ? ConsoleColor.Green : ConsoleColor.Yellow, text);
            }
        }
        catch (OperationCanceledException)
        {
        }
    });

// Mixes two 16-bit mono buffers of equal length.
static byte[] Mix(byte[] a, byte[] b)
{
    var output = new byte[a.Length];
    for (var i = 0; i + 1 < a.Length; i += 2)
    {
        var sum = BitConverter.ToInt16(a, i) + BitConverter.ToInt16(b, i);
        BitConverter.TryWriteBytes(output.AsSpan(i, 2), (short)Math.Clamp(sum, short.MinValue, short.MaxValue));
    }

    return output;
}

// Loudness (root mean square) of a 16-bit mono buffer.
static double Rms(byte[] pcm)
{
    double sum = 0;
    var count = pcm.Length / 2;
    for (var i = 0; i + 1 < pcm.Length; i += 2)
    {
        double v = BitConverter.ToInt16(pcm, i);
        sum += v * v;
    }

    return count == 0 ? 0 : Math.Sqrt(sum / count);
}

// Averages all channels into one and returns 32-bit float samples as bytes.
static byte[] ToMonoFloatBytes(byte[] data, int byteCount, int channels, int bitsPerSample)
{
    var bytesPerSample = bitsPerSample / 8;
    var frames = byteCount / (bytesPerSample * channels);
    var output = new byte[frames * 4];
    for (var f = 0; f < frames; f++)
    {
        float sum = 0;
        for (var c = 0; c < channels; c++)
        {
            var offset = (f * channels + c) * bytesPerSample;
            sum += bitsPerSample == 32
                ? BitConverter.ToSingle(data, offset)
                : BitConverter.ToInt16(data, offset) / 32768f;
        }

        BitConverter.TryWriteBytes(output.AsSpan(f * 4, 4), sum / channels);
    }

    return output;
}

static string? FirstNonEmpty(params string?[] values) =>
    values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

// Remembers mic vs PC loudness for each chunk of the stream, so a transcribed
// line can be matched back to whoever was talking at that moment.
sealed class LoudnessLog
{
    private readonly object _lock = new();
    private readonly List<(double Start, double End, double Mic, double Pc)> _chunks = new();
    private double _position; // seconds of audio sent so far

    public void Add(double micRms, double pcRms, int samples)
    {
        lock (_lock)
        {
            var end = _position + samples / 16000.0;
            _chunks.Add((_position, end, micRms, pcRms));
            _position = end;
        }
    }

    public bool MicWasLouder(double? start, double? end)
    {
        lock (_lock)
        {
            // If the model doesn't report timing, use the last 3 seconds of audio.
            var to = end ?? _position;
            var from = start ?? to - 3.0;
            double mic = 0, pc = 0;
            foreach (var c in _chunks)
            {
                if (c.End > from && c.Start < to)
                {
                    mic += c.Mic;
                    pc += c.Pc;
                }
            }

            return mic >= pc;
        }
    }
}
