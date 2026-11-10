using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace MeetingAssistant;

// Reads Windows Live Captions (whatever plays through your speakers/headphones) and saves
// each finished sentence. There's no official API, so this reads the caption text the same way
// accessibility tools do (UI Automation). Based on the approach used by the open-source
// LiveCaptions-Translator and get-livecaptions projects.
sealed class CaptionReader
{
    // Change this path if you want transcripts saved somewhere else.
    public const string TranscriptDir = @"C:\Users\Dlee\Local\Records";
    public static string CorrectionsPath => Path.Combine(Directory.GetCurrentDirectory(), "corrections.txt");

    private const string WindowClass = "LiveCaptionsDesktopWindow";
    private const string TextBlockId = "CaptionsTextBlock";
    private const int PollMs = 250; // read 4x a second so text is saved before it scrolls away

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private StreamWriter? _transcript;
    private List<(Regex Heard, string Correct)> _corrections = new();
    private IntPtr _hiddenWindow;
    private AutomationElement? _window, _textBlock; // cached; only used by one thread at a time
    private long _linesSaved, _gaps;

    public string? CurrentTranscriptPath { get; private set; }
    public bool IsRunning => _loop is not null;
    public long LinesSaved => Interlocked.Read(ref _linesSaved);
    public long Gaps => Interlocked.Read(ref _gaps);

    /// <summary>A finished sentence was saved. Runs on a background thread.</summary>
    public event Action<string>? LineSaved;
    /// <summary>Text still being spoken (not saved yet). Runs on a background thread.</summary>
    public event Action<string>? InProgress;
    /// <summary>Some captions scrolled away before they could be read.</summary>
    public event Action? GapDetected;

    public async Task StartAsync(bool hideCaptionsWindow, Action<string> status)
    {
        if (IsRunning)
        {
            return;
        }

        var window = await FindOrLaunchAsync(status);
        _window = window;
        _textBlock = null;
        if (hideCaptionsWindow)
        {
            Hide(window);
        }

        _corrections = LoadCorrections(CorrectionsPath);
        Interlocked.Exchange(ref _linesSaved, 0);
        Interlocked.Exchange(ref _gaps, 0);
        Directory.CreateDirectory(TranscriptDir);
        CurrentTranscriptPath = Path.Combine(TranscriptDir, $"meeting_{DateTime.Now:yyyy-MM-dd_HHmm}.txt");
        _transcript = new StreamWriter(new FileStream(CurrentTranscriptPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };

        // Anything already on screen when you press Start is skipped.
        var startText = await Task.Run(ReadCaptions);
        var saved = CaptionText.Baseline(startText);
        Debug($"--- start {DateTime.Now:yyyy-MM-dd} | window found: {_window is not null} | text block found: {_textBlock is not null} | hidden: {hideCaptionsWindow} | {startText.Length} chars on screen");

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => PollLoop(saved, token));
    }

    public async Task StopAsync()
    {
        if (_loop is null)
        {
            return;
        }

        _cts!.Cancel();
        await _loop;
        _loop = null;
        _cts.Dispose();
        _cts = null;
        _transcript?.Dispose();
        _transcript = null;
        Restore();
    }

