using System.Text.RegularExpressions;

namespace MeetingAssistant;

// Turns the Live Captions text (which keeps changing and scrolling) into finished sentences,
// each saved once. Kept separate from the window-reading code so it can be tested on its own.
static class CaptionText
{
    /// <summary>Splits caption text into sentences (Live Captions adds punctuation).</summary>
    public static List<string> SplitSentences(string text)
    {
        var flat = Regex.Replace(text, @"\s+", " ").Trim();
        if (flat.Length == 0)
        {
            return new List<string>();
        }

        return Regex.Split(flat, @"(?<=[.!?])\s+")
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Sentences safe to save now. The sentence being spoken, plus the last finished one
    /// (Live Captions sometimes still fixes it), are held back until later.
    /// </summary>
    public static (List<string> Settled, string InProgress) Settle(List<string> sentences, bool lastFinishedIsStable = false)
    {
        var lastIsComplete = sentences.Count > 0 && ".!?".Contains(sentences[^1][^1]);
        var complete = lastIsComplete ? sentences.Count : sentences.Count - 1;
        // The newest finished sentence is held back until it has stopped changing.
        var settledCount = lastFinishedIsStable ? Math.Max(0, complete) : Math.Max(0, complete - 1);
        return (sentences.Take(settledCount).ToList(), string.Join(" ", sentences.Skip(settledCount)));
    }

    /// <summary>The newest finished sentence, or "" if there isn't one.</summary>
    public static string LastFinished(List<string> sentences)
    {
        var lastIsComplete = sentences.Count > 0 && ".!?".Contains(sentences[^1][^1]);
        var complete = lastIsComplete ? sentences.Count : sentences.Count - 1;
        return complete > 0 ? sentences[complete - 1] : "";
    }

    /// <summary>Finished sentences only (used as the starting point when you press Start, so older text isn't saved).</summary>
    public static List<string> Baseline(string text)
    {
        var sentences = SplitSentences(text);
        var lastIsComplete = sentences.Count > 0 && ".!?".Contains(sentences[^1][^1]);
        return lastIsComplete ? sentences : sentences.Take(sentences.Count - 1).ToList();
    }

    /// <summary>
    /// Returns sentences in <paramref name="current"/> that come after what was already seen in
    /// <paramref name="previous"/>. Works even when old text scrolls off the top.
    /// Gap = true when it couldn't find where it left off, so some text may have been lost.
    /// </summary>
    public static (List<string> Sentences, bool Gap) NewSentences(List<string> previous, List<string> current)
    {
        if (previous.Count == 0)
        {
            return (current.ToList(), false);
        }

        var prev = previous.Select(Normalize).ToList();
        var cur = current.Select(Normalize).ToList();

        // Find where the last 2 (then last 1) sentences we already saw appear in the new text.
        for (var k = Math.Min(2, prev.Count); k >= 1; k--)
        {
            var anchor = prev.Skip(prev.Count - k).ToList();
            for (var j = cur.Count - k; j >= 0; j--)
            {
                if (cur.Skip(j).Take(k).SequenceEqual(anchor))
                {
                    return (current.Skip(j + k).ToList(), false);
                }
            }
        }

        // The latest sentence may have been reworded; try anchoring on slightly older ones.
        for (var back = 2; back <= Math.Min(prev.Count, 5); back++)
        {
            var anchor = prev[^back];
            for (var j = cur.Count - 1; j >= 0; j--)
            {
                if (cur[j] == anchor)
                {
                    return (current.Skip(j + 1).ToList(), false);
                }
            }
        }

        // No overlap: text scrolled away between reads, or Live Captions restarted.
        // Skip anything already seen, and a cut-off piece of a sentence we already have.
        var fresh = current
            .Where(c =>
            {
                var n = Normalize(c);
                return n.Length > 0 && !prev.Any(p => p == n || p.EndsWith(n, StringComparison.Ordinal));
            })
            .ToList();
        // Only a real gap if nothing in the new text overlaps what was already saved.
        var overlaps = cur.Any(n => n.Length > 0 && prev.Any(p => p == n || p.EndsWith(n, StringComparison.Ordinal)));
        return (fresh, fresh.Count > 0 && !overlaps);
    }

    /// <summary>Lowercase letters and digits only, so small punctuation fixes don't count as new text.</summary>
    public static string Normalize(string sentence) =>
        Regex.Replace(sentence.ToLowerInvariant(), @"[^a-z0-9]+", "");
}
