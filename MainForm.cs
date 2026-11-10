using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using System.Diagnostics;

namespace MeetingAssistant;

// The app window: transcription on the left, chat with the local model on the right.
sealed class MainForm : Form
{
    private static readonly Color Background = Color.FromArgb(30, 30, 30);
    private static readonly Color TextColor = Color.Gainsboro;
    private static readonly Color AiColor = Color.LightGreen;
    private static readonly Color DimColor = Color.Gray;
    private static readonly Color YouColor = Color.SkyBlue;
    private static readonly Color ErrorColor = Color.IndianRed;

    private readonly CaptionReader _captions = new();
    private readonly Assistant _assistant = new();

    // Left side
    private readonly Button _startButton = MakeButton("Start", enabled: false);
    private readonly CheckBox _hideCaptionsBox = new() { Text = "Hide Live Captions bar while recording", AutoSize = true, Checked = true, Margin = new Padding(8, 9, 3, 3) };
    private readonly Label _hearingLabel = new() { Text = "", AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(3, 4, 3, 3), MaximumSize = new Size(600, 0) };
    private readonly Button _folderButton = MakeButton("Open Records folder");
    private readonly Button _correctionsButton = MakeButton("Edit corrections");
    private readonly Label _statusLabel = MakeLabel("Starting up...");
    private readonly RichTextBox _transcriptBox = MakeTextBox();

