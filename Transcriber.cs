using Microsoft.AI.Foundry.Local;
using Microsoft.AI.Foundry.Local.OpenAI;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Text.RegularExpressions;
using System.Threading.Channels;

// WasapiLoopbackCapture is marked obsolete in NAudio 3 but still works for whole-PC audio.
#pragma warning disable CS0618

namespace MeetingAssistant;

// Captures your mic ("ME") and your PC's audio ("THEM"), mixes them into one stream
// (Foundry Local allows only one live session), and transcribes it live.
sealed class Transcriber
{
    private const int SampleRate = 16000;
    private const int ChunkBytes = SampleRate * 2 / 10; // 100 ms of 16-bit mono audio

    // Change this path if you want transcripts saved somewhere else.
    public const string TranscriptDir = @"C:\Users\Dlee\Local\Records";
    public static string CorrectionsPath => Path.Combine(Directory.GetCurrentDirectory(), "corrections.txt");

    private readonly string _modelAlias = Foundry.Env("FOUNDRY_LOCAL_SPEECH_MODEL", "nemotron-speech-streaming-en-0.6b");
    private readonly string _language = Foundry.Env("FOUNDRY_LOCAL_SPEECH_LANGUAGE", "en");

    private IModel? _model;
    private OpenAIAudioClient? _audioClient;

    // Per-recording state
    private LiveAudioTranscriptionSession? _session;
    private WaveInEvent? _waveIn;
    private WasapiLoopbackCapture? _loopback;
    private Channel<byte[]>? _audioChannel;
    private Task? _appendTask, _readTask;
    private StreamWriter? _transcript;
    private List<(Regex Heard, string Correct)> _corrections = new();
    private readonly object _writeLock = new();

    private long _chunksSent, _linesReceived;
    public long ChunksSent => Interlocked.Read(ref _chunksSent);
    public long LinesReceived => Interlocked.Read(ref _linesReceived);

    public string? CurrentTranscriptPath { get; private set; }
    public bool IsRunning => _session is not null;

    /// <summary>Raised for every transcribed line: (label, text). Runs on a background thread.</summary>
    public event Action<string, string>? LineReceived;

    public async Task LoadAsync(Action<string> status)
    {
        _model = await Foundry.Catalog.GetModelAsync(_modelAlias)
            ?? throw new InvalidOperationException($"Speech model '{_modelAlias}' was not found in the Foundry Local catalog.");

        var cpu = _model.Variants.FirstOrDefault(v => v.Info.Runtime?.DeviceType == DeviceType.CPU);
        if (cpu is not null)
        {
            _model.SelectVariant(cpu);
        }

        await _model.DownloadAsync(p => status($"Downloading speech model: {p:F0}% (first time only)"));
        status("Loading speech model...");
        await _model.LoadAsync();
        _audioClient = await _model.GetAudioClientAsync();
    }

    public async Task StartAsync(bool micOnly, Action<string> warn)
    {
        if (_audioClient is null || IsRunning)
        {
            return;
        }

        if (WaveIn.DeviceCount == 0)
        {
            throw new InvalidOperationException("No microphone was detected. Connect one and try again.");
        }

        _corrections = LoadCorrections(CorrectionsPath);
        Interlocked.Exchange(ref _chunksSent, 0);
        Interlocked.Exchange(ref _linesReceived, 0);

        Directory.CreateDirectory(TranscriptDir);
        CurrentTranscriptPath = Path.Combine(TranscriptDir, $"meeting_{DateTime.Now:yyyy-MM-dd_HHmm}.txt");
        _transcript = new StreamWriter(new FileStream(CurrentTranscriptPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };

        var session = _audioClient.CreateLiveTranscriptionSession();
        session.Settings.SampleRate = SampleRate;
        session.Settings.Channels = 1;
        session.Settings.Language = _language;
        await session.StartAsync(CancellationToken.None);
        _session = session;

        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(50) { FullMode = BoundedChannelFullMode.DropOldest });
        _audioChannel = channel;
        _appendTask = Task.Run(async () =>
        {
            await foreach (var chunk in channel.Reader.ReadAllAsync())
            {
                await session.AppendAsync(chunk);
                Interlocked.Increment(ref _chunksSent);
            }
        });

        var loudness = new LoudnessLog();
        _readTask = Task.Run(async () =>
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

                    Interlocked.Increment(ref _linesReceived);
                    text = ApplyCorrections(text, _corrections);
                    var label = micOnly || loudness.MicWasLouder(result.StartTime, result.EndTime) ? "ME" : "THEM";
                    lock (_writeLock)
                    {
                        _transcript?.WriteLine($"{DateTime.Now:HH:mm:ss} [{label}] {text}");
                    }

                    LineReceived?.Invoke(label, text);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        // ----- PC audio (THEM): converted to 16 kHz mono, held until the mic asks for it -----
        BufferedWaveProvider? pcBuffer = null;
        if (!micOnly)
        {
            try
            {
                var loopback = new WasapiLoopbackCapture();
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
                var buffer = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, 1), TimeSpan.FromSeconds(10))
                {
                    ReadFully = true,
                    DiscardOnBufferOverflow = true
                };
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
                        buffer.AddSamples(readBuffer, 0, n);
                    }
                };