    private async Task PollLoop(List<string> saved, CancellationToken token)
    {
        var lastInProgress = "";
        var lastRaw = "";
        var lastFinished = "";
        var lastFinishedSince = DateTime.UtcNow;
        while (!token.IsCancellationRequested)
        {
            var raw = ReadCaptions();
            var sentences = CaptionText.SplitSentences(raw);

            // Live Captions may only keep a sentence or two, so don't wait for the next sentence:
            // save the newest finished one once it has stayed the same for 1.5 seconds.
            var finished = CaptionText.LastFinished(sentences);
            if (CaptionText.Normalize(finished) != CaptionText.Normalize(lastFinished))
            {
                lastFinished = finished;
                lastFinishedSince = DateTime.UtcNow;
            }

            var stable = finished.Length > 0 && DateTime.UtcNow - lastFinishedSince >= TimeSpan.FromSeconds(1.5);
            var (settled, inProgress) = CaptionText.Settle(sentences, stable);
            var before = LinesSaved;
            Save(saved, settled);

            if (raw != lastRaw)
            {
                lastRaw = raw;
                Debug($"read {raw.Length} chars, {sentences.Count} sentences, {settled.Count} ready, saved {LinesSaved - before} new | start: \"{Clip(raw, 0, 40)}\" | end: \"{Clip(raw, raw.Length - 60, 60)}\"");
            }

            if (inProgress != lastInProgress)
            {
                lastInProgress = inProgress;
                InProgress?.Invoke(inProgress);
            }

            try
            {
                await Task.Delay(PollMs, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // On Stop, save everything left, including the sentence that was still in progress.
        var beforeStop = LinesSaved;
        Save(saved, CaptionText.SplitSentences(ReadCaptions()));
        Debug($"stop: saved {LinesSaved - beforeStop} more");
        InProgress?.Invoke("");
        _debug?.Dispose();
        _debug = null;
    }

    // ---------------- diagnostic log ----------------
    // Writes what Live Captions returned to caption-debug.log in the Records folder,
    // one line each time the caption text changes. Safe to delete anytime.

    private StreamWriter? _debug;

    private void Debug(string message)
    {
        try
        {
            _debug ??= new StreamWriter(Path.Combine(TranscriptDir, "caption-debug.log"), append: true) { AutoFlush = true };
            _debug.WriteLine($"{DateTime.Now:HH:mm:ss.f} {message}");
        }
        catch (IOException)
        {
        }
    }

    private static string Clip(string text, int start, int length)
    {
        start = Math.Max(0, start);
        length = Math.Min(length, text.Length - start);
        return length <= 0 ? "" : text.Substring(start, length).Replace("\r", " ").Replace("\n", " / ");
    }

    private void Save(List<string> saved, List<string> candidates)
    {
        var (news, gap) = CaptionText.NewSentences(saved, candidates);
        if (gap)
        {
            Interlocked.Increment(ref _gaps);
            _transcript?.WriteLine($"{DateTime.Now:HH:mm:ss} [gap: some captions may be missing]");
            GapDetected?.Invoke();
        }

        foreach (var sentence in news)
        {
            var text = ApplyCorrections(sentence, _corrections);
            _transcript?.WriteLine($"{DateTime.Now:HH:mm:ss} {text}");
            Interlocked.Increment(ref _linesSaved);
            LineSaved?.Invoke(text);
        }

        // Remember the last few saved sentences to find our place next time.
        saved.AddRange(news);
        if (saved.Count > 10)
        {
            saved.RemoveRange(0, saved.Count - 10);
        }
    }

    // ---------------- Live Captions window ----------------

    private string ReadCaptions()
    {
        try
        {
            _window ??= FindWindow();
            _textBlock ??= _window?.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, TextBlockId));
            return _textBlock?.Current.Name ?? "";
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            // Live Captions was closed or restarted; look for it again next time.
            _window = null;
            _textBlock = null;
            return "";
        }
    }

    private static AutomationElement? FindWindow() =>
        AutomationElement.RootElement.FindFirst(TreeScope.Children,
            new PropertyCondition(AutomationElement.ClassNameProperty, WindowClass));

    private static async Task<AutomationElement> FindOrLaunchAsync(Action<string> status)
    {
        var window = await Task.Run(FindWindow);
        if (window is not null)
        {
            return window;
        }

        status("Opening Live Captions...");
        Process.Start(new ProcessStartInfo("LiveCaptions.exe") { UseShellExecute = true });
        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(250);
            window = await Task.Run(FindWindow);
            if (window is not null)
            {
                return window;
            }
        }

        throw new InvalidOperationException(
            "Couldn't find Live Captions. Turn it on with Win + Ctrl + L (Windows 11 22H2 or later), then press Start again.");
    }

    // Minimizes the Live Captions bar and hides it from the taskbar while recording.
    private void Hide(AutomationElement window)
    {
        _hiddenWindow = new IntPtr(window.Current.NativeWindowHandle);
        var style = GetWindowLong(_hiddenWindow, GwlExStyle);
        ShowWindow(_hiddenWindow, SwMinimize);
        SetWindowLong(_hiddenWindow, GwlExStyle, style | WsExToolWindow);
    }

    public void Restore()
    {
        if (_hiddenWindow == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(_hiddenWindow, GwlExStyle);
        SetWindowLong(_hiddenWindow, GwlExStyle, style & ~WsExToolWindow);
        ShowWindow(_hiddenWindow, SwRestore);
        _hiddenWindow = IntPtr.Zero;
    }

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int SwMinimize = 6;
    private const int SwRestore = 9;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    // ---------------- corrections ----------------

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
}
