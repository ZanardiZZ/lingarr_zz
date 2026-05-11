using System.Text;
using System.Text.RegularExpressions;
using Lingarr.Core.Entities;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models.FileSystem;
using Microsoft.Extensions.Logging;

namespace Lingarr.Server.Services;

public partial class SubtitlePostProcessingService(ILogger<SubtitlePostProcessingService> logger) : ISubtitlePostProcessingService
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
        "CONTEXT"
    ];

    private static readonly string[] TrailingNotePrefixes = ["Note:", "Nota:"];

    public Task<List<SubtitleItem>> Process(
        List<SubtitleItem> translatedSubtitles,
        TranslationRequest translationRequest,
        CancellationToken cancellationToken = default)
    {
        var suspiciousCount = 0;

        foreach (var subtitle in translatedSubtitles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (var i = 0; i < subtitle.TranslatedLines.Count; i++)
            {
                var sourceLine = GetSourceLine(subtitle, i);
                var originalLine = subtitle.TranslatedLines[i] ?? string.Empty;
                var cleanedLine = CleanLine(originalLine, sourceLine);

                subtitle.TranslatedLines[i] = cleanedLine;

                var suspiciousReasons = GetSuspiciousReasons(cleanedLine, sourceLine);
                if (suspiciousReasons.Count > 0)
                {
                    suspiciousCount++;
                    logger.LogWarning(
                        "Suspicious translated subtitle content detected. RequestId={RequestId}, Position={Position}, LineIndex={LineIndex}, Reasons={Reasons}",
                        translationRequest.Id,
                        subtitle.Position,
                        i,
                        string.Join(",", suspiciousReasons));
                }
            }
        }

        if (suspiciousCount > 0)
        {
            logger.LogInformation(
                "Subtitle post-processing flagged suspicious lines. RequestId={RequestId}, Count={Count}",
                translationRequest.Id,
                suspiciousCount);
        }

        return Task.FromResult(translatedSubtitles);
    }

    private static string GetSourceLine(SubtitleItem subtitle, int lineIndex)
    {
        if (lineIndex < subtitle.PlaintextLines.Count)
        {
            return subtitle.PlaintextLines[lineIndex] ?? string.Empty;
        }

        if (lineIndex < subtitle.Lines.Count)
        {
            return subtitle.Lines[lineIndex] ?? string.Empty;
        }

        return string.Empty;
    }

    private static string CleanLine(string line, string sourceLine)
    {
        var cleaned = line.Trim();

        foreach (var label in LeadingLabels)
        {
            if (cleaned.StartsWith(label, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[label.Length..].TrimStart();
            }
        }

        cleaned = RemovePromptLeakageMarkers(cleaned);
        cleaned = RemoveTrailingNote(cleaned);
        cleaned = UnwrapLineQuotesIfNeeded(cleaned, sourceLine);
        cleaned = NormalizeSpacing(cleaned);

        return cleaned;
    }

    private static string RemovePromptLeakageMarkers(string input)
    {
        var result = input;
        foreach (var marker in PromptLeakageMarkers)
        {
            result = Regex.Replace(result, $@"\b{Regex.Escape(marker)}\b", string.Empty, RegexOptions.IgnoreCase);
        }

        return result;
    }

    private static string RemoveTrailingNote(string input)
    {
        var bestIndex = -1;

        foreach (var prefix in TrailingNotePrefixes)
        {
            var index = input.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && (bestIndex < 0 || index < bestIndex))
            {
                bestIndex = index;
            }
        }

        return bestIndex >= 0 ? input[..bestIndex].TrimEnd() : input;
    }

    private static string UnwrapLineQuotesIfNeeded(string input, string sourceLine)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return input;
        }

        if (!IsWrappedInQuotes(input) || IsWrappedInQuotes(sourceLine))
        {
            return input;
        }

        return input[1..^1].Trim();
    }

    private static bool IsWrappedInQuotes(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length < 2)
        {
            return false;
        }

        var first = trimmed[0];
        var last = trimmed[^1];

        return (first == '"' && last == '"') || (first == '“' && last == '”');
    }

    private static string NormalizeSpacing(string input)
    {
        var normalized = MultipleSpacesRegex().Replace(input, " ");
        normalized = SpaceBeforePunctuationRegex().Replace(normalized, "$1");
        return normalized.Trim();
    }

    private static List<string> GetSuspiciousReasons(string line, string sourceLine)
    {
        var reasons = new List<string>();

        if (ContainsCjk(line))
        {
            reasons.Add("cjk");
        }

        if (ContainsAny(line, PromptLeakageMarkers))
        {
            reasons.Add("prompt_leakage");
        }

        if (ContainsAny(line, LeadingLabels))
        {
            reasons.Add("assistant_label");
        }

        if (HasRepeatedSegment(line))
        {
            reasons.Add("repeated_segment");
        }

        if (!string.IsNullOrWhiteSpace(sourceLine)
            && line.Length > 120
            && line.Length > sourceLine.Length * 4)
        {
            reasons.Add("excessive_length");
        }

        return reasons;
    }

    private static bool ContainsAny(string line, IEnumerable<string> terms)
    {
        var normalizedLine = NormalizeForDetection(line);
        return terms.Any(term => normalizedLine.Contains(NormalizeForDetection(term), StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeForDetection(string input)
        => input.Replace("_", " ").Replace("-", " ");

    private static bool ContainsCjk(string line)
    {
        foreach (var rune in line.EnumerateRunes())
        {
            var value = rune.Value;
            if ((value >= 0x4E00 && value <= 0x9FFF)
                || (value >= 0x3400 && value <= 0x4DBF)
                || (value >= 0x3040 && value <= 0x30FF)
                || (value >= 0xAC00 && value <= 0xD7AF))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasRepeatedSegment(string line)
    {
        var normalized = NormalizeSpacing(line);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 4)
        {
            var midpoint = words.Length / 2;
            if (words.Length % 2 == 0)
            {
                var firstHalf = string.Join(' ', words.Take(midpoint));
                var secondHalf = string.Join(' ', words.Skip(midpoint));
                if (string.Equals(firstHalf, secondHalf, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        var textLength = normalized.Length;
        for (var size = 8; size <= textLength / 2; size++)
        {
            var segment = normalized[..size];
            if (normalized.StartsWith(segment + segment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultipleSpacesRegex();

    [GeneratedRegex(@"\s+([,.;:!?])")]
    private static partial Regex SpaceBeforePunctuationRegex();
}