                _loopback = loopback;
                pcBuffer = buffer;
            }
            catch (Exception ex)
            {
                warn($"Could not capture PC audio ({ex.Message}). Recording microphone only.");
                _loopback?.Dispose();
                _loopback = null;
            }
        }

        // ----- Microphone (ME): its steady timing paces the mix -----
        var waveIn = new WaveInEvent
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
            channel.Writer.TryWrite(Mix(mic, pc));
        };
        _waveIn = waveIn;

        _loopback?.StartRecording();
        waveIn.StartRecording();
    }

    public async Task StopAsync()
    {
        if (_session is null)
        {
            return;
        }

        _waveIn?.StopRecording();
        _loopback?.StopRecording();
        _audioChannel?.Writer.TryComplete();
        if (_appendTask is not null) await _appendTask;
        await _session.StopAsync(CancellationToken.None);
        if (_readTask is not null) await _readTask;

        _waveIn?.Dispose();
        _loopback?.Dispose();
        await _session.DisposeAsync();
        lock (_writeLock)
        {
            _transcript?.Dispose();
            _transcript = null;
        }

        _waveIn = null;
        _loopback = null;
        _session = null;
    }

    public async Task UnloadAsync()
    {
        await StopAsync();
        if (_model is not null)
        {
            await _model.UnloadAsync();
        }
    }

    // ---------------- helpers ----------------

    // Reads lines like "acme => ACME Corp". Lines starting with # are notes and are skipped.
    private static List<(Regex, string)> LoadCorrections(string path)
    {
        var list = new List<(Regex, string)>();
        if (!File.Exists(path))
        {
            return list;
        }

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split("=>", 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length == 0)
            {
                continue;
            }

            // Whole words only, any capitalization, flexible spacing between words.
            var words = parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape);
            var pattern = @"\b" + string.Join(@"[\s,.-]*", words) + @"\b";
            list.Add((new Regex(pattern, RegexOptions.IgnoreCase), parts[1]));
        }

        return list;
    }

    private static string ApplyCorrections(string text, List<(Regex Heard, string Correct)> corrections)
    {
        foreach (var (heard, correct) in corrections)
        {
            text = heard.Replace(text, correct.Replace("$", "$$"));
        }

        return text;
    }

    // Averages all channels into one and returns 32-bit float samples as bytes.
    private static byte[] ToMonoFloatBytes(byte[] data, int byteCount, int channels, int bitsPerSample)
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

    // Mixes two 16-bit mono buffers of equal length.
    private static byte[] Mix(byte[] a, byte[] b)
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
    private static double Rms(byte[] pcm)
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

    // Remembers mic vs PC loudness for each chunk of the stream, so a transcribed
    // line can be matched back to whoever was talking at that moment.
    private sealed class LoudnessLog
    {
        private readonly object _lock = new();
        private readonly List<(double Start, double End, double Mic, double Pc)> _chunks = new();
        private double _position; // seconds of audio sent so far

        public void Add(double micRms, double pcRms, int samples)
        {
            lock (_lock)
            {
                var end = _position + samples / (double)SampleRate;
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
}
