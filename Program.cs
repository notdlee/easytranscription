// Meeting transcriber — Foundry Local live speech-to-text for BOTH sides of a call.
//
// Based on Microsoft's "11-foundrylocal-live-transcription" sample (MIT license,
// see LICENSE). Changes from the original:
//   - Captures your microphone ("ME") AND your PC's speaker output ("THEM"),
//     each in its own live transcription session, so lines are labeled.
//   - Saves every transcribed line to a text file in Documents\MeetingTranscripts.
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
var transcriptDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MeetingTranscripts");
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

    // ======================= MICROPHONE (ME) =======================
    var micSession = audioClient.CreateLiveTranscriptionSession();
    micSession.Settings.SampleRate = SampleRate;
    micSession.Settings.Channels = 1;
    micSession.Settings.Language = language;
    await micSession.StartAsync(CancellationToken.None);

    var micChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(50) { FullMode = BoundedChannelFullMode.DropOldest });
    var micAppendTask = PumpAudio(micChannel, micSession);
    var micReadTask = ReadResults(micSession, "ME", ConsoleColor.Green);

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

        var buffer = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, buffer, 0, e.BytesRecorded);
        micChannel.Writer.TryWrite(buffer);
    };

    // ======================= PC AUDIO (THEM) =======================
    WasapiLoopbackCapture? loopback = null;
    Channel<byte[]>? pcChannel = null;
    Task? pcAppendTask = null, pcReadTask = null, pcSilenceTask = null;
    var pcSession = (Microsoft.AI.Foundry.Local.OpenAI.LiveAudioTranscriptionSession?)null;
    using var stopSilence = new CancellationTokenSource();

    if (!micOnly)
    {
        try
        {
            pcSession = audioClient.CreateLiveTranscriptionSession();
            pcSession.Settings.SampleRate = SampleRate;
            pcSession.Settings.Channels = 1;
            pcSession.Settings.Language = language;
            await pcSession.StartAsync(CancellationToken.None);

            pcChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(50) { FullMode = BoundedChannelFullMode.DropOldest });
            pcAppendTask = PumpAudio(pcChannel, pcSession);
            pcReadTask = ReadResults(pcSession, "THEM", ConsoleColor.Yellow);

            // Windows gives speaker audio in its own format (usually 48 kHz, 32-bit float, stereo).
            // Convert it to 16 kHz, 16-bit, mono — the format the session expects.
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
            var readBuffer = new byte[ChunkBytes];
            var lastPcAudio = DateTime.UtcNow;
            var pcChannelLocal = pcChannel;

            loopback.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0)
                {
                    return;
                }

                lastPcAudio = DateTime.UtcNow;
                var mono = ToMonoFloatBytes(e.Buffer, e.BytesRecorded, src.Channels, src.BitsPerSample);
                monoBuffer.AddSamples(mono, 0, mono.Length);

                int n;
                while ((n = converted.Read(readBuffer.AsSpan())) > 0)
                {
                    var chunk = new byte[n];
                    Buffer.BlockCopy(readBuffer, 0, chunk, 0, n);
                    pcChannelLocal.Writer.TryWrite(chunk);
                }
            };

            // Windows sends nothing while the PC is silent. Feed silence during those gaps
            // so the model knows the speaker stopped and finishes the sentence.
            pcSilenceTask = Task.Run(async () =>
            {
                var silence = new byte[ChunkBytes];
                while (!stopSilence.IsCancellationRequested)
                {
                    try { await Task.Delay(100, stopSilence.Token); } catch (OperationCanceledException) { break; }
                    if (DateTime.UtcNow - lastPcAudio > TimeSpan.FromMilliseconds(300))
                    {
                        pcChannelLocal.Writer.TryWrite(silence);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Could not start PC-audio transcription: {ex.Message}");
            Console.WriteLine("Continuing with microphone only.");
            Console.ResetColor();
            loopback?.Dispose();
            loopback = null;
            stopSilence.Cancel();
        }
    }

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
        waveIn.StartRecording();
        loopback?.StartRecording();
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
    stopSilence.Cancel();

    micChannel.Writer.Complete();
    pcChannel?.Writer.Complete();
    await micAppendTask;
    if (pcAppendTask is not null) await pcAppendTask;
    if (pcSilenceTask is not null) await pcSilenceTask;

    await micSession.StopAsync(CancellationToken.None);
    if (pcSession is not null) await pcSession.StopAsync(CancellationToken.None);
    await micReadTask;
    if (pcReadTask is not null) await pcReadTask;

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

// Prints each transcribed piece of text with its label and saves it to the file.
Task ReadResults(Microsoft.AI.Foundry.Local.OpenAI.LiveAudioTranscriptionSession session, string label, ConsoleColor color) =>
    Task.Run(async () =>
    {
        try
        {
            await foreach (var result in session.GetStream(CancellationToken.None))
            {
                var text = result.Content?[0]?.Text?.Trim();
                if (result.IsFinal && !string.IsNullOrEmpty(text))
                {
                    PrintLine(label, color, text);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    });

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
