using System.Collections.Immutable;

namespace NexusPipeline.Core.Execution;

internal sealed class KeywordJudgementRules
{
    private readonly List<HashSet<string>> _groups = [];
    internal bool IsConfigured => _groups.Count != 0;

    internal KeywordJudgementRules(ImmutableArray<string> expressions)
    {
        foreach (var expression in expressions)
            foreach (var rawLine in expression.Split('\n'))
            {
                var words = rawLine.Trim().Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (words.Length != 0) _groups.Add(new(words, StringComparer.OrdinalIgnoreCase));
            }
    }

    internal int? Consume(string line)
    {
        int? firstCompletion = null;
        foreach (var pending in _groups)
        {
            if (pending.Count == 0) continue;
            var hits = new List<(string Word, int Position)>();
            foreach (var word in pending)
            {
                var position = line.IndexOf(word, StringComparison.OrdinalIgnoreCase);
                if (position >= 0) hits.Add((word, position));
            }
            foreach (var hit in hits) pending.Remove(hit.Word);
            if (pending.Count != 0 || hits.Count == 0) continue;
            // Earlier lines already supplied other words; this group becomes true at its last missing word on this line.
            var completion = hits.Max(hit => hit.Position);
            firstCompletion = firstCompletion is null ? completion : Math.Min(firstCompletion.Value, completion);
        }
        return firstCompletion;
    }
}
