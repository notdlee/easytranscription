using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Microsoft.AI.Foundry.Local;
using System.Text;
using System.Text.RegularExpressions;

namespace MeetingAssistant;

public enum NotesKind { Training, Summary }

// phi-4-mini (or another chat model) running locally through Foundry Local.
// Loaded only when you use it; the window unloads it after 10 idle minutes.
sealed class Assistant
{
    private const int ReplyTokens = 1024;
    private const int PartNotesTokens = 450;

    public string ModelAlias { get; } = Foundry.Env("FOUNDRY_LOCAL_CHAT_MODEL", "phi-4-mini");

    private IModel? _model;
    private OpenAIChatClient? _client;

    public bool IsLoaded => _client is not null;

    /// <summary>How much text the model can take at once (in tokens). Capped to keep it fast.</summary>
    public int ContextTokens { get; private set; } = 4096;

    public async Task EnsureLoadedAsync(Action<string> status)
    {
        if (_client is not null)
        {
            return;
        }

        _model ??= await Foundry.Catalog.GetModelAsync(ModelAlias)
            ?? throw new InvalidOperationException($"Chat model '{ModelAlias}' was not found. Run 'foundry model list' to see what's available.");

        await _model.DownloadAsync(p => status($"Downloading {ModelAlias}: {p:F0}% (first time only)"));
        status($"Loading {ModelAlias}...");
        try
        {
            await _model.LoadAsync();
        }
        catch when (_model.Variants.FirstOrDefault(v => v.Info.Runtime?.DeviceType == DeviceType.CPU) is { } cpu && cpu.Id != _model.Id)
        {
            // The best variant for this PC (e.g. NPU) failed to load — fall back to the CPU version.
            status($"Loading {ModelAlias} (CPU version)...");
            _model.SelectVariant(cpu);
            await _model.DownloadAsync(p => status($"Downloading {ModelAlias} (CPU): {p:F0}%"));
            await _model.LoadAsync();
        }

        _client = await _model.GetChatClientAsync();
        _client.Settings.Temperature = 0.2f;
        ContextTokens = (int)Math.Clamp(_model.Info.ContextLength ?? 4096, 4096, 8192);
        status($"{ModelAlias} ready ({_model.Info.Runtime?.DeviceType}).");
    }

    public async Task UnloadAsync()
    {
        if (_model is not null && _client is not null)
        {
            _client = null;
            await _model.UnloadAsync();
        }
    }

    /// <summary>Sends messages to the model and streams the reply back piece by piece.</summary>
    public async Task<string> ChatAsync(IEnumerable<ChatMessage> messages, Action<string>? onText, int maxTokens, CancellationToken ct)
    {
        var client = _client ?? throw new InvalidOperationException("The chat model is not loaded.");
        client.Settings.MaxTokens = maxTokens;
        var reply = new StringBuilder();
        await foreach (var chunk in client.CompleteChatStreamingAsync(messages, ct))
        {
            var choice = chunk.Choices?.FirstOrDefault();
            var text = choice?.Delta?.Content ?? choice?.Message?.Content;
            if (!string.IsNullOrEmpty(text))
            {
                reply.Append(text);
                onText?.Invoke(text);
            }
        }

        return reply.ToString().Trim();
    }

    // ======================= Chat =======================

    public Task<string> AskAsync(string? transcript, List<ChatMessage> history, string question, Action<string> onText, CancellationToken ct)
    {
        TrimHistory(history);
        var messages = new List<ChatMessage>();
        if (transcript is null)
        {
            messages.Add(ChatMessage.FromSystem("You are a helpful, concise assistant for an IT service desk coordinator. Use plain English."));
        }
        else
        {
            var budgetChars = (ContextTokens - ReplyTokens - EstimateTokens(history) - 400) * 3;
            var excerpt = SelectRelevant(transcript, question, Math.Max(budgetChars, 2000));
            messages.Add(ChatMessage.FromSystem(
                "You answer questions about a recorded work meeting for an IT service desk coordinator. " +
                "Use only the transcript below. If the answer isn't in it, say so. Be brief and use plain English.\n" +
                TranscriptNote +
                (excerpt.Length < transcript.Length ? "Only the parts of the transcript most related to the question are included.\n" : "") +
                "\nTRANSCRIPT:\n" + excerpt));
        }

        messages.AddRange(history);
        messages.Add(ChatMessage.FromUser(question));
        return ChatAsync(messages, onText, ReplyTokens, ct);
    }

    // ======================= Training notes / summary =======================

    private const string TranscriptNote =
        "The transcript comes from live speech-to-text, so expect misheard words and missing punctuation. " +
        "[ME] is the person recording; [THEM] is audio from their computer (team leader, callers, or clients). These labels can be wrong.\n";

    private const string TrainingSystem =
        "You help an IT service desk coordinator take notes while shadowing their team leader. " + TranscriptNote +
        "Never invent steps, names, or details that are not in the text.";

    private const string TrainingPartPrompt =
        "From this part of the transcript, write notes with exactly these two headings:\n" +
        "WORKFLOW: the steps the team leader takes when a call or ticket comes in, in order, as short actions " +
        "(for example: 'Confirm caller and company', 'Check ticket history'). If steps are specific to one client, put the client name first.\n" +
        "CLIENT DETAILS: specific facts the team leader points out about a named client " +
        "(systems they use, contacts, rules, exceptions, quirks). Start each line with the client name.\n" +
        "Write 'none' under a heading if there is nothing for it. Keep it short.";

