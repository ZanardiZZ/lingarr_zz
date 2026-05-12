using System.Text.RegularExpressions;
using Lingarr.Server.Interfaces.Services;

namespace Lingarr.Server.Services;

public partial class SubtitleQualityAnalyzer : ISubtitleQualityAnalyzer
{
    private static readonly string[] LeadingLabels =
    [
        "Translation in Portuguese:",
        "Translation:",
        "Tradução:",
        "Here is the translation:"
    ];

    private static readonly string[] PromptLeakageMarkers =
    [
        "SUBTITLE_DATA_START",
        "SUBTITLE_DATA_END",
        "TARGET LINE TO TRANSLATE",
        "TARGET LINE",
        "CONTEXT BEFORE",
        "CONTEXT AFTER",
        "TARGET",
        "CONTEXT",
        "NOTE"
    ];

    private static readonly HashSet<string> NonNameLeadingWords =
    ["I", "The", "A", "An", "We", "You", "He", "She", "They", "It", "This", "That"];

    public List<string> GetSuspiciousReasons(string translatedLine, string sourceLine, string? targetLanguage)
    {
        var reasons = new List<string>();
        var normalizedSource = NormalizeSpacing(sourceLine);
        var normalizedLine = NormalizeSpacing(translatedLine);

        if (ContainsCjk(normalizedLine)) reasons.Add("cjk");
        if (ContainsAny(normalizedLine, PromptLeakageMarkers)) reasons.Add("prompt_leakage");
        if (ContainsAny(normalizedLine, LeadingLabels)) reasons.Add("assistant_label");
        if (HasRepeatedSegment(normalizedLine)) reasons.Add("repeated_segment");

        if (!string.IsNullOrWhiteSpace(sourceLine)
            && normalizedLine.Length > 120
            && normalizedLine.Length > normalizedSource.Length * 4)
        {
            reasons.Add("excessive_length");
        }

        if (HasEnglishLeftovers(normalizedLine, normalizedSource, targetLanguage)) reasons.Add("possible_english_leftover");
        if (HasMissingParentheticalCue(normalizedLine, normalizedSource)) reasons.Add("missing_parenthetical_cue");
        if (HasAddedSpeakerLabel(normalizedLine, normalizedSource)) reasons.Add("added_speaker_label");
        if (HasLikelyChangedProperNoun(normalizedLine, normalizedSource)) reasons.Add("changed_proper_noun");

        return reasons;
    }

    private static bool ContainsAny(string line, IEnumerable<string> terms)
    {
        var normalizedLine = NormalizeForDetection(line);
        return terms.Any(term => normalizedLine.Contains(NormalizeForDetection(term), StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeForDetection(string input)
        => input.Replace("_", " ").Replace("-", " ");

    private static string NormalizeSpacing(string input)
    {
        var normalized = MultipleSpacesRegex().Replace(input, " ");
        normalized = SpaceBeforePunctuationRegex().Replace(normalized, "$1");
        return normalized.Trim();
    }

    private static bool ContainsCjk(string line)
    {
        foreach (var rune in line.EnumerateRunes())
        {
            var value = rune.Value;
            if ((value >= 0x4E00 && value <= 0x9FFF)
                || (value >= 0x3400 && value <= 0x4DBF)
                || (value >= 0x3040 && value <= 0x30FF)
                || (value >= 0xAC00 && value <= 0xD7AF)) return true;
        }

        return false;
    }

    private static bool HasRepeatedSegment(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 4 && words.Length % 2 == 0)
        {
            var midpoint = words.Length / 2;
            var firstHalf = string.Join(' ', words.Take(midpoint));
            var secondHalf = string.Join(' ', words.Skip(midpoint));
            if (string.Equals(firstHalf, secondHalf, StringComparison.OrdinalIgnoreCase)) return true;
        }

        for (var size = 8; size <= line.Length / 2; size++)
        {
            var segment = line[..size];
            if (line.StartsWith(segment + segment, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static bool HasEnglishLeftovers(string line, string sourceLine, string? targetLanguage)
    {
        if (IsEnglishLanguage(targetLanguage)) return false;
        if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(sourceLine)) return false;

        var sourceWords = EnglishWordRegex().Matches(sourceLine).Select(m => m.Value.ToLowerInvariant()).ToHashSet();
        if (sourceWords.Count == 0) return false;

        var lineWords = EnglishWordRegex().Matches(line).Select(m => m.Value.ToLowerInvariant()).ToList();
        if (lineWords.Count == 0) return false;

        var overlap = lineWords.Count(word => sourceWords.Contains(word));
        return overlap >= 3 && overlap >= Math.Max(3, sourceWords.Count / 2);
    }

    private static bool HasMissingParentheticalCue(string line, string sourceLine)
    {
        var sourceHasCue = ParentheticalCueRegex().IsMatch(sourceLine)
            || BracketCueRegex().IsMatch(sourceLine)
            || AsteriskCueRegex().IsMatch(sourceLine);
        if (!sourceHasCue) return false;

        return !ParentheticalCueRegex().IsMatch(line)
            && !BracketCueRegex().IsMatch(line)
            && !AsteriskCueRegex().IsMatch(line);
    }

    private static bool HasAddedSpeakerLabel(string line, string sourceLine)
    {
        var sourceHasLabel = SpeakerLabelRegex().IsMatch(sourceLine);
        var lineHasLabel = SpeakerLabelRegex().IsMatch(line);
        return !sourceHasLabel && lineHasLabel;
    }

    private static bool HasLikelyChangedProperNoun(string line, string sourceLine)
    {
        if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(sourceLine)) return false;
        var sourceNames = GetLikelyNames(sourceLine);
        if (sourceNames.Count == 0) return false;

        var lineNameSet = GetLikelyNames(line).ToHashSet(StringComparer.Ordinal);
        return sourceNames.Any(name => !lineNameSet.Contains(name));
    }

    private static List<string> GetLikelyNames(string text)
    {
        var names = new List<string>();
        foreach (Match match in ProperNounRegex().Matches(text))
        {
            var value = match.Value.Trim();
            var hasWhitespace = value.Contains(' ');
            var isAtStart = match.Index == 0;

            if (!hasWhitespace && isAtStart && NonNameLeadingWords.Contains(value)) continue;
            if (hasWhitespace || !isAtStart) names.Add(value);
        }

        return names.Distinct(StringComparer.Ordinal).ToList();
    }

    private static bool IsEnglishLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return false;
        var normalized = language.Trim().ToLowerInvariant();
        return normalized is "en" or "eng" or "english";
    }

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultipleSpacesRegex();
    [GeneratedRegex(@"\s+([,.;:!?])")]
    private static partial Regex SpaceBeforePunctuationRegex();
    [GeneratedRegex(@"\b[A-Za-z][A-Za-z'’-]{2,}\b")]
    private static partial Regex EnglishWordRegex();
    [GeneratedRegex(@"\((?:[^)]{1,30})\)")]
    private static partial Regex ParentheticalCueRegex();
    [GeneratedRegex(@"\[(?:[^\]]{1,30})\]")]
    private static partial Regex BracketCueRegex();
    [GeneratedRegex(@"\*(?:[^*]{1,30})\*")]
    private static partial Regex AsteriskCueRegex();
    [GeneratedRegex(@"^\s*(?:[A-Z][A-Za-z]{1,20}|[A-Z]{2,20})\s*:\s+")]
    private static partial Regex SpeakerLabelRegex();
    [GeneratedRegex(@"\b[A-Z][a-z]+(?:\s+[A-Z][a-z]+)*\b")]
    private static partial Regex ProperNounRegex();
}
