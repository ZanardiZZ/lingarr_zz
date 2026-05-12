using System.Text.RegularExpressions;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models;

namespace Lingarr.Server.Services;

public partial class SubtitleQualityAnalyzer : ISubtitleQualityAnalyzer
{
    private const int PromptLeakageWeight = 100;
    private const int EmptyTranslationWeight = 90;
    private const int AssistantLabelWeight = 80;
    private const int CjkWeight = 70;
    private const int RepeatedSegmentWeight = 65;
    private const int ExcessiveLengthWeight = 60;
    private const int PossibleEnglishLeftoverWeight = 35;
    private const int CueStructureChangedWeight = 35;
    private const int ChangedProperNounWeight = 30;
    private const int MissingParentheticalCueWeight = 25;
    private const int AddedSpeakerLabelWeight = 25;
    private const int ProtectedTermChangedWeight = 75;

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

    private static readonly HashSet<string> EnglishStopWords =
    [
        "the", "and", "for", "you", "your", "are", "was", "were", "this", "that", "with", "have", "has",
        "had", "not", "but", "from", "they", "them", "she", "him", "his", "her", "our", "out", "get",
        "got", "can", "could", "would", "should", "will", "just", "now", "then", "than"
    ];

    private static readonly HashSet<string> TargetLanguageSignals =
    [
        "a", "ao", "aos", "as", "de", "da", "das", "do", "dos", "e", "em", "eu", "meu", "minha",
        "na", "nas", "no", "nos", "o", "os", "para", "por", "que", "se", "um", "uma", "voce",
        "usted", "vous", "avec", "pour", "dans", "und", "der", "die", "das", "nicht", "con", "per"
    ];

    public SubtitleQualityAnalysis Analyze(
        string translatedLine,
        string sourceLine,
        string? targetLanguage,
        IReadOnlyDictionary<string, string>? protectedTerms = null)
    {
        var issues = new List<SubtitleQualityIssue>();
        var normalizedSource = NormalizeSpacing(sourceLine);
        var normalizedLine = NormalizeSpacing(translatedLine);

        if (string.IsNullOrWhiteSpace(normalizedLine)) issues.Add(new SubtitleQualityIssue("empty_translation", EmptyTranslationWeight));
        if (ContainsUnexpectedCjk(normalizedLine, normalizedSource, targetLanguage)) issues.Add(new SubtitleQualityIssue("cjk", CjkWeight));
        if (ContainsAny(normalizedLine, PromptLeakageMarkers)) issues.Add(new SubtitleQualityIssue("prompt_leakage", PromptLeakageWeight));
        if (ContainsAny(normalizedLine, LeadingLabels)) issues.Add(new SubtitleQualityIssue("assistant_label", AssistantLabelWeight));
        if (HasRepeatedSegment(normalizedLine)) issues.Add(new SubtitleQualityIssue("repeated_segment", RepeatedSegmentWeight));

        if (!string.IsNullOrWhiteSpace(sourceLine)
            && normalizedLine.Length > 120
            && normalizedLine.Length > normalizedSource.Length * 4)
        {
            issues.Add(new SubtitleQualityIssue("excessive_length", ExcessiveLengthWeight));
        }

        if (HasEnglishLeftovers(normalizedLine, normalizedSource, targetLanguage)) issues.Add(new SubtitleQualityIssue("possible_english_leftover", PossibleEnglishLeftoverWeight));
        if (HasMissingParentheticalCue(normalizedLine, normalizedSource)) issues.Add(new SubtitleQualityIssue("missing_parenthetical_cue", MissingParentheticalCueWeight));
        if (HasAddedSpeakerLabel(normalizedLine, normalizedSource)) issues.Add(new SubtitleQualityIssue("added_speaker_label", AddedSpeakerLabelWeight));
        if (HasCueStructureChanged(normalizedLine, normalizedSource)) issues.Add(new SubtitleQualityIssue("cue_structure_changed", CueStructureChangedWeight));
        if (HasProtectedTermChanged(normalizedLine, normalizedSource, protectedTerms)) issues.Add(new SubtitleQualityIssue("protected_term_changed", ProtectedTermChangedWeight));
        if (HasLikelyChangedProperNoun(normalizedLine, normalizedSource, protectedTerms)) issues.Add(new SubtitleQualityIssue("changed_proper_noun", ChangedProperNounWeight));

        return new SubtitleQualityAnalysis(issues);
    }

    public List<string> GetSuspiciousReasons(
        string translatedLine,
        string sourceLine,
        string? targetLanguage,
        IReadOnlyDictionary<string, string>? protectedTerms = null)
        => Analyze(translatedLine, sourceLine, targetLanguage, protectedTerms).Reasons;

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

    private static bool ContainsUnexpectedCjk(string line, string sourceLine, string? targetLanguage)
    {
        if (IsCjkLanguage(targetLanguage)) return false;

        var lineCjkCount = CountCjk(line);
        if (lineCjkCount == 0) return false;

        var sourceCjkCount = CountCjk(sourceLine);
        if (sourceCjkCount == 0) return true;

        var cjkRatio = lineCjkCount / (double)Math.Max(1, line.EnumerateRunes().Count());
        return lineCjkCount > sourceCjkCount || cjkRatio > 0.35;
    }