    // Right side
    private readonly Button _useCurrentButton = MakeButton("Use current meeting");
    private readonly Button _openFileButton = MakeButton("Open transcript...");
    private readonly Button _generalButton = MakeButton("General chat");
    private readonly Label _sourceLabel = MakeLabel("Chatting about: nothing (general chat)");
    private readonly Button _notesButton = MakeButton("Training notes");
    private readonly Button _summaryButton = MakeButton("Summary");
    private readonly Label _aiStatusLabel = MakeLabel("");
    private readonly RichTextBox _chatBox = MakeTextBox();
    private readonly TextBox _inputBox = new() { Multiline = true, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10.5f), ScrollBars = ScrollBars.Vertical };
    private readonly Button _sendButton = MakeButton("Send");

    private readonly System.Windows.Forms.Timer _idleTimer = new() { Interval = 10 * 60 * 1000 };
    private readonly System.Windows.Forms.Timer _recordingTimer = new() { Interval = 1000 };
    private DateTime _recordingStarted;
    private readonly List<ChatMessage> _history = new();
    private string? _sourcePath;        // transcript being chatted about (null = general chat)
    private CancellationTokenSource? _aiCts;
    private bool _foundryReady;
    private bool _closingDone;

    public MainForm()
    {
        Text = "Meeting Assistant";
        Size = new Size(1300, 800);
        MinimumSize = new Size(900, 500);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6 };
        split.Panel1.Controls.Add(BuildLeft());
        split.Panel2.Controls.Add(BuildRight());
        Controls.Add(split);
        Load += (_, _) => split.SplitterDistance = ClientSize.Width / 2;

        _captions.LineSaved += text => OnUi(() => AppendTranscriptLine(text));
        _captions.InProgress += text => OnUi(() => _hearingLabel.Text = text.Length == 0 ? "" : "Hearing: " + text);
        _captions.GapDetected += () => OnUi(() => AppendTranscriptNote("[gap: some captions may be missing]", ErrorColor));

        _startButton.Click += async (_, _) => await ToggleRecordingAsync();
        _folderButton.Click += (_, _) => OpenRecordsFolder();
        _correctionsButton.Click += (_, _) => OpenCorrections();
        _useCurrentButton.Click += (_, _) => UseCurrentMeeting();
        _openFileButton.Click += (_, _) => OpenTranscriptFile();
        _generalButton.Click += (_, _) => SetSource(null);
        _notesButton.Click += async (_, _) => await MakeNotesAsync(NotesKind.Training);
        _summaryButton.Click += async (_, _) => await MakeNotesAsync(NotesKind.Summary);
        _sendButton.Click += async (_, _) => await SendOrStopAsync();
        _inputBox.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                await SendOrStopAsync();
            }
        };
        _idleTimer.Tick += async (_, _) =>
        {
            _idleTimer.Stop();
            if (_aiCts is null && _assistant.IsLoaded)
            {
                await _assistant.UnloadAsync();
                _aiStatusLabel.Text = $"{_assistant.ModelAlias} unloaded to free memory (reloads when you use it).";
            }
        };

        // While recording, show elapsed time and whether audio and text are flowing.
        _recordingTimer.Tick += (_, _) =>
            _statusLabel.Text = $"Recording {DateTime.Now - _recordingStarted:mm\\:ss}  ·  lines saved: {_captions.LinesSaved}" +
                                (_captions.Gaps > 0 ? $"  ·  gaps: {_captions.Gaps}" : "");

        Shown += async (_, _) => await StartUpAsync();
        FormClosing += OnFormClosing;
        SetAiButtonsEnabled(false);
    }

    // ======================= Layout =======================

    private Control BuildLeft()
    {
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        buttons.Controls.AddRange(new Control[] { _startButton, _hideCaptionsBox, _folderButton, _correctionsButton });

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(6) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(buttons, 0, 0);
        layout.Controls.Add(_statusLabel, 0, 1);
        layout.Controls.Add(_transcriptBox, 0, 2);
        layout.Controls.Add(_hearingLabel, 0, 3);
        return layout;
    }

    private Control BuildRight()
    {
        var sourceButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        sourceButtons.Controls.AddRange(new Control[] { _useCurrentButton, _openFileButton, _generalButton });

        var notesButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        notesButtons.Controls.AddRange(new Control[] { _notesButton, _summaryButton, _aiStatusLabel });

        var inputRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        inputRow.Controls.Add(_inputBox, 0, 0);
        inputRow.Controls.Add(_sendButton, 1, 0);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(6) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        layout.Controls.Add(sourceButtons, 0, 0);
        layout.Controls.Add(_sourceLabel, 0, 1);
        layout.Controls.Add(notesButtons, 0, 2);
        layout.Controls.Add(_chatBox, 0, 3);
        layout.Controls.Add(inputRow, 0, 4);
        return layout;
    }

    private static Button MakeButton(string text, bool enabled = true) =>
        new() { Text = text, AutoSize = true, Enabled = enabled, Padding = new Padding(6, 2, 6, 2) };

    private static Label MakeLabel(string text) =>
        new() { Text = text, AutoSize = true, Margin = new Padding(3, 8, 3, 3) };

    private static RichTextBox MakeTextBox() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BackColor = Background,
        ForeColor = TextColor,
        BorderStyle = BorderStyle.None,
        Font = new Font("Segoe UI", 10.5f),
        DetectUrls = false
    };

    // ======================= Startup / shutdown =======================

    private async Task StartUpAsync()
    {
        try
        {
            // Recording only needs Live Captions, so it's ready right away.
            _startButton.Enabled = true;
            _statusLabel.Text = "Ready. Press Start when the call begins (uses Windows Live Captions).";
            _aiStatusLabel.Text = "Setting up Foundry Local...";
            await Foundry.InitAsync(s => OnUi(() => _aiStatusLabel.Text = s));
            _foundryReady = true;
            SetAiButtonsEnabled(true);
            _aiStatusLabel.Text = $"{_assistant.ModelAlias} loads when you first use it.";
        }
        catch (Exception ex)
        {
            _aiStatusLabel.ForeColor = ErrorColor;
            _aiStatusLabel.Text = "Foundry Local setup failed: " + ex.Message;
        }
    }

    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_closingDone)
        {
            return;
        }

        e.Cancel = true;
        Enabled = false;
        Text = "Meeting Assistant — saving and closing...";
        try
        {
            _aiCts?.Cancel();
            await _captions.StopAsync();
            await _assistant.UnloadAsync();
            Foundry.Shutdown();
        }
        catch
        {
            // Closing anyway.
        }

        _closingDone = true;
        Close();
    }

    // ======================= Recording =======================

    private async Task ToggleRecordingAsync()
    {
        _startButton.Enabled = false;
        try
        {
            if (!_captions.IsRunning)
            {
                var hide = _hideCaptionsBox.Checked;
                await Task.Run(() => _captions.StartAsync(hide, s => OnUi(() => _statusLabel.Text = s)));
                _startButton.Text = "Stop";
                _hideCaptionsBox.Enabled = false;
                _recordingStarted = DateTime.Now;
                _recordingTimer.Start();
                AppendTranscriptNote($"— Started {DateTime.Now:t} —", DimColor);
            }
            else
            {
                _recordingTimer.Stop();
                _statusLabel.Text = "Stopping...";
                await Task.Run(_captions.StopAsync);
                _startButton.Text = "Start";
                _hideCaptionsBox.Enabled = true;
                _statusLabel.Text = $"Saved: {_captions.CurrentTranscriptPath}";
                AppendTranscriptNote($"— Stopped {DateTime.Now:t} —", DimColor);
            }
        }
        catch (Exception ex)
        {
            AppendTranscriptNote("Error: " + ex.Message, ErrorColor);
        }
        finally
        {
            _startButton.Enabled = true;
        }
    }

    private void AppendTranscriptLine(string text)
    {
        Append(_transcriptBox, $"{DateTime.Now:HH:mm:ss} ", DimColor);
        Append(_transcriptBox, text + Environment.NewLine, TextColor);
    }

    private void AppendTranscriptNote(string text, Color color) =>
        Append(_transcriptBox, text + Environment.NewLine, color);

    private void OpenRecordsFolder()
    {
        Directory.CreateDirectory(CaptionReader.TranscriptDir);
        Process.Start("explorer.exe", CaptionReader.TranscriptDir);
    }

    private void OpenCorrections()
    {
        var path = CaptionReader.CorrectionsPath;
        if (!File.Exists(path))
        {
            File.WriteAllText(path,
                "# Word corrections: one per line, written as   what the model wrote => what it should say\n" +
                "# Matching ignores capitalization and only replaces whole words.\n" +
                "# Changes apply the next time you press Start.\n");
        }

        Process.Start("notepad.exe", path);
    }

    // ======================= Chat source =======================

    private void UseCurrentMeeting()
    {
        if (_captions.CurrentTranscriptPath is { } path && File.Exists(path))
        {
            SetSource(path);
        }
        else
        {
            AppendChat("No meeting recorded yet in this session. Press Start first, or use Open transcript...", ErrorColor);
        }
    }

    private void OpenTranscriptFile()
    {
        using var dialog = new OpenFileDialog
        {
            InitialDirectory = Directory.Exists(CaptionReader.TranscriptDir) ? CaptionReader.TranscriptDir : null,
            Filter = "Transcripts (*.txt)|*.txt",
            Title = "Pick a transcript"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            SetSource(dialog.FileName);
        }
    }

    private void SetSource(string? path)
    {
        _sourcePath = path;
        _history.Clear();
        _sourceLabel.Text = path is null ? "Chatting about: nothing (general chat)" : $"Chatting about: {Path.GetFileName(path)}";
        AppendChat(path is null ? "— General chat —" : $"— Now chatting about {Path.GetFileName(path)} —", DimColor);
    }

    // ======================= AI actions =======================

    private async Task SendOrStopAsync()
    {
        if (_aiCts is not null)
        {
            _aiCts.Cancel();
            return;
        }

        var question = _inputBox.Text.Trim();
        if (question.Length == 0 || !_foundryReady)
        {
            return;
        }

        _inputBox.Clear();
        AppendChat("You: ", YouColor, newLine: false);
        AppendChat(question, TextColor);

        await RunAiAsync(async ct =>
        {
            var transcript = _sourcePath is null ? null : Assistant.ReadTranscript(_sourcePath);
            AppendChat("AI: ", AiColor, newLine: false);
            var answer = await _assistant.AskAsync(transcript, _history, question, t => OnUi(() => AppendChat(t, TextColor, newLine: false)), ct);
            AppendChat("", TextColor);
            _history.Add(ChatMessage.FromUser(question));
            _history.Add(ChatMessage.FromAssistant(answer));
        });
    }

    private async Task MakeNotesAsync(NotesKind kind)
    {
        if (_aiCts is not null || !_foundryReady)
        {
            return;
        }

        var path = _sourcePath ?? _captions.CurrentTranscriptPath;
        if (path is null || !File.Exists(path))
        {
            AppendChat("Pick a transcript first: Use current meeting, or Open transcript...", ErrorColor);
            return;
        }

        if (_sourcePath != path)
        {
            SetSource(path);
        }

        var transcript = Assistant.ReadTranscript(path);
        if (transcript.Length < 20)
        {
            AppendChat("That transcript is empty.", ErrorColor);
            return;
        }

        var name = kind == NotesKind.Training ? "Training notes" : "Summary";
        AppendChat($"{name} for {Path.GetFileName(path)}:", YouColor);

        await RunAiAsync(async ct =>
        {
            var result = await _assistant.BuildNotesAsync(transcript, kind,
                s => OnUi(() => _aiStatusLabel.Text = s),
                t => OnUi(() => AppendChat(t, TextColor, newLine: false)), ct);
            AppendChat("", TextColor);

            var suffix = kind == NotesKind.Training ? "_training-notes.txt" : "_summary.txt";
            var savePath = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + suffix);
            await File.WriteAllTextAsync(savePath, result + Environment.NewLine, ct);
            AppendChat($"Saved to {savePath}", DimColor);

            // Lets you ask follow-up questions about the notes.
            _history.Add(ChatMessage.FromUser($"Write the {name.ToLowerInvariant()} for this meeting."));
            _history.Add(ChatMessage.FromAssistant(result));
        });
    }

    // Loads the model if needed, runs the action, and handles Stop and errors.
    private async Task RunAiAsync(Func<CancellationToken, Task> action)
    {
        _idleTimer.Stop();
        _aiCts = new CancellationTokenSource();
        SetAiButtonsEnabled(false);
        _sendButton.Text = "Stop";
        _sendButton.Enabled = true;
        try
        {
            await _assistant.EnsureLoadedAsync(s => OnUi(() => _aiStatusLabel.Text = s));
            _aiStatusLabel.Text = "Thinking...";
            await action(_aiCts.Token);
            _aiStatusLabel.Text = $"{_assistant.ModelAlias} ready (unloads after 10 idle minutes).";
        }
        catch (OperationCanceledException)
        {
            AppendChat(Environment.NewLine + "(stopped)", DimColor);
            _aiStatusLabel.Text = "Stopped.";
        }
        catch (Exception ex)
        {
            AppendChat(Environment.NewLine + "Error: " + ex.Message, ErrorColor);
            _aiStatusLabel.Text = "Something went wrong — see the chat.";
        }
        finally
        {
            _aiCts.Dispose();
            _aiCts = null;
            _sendButton.Text = "Send";
            SetAiButtonsEnabled(true);
            _idleTimer.Start();
        }
    }

    private void SetAiButtonsEnabled(bool enabled)
    {
        foreach (var control in new Control[] { _notesButton, _summaryButton, _useCurrentButton, _openFileButton, _generalButton, _sendButton })
        {
            control.Enabled = enabled;
        }
    }

    // ======================= Text helpers =======================

    private void AppendChat(string text, Color color, bool newLine = true) =>
        Append(_chatBox, newLine ? text + Environment.NewLine : text, color);

    private static void Append(RichTextBox box, string text, Color color)
    {
        box.SelectionStart = box.TextLength;
        box.SelectionLength = 0;
        box.SelectionColor = color;
        box.AppendText(text);
        box.SelectionColor = box.ForeColor;
        box.ScrollToCaret();
    }

    // Runs UI updates on the window's thread (audio and model callbacks arrive on other threads).
    private void OnUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(action);
        }
        else
        {
            action();
        }
    }
}