    private const string TrainingFinalPrompt =
        "Turn these notes into the final training notes. Use exactly this format:\n\n" +
        "## Call workflow\n" +
        "**General** (and a separate block per client only if their steps differ)\n" +
        "Step\n  ↓\nStep\n  ↓\nDecision? → yes: step / no: step\n  ↓\nStep\n\n" +
        "## Client details\n" +
        "### Client name\n- detail\n- detail\n\n" +
        "Rules: keep each step to a few words, merge duplicates, keep the order the team leader used, " +
        "and write 'None mentioned' if a section has nothing. Do not add anything that isn't in the notes.";

    private const string SummarySystem =
        "You summarize work meetings for an IT service desk coordinator. " + TranscriptNote +
        "Never invent details that are not in the text.";

    private const string SummaryPartPrompt =
        "From this part of the transcript, list briefly: main topics discussed, decisions made, and action items " +
        "(who does what, if said). Write 'none' if a category has nothing.";

    private const string SummaryFinalPrompt =
        "Turn these notes into a final meeting summary in exactly this format:\n\n" +
        "## Summary\n2-4 sentences.\n\n## Decisions\n- item\n\n## Action items\n- Who: what\n\n" +
        "Merge duplicates, keep it brief, write 'None' for empty sections, and don't add anything that isn't in the notes.";

    /// <summary>
    /// Long transcripts don't fit in a small model at once, so this reads them in parts,
    /// takes notes on each part, then combines the notes into the final result.
    /// </summary>
    public async Task<string> BuildNotesAsync(string transcript, NotesKind kind, Action<string> status, Action<string> onText, CancellationToken ct)
    {
        var (system, partPrompt, finalPrompt) = kind == NotesKind.Training
            ? (TrainingSystem, TrainingPartPrompt, TrainingFinalPrompt)
            : (SummarySystem, SummaryPartPrompt, SummaryFinalPrompt);

        var chunkChars = (ContextTokens - ReplyTokens - 500) * 3;

        // Short transcript: one pass.
        if (transcript.Length <= chunkChars)
        {
            status("Writing...");
            return await ChatAsync(new[]
            {
                ChatMessage.FromSystem(system),
                ChatMessage.FromUser(finalPrompt.Replace("these notes", "this transcript") + "\n\nTRANSCRIPT:\n" + transcript)
            }, onText, ReplyTokens, ct);
        }

        // Long transcript: notes per part, then combine (repeating if the notes are still too long).
        var text = transcript;
        var round = 0;
        while (text.Length > chunkChars && round < 4)
        {
            round++;
            var parts = SplitByLines(text, chunkChars);
            var notes = new List<string>();
            for (var i = 0; i < parts.Count; i++)
            {
                status(round == 1 ? $"Reading part {i + 1} of {parts.Count}..." : $"Combining notes ({i + 1} of {parts.Count})...");
                var prompt = round == 1 ? partPrompt : "Merge these notes into one shorter set with the same headings, removing duplicates.";
                notes.Add(await ChatAsync(new[]
                {
                    ChatMessage.FromSystem(system),
                    ChatMessage.FromUser($"{prompt}\n\nPART {i + 1} OF {parts.Count}:\n{parts[i]}")
                }, null, PartNotesTokens, ct));
            }

            text = string.Join("\n\n", notes);
        }

        if (text.Length > chunkChars)
        {
            text = text[..chunkChars]; // safety net; shouldn't normally happen
        }

        status("Writing final notes...");
        return await ChatAsync(new[]
        {
            ChatMessage.FromSystem(system),
            ChatMessage.FromUser(finalPrompt + "\n\nNOTES:\n" + text)
        }, onText, ReplyTokens, ct);
    }

    // ======================= helpers =======================

    /// <summary>Reads a transcript file (even while it's still being written) and drops the timestamps.</summary>
    public static string ReadTranscript(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        return Regex.Replace(text, @"^\d{2}:\d{2}:\d{2} ", "", RegexOptions.Multiline).Trim();
    }

    private static int EstimateTokens(IEnumerable<ChatMessage> messages) =>
        messages.Sum(m => (m.Content?.Length ?? 0) / 3 + 4);

    // Keeps chat history to about a third of what the model can hold, dropping the oldest turns first.
    private void TrimHistory(List<ChatMessage> history)
    {
        while (history.Count > 2 && EstimateTokens(history) > ContextTokens / 3)
        {
            history.RemoveRange(0, 2);
        }
    }

    private static List<string> SplitByLines(string text, int maxChars)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            if (current.Length + line.Length + 1 > maxChars && current.Length > 0)
            {
                parts.Add(current.ToString());
                current.Clear();
            }

            current.AppendLine(line.Length > maxChars ? line[..maxChars] : line);
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }

    // For long transcripts: keep the sections that share the most words with the question.
    private static string SelectRelevant(string transcript, string question, int maxChars)
    {
        if (transcript.Length <= maxChars)
        {
            return transcript;
        }

        var keywords = Regex.Matches(question.ToLowerInvariant(), @"[a-z0-9]{4,}").Select(m => m.Value).Distinct().ToList();
        var sections = SplitByLines(transcript, 1500);
        var ranked = sections
            .Select((s, i) => (Index: i, Text: s, Score: keywords.Sum(k => Regex.Matches(s.ToLowerInvariant(), Regex.Escape(k)).Count)))
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Index) // ties: prefer later (more recent) parts
            .ToList();

        var chosen = new List<(int Index, string Text)>();
        var total = 0;
        foreach (var s in ranked)
        {
            if (total + s.Text.Length > maxChars)
            {
                continue;
            }

            chosen.Add((s.Index, s.Text));
            total += s.Text.Length;
        }

        return string.Join("\n...\n", chosen.OrderBy(c => c.Index).Select(c => c.Text));
    }
}