    private static int CountCjk(string line)
    {
        var count = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            var value = rune.Value;
            if ((value >= 0x4E00 && value <= 0x9FFF)
                || (value >= 0x3400 && value <= 0x4DBF)
                || (value >= 0x3040 && value <= 0x30FF)
                || (value >= 0xAC00 && value <= 0xD7AF))
            {
                count++;
            }
        }

        return count;
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

        var sourceWords = EnglishWordRegex().Matches(sourceLine)
            .Where(match => !IsLikelyProtectedEnglishToken(match.Value, match.Index))
            .Select(match => match.Value.ToLowerInvariant())
            .Where(IsContentWord)
            .ToHashSet();
        if (sourceWords.Count == 0) return false;

        var lineWords = EnglishWordRegex().Matches(line)
            .Select(m => m.Value.ToLowerInvariant())
            .ToList();
        if (lineWords.Count == 0) return false;

        var lineContentWords = lineWords.Where(IsContentWord).ToList();
        var overlap = lineContentWords.Count(word => sourceWords.Contains(word));
        if (overlap == 0) return false;

        var copiedRatio = overlap / (double)Math.Max(1, sourceWords.Count);
        var hasTargetLanguageSignals = lineWords.Any(word => TargetLanguageSignals.Contains(RemoveDiacritics(word)));
        if (hasTargetLanguageSignals && overlap < 4 && copiedRatio < 0.70) return false;

        return overlap >= 4 && copiedRatio >= 0.55;
    }

    private static bool IsContentWord(string word)
        => word.Length >= 3 && !EnglishStopWords.Contains(word);

    private static bool IsLikelyProtectedEnglishToken(string word, int index)
    {
        if (word.Length <= 1) return false;
        if (word.All(c => !char.IsLetter(c) || char.IsUpper(c))) return true;
        if (word.Skip(1).Any(char.IsUpper)) return true;
        return index > 0 && char.IsUpper(word[0]);
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

    private static bool HasCueStructureChanged(string line, string sourceLine)
    {
        if (string.IsNullOrWhiteSpace(sourceLine) || string.IsNullOrWhiteSpace(line)) return false;

        var sourceHasCue = ParentheticalCueRegex().IsMatch(sourceLine)
            || BracketCueRegex().IsMatch(sourceLine)
            || AsteriskCueRegex().IsMatch(sourceLine)
            || SpeakerLabelRegex().IsMatch(sourceLine);
        if (!sourceHasCue) return false;

        var lineHasCue = ParentheticalCueRegex().IsMatch(line)
            || BracketCueRegex().IsMatch(line)
            || AsteriskCueRegex().IsMatch(line)
            || SpeakerLabelRegex().IsMatch(line);

        return !lineHasCue;
    }

    private static bool HasProtectedTermChanged(
        string line,
        string sourceLine,
        IReadOnlyDictionary<string, string>? protectedTerms)
    {
        if (protectedTerms == null || protectedTerms.Count == 0) return false;

        foreach (var protectedTerm in protectedTerms)
        {
            if (!ContainsWholeTerm(sourceLine, protectedTerm.Key)) continue;
            if (!ContainsWholeTerm(line, protectedTerm.Value)) return true;
        }

        return false;
    }

    private static bool HasLikelyChangedProperNoun(
        string line,
        string sourceLine,
        IReadOnlyDictionary<string, string>? protectedTerms)
    {
        if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(sourceLine)) return false;
        var sourceNames = GetLikelyNames(sourceLine);
        if (sourceNames.Count == 0) return false;

        return sourceNames.Any(name =>
        {
            var expectedName = GetProtectedTarget(protectedTerms, name) ?? name;
            return !ContainsWholeTerm(line, expectedName);
        });
    }

    private static string? GetProtectedTarget(IReadOnlyDictionary<string, string>? protectedTerms, string sourceTerm)
    {
        if (protectedTerms == null || protectedTerms.Count == 0) return null;

        foreach (var protectedTerm in protectedTerms)
        {
            if (string.Equals(protectedTerm.Key, sourceTerm, StringComparison.OrdinalIgnoreCase))
            {
                return protectedTerm.Value;
            }
        }

        return null;
    }

    private static bool ContainsWholeTerm(string text, string term)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(term)) return false;

        var pattern = $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(term)}(?![\p{{L}}\p{{N}}_])";
        return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
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

    private static bool IsCjkLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return false;
        var normalized = language.Trim().ToLowerInvariant();
        return normalized is "zh" or "zho" or "chi" or "chinese" or "ja" or "jpn" or "japanese" or "ko" or "kor" or "korean";
    }

    private static string RemoveDiacritics(string value)
    {
        var normalized = value.Normalize(System.Text.NormalizationForm.FormD);
        var chars = normalized.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark);
        return new string(chars.ToArray()).Normalize(System.Text.NormalizationForm.FormC);
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
