using System.Text.RegularExpressions;
using Lingarr.Core.Entities;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models.FileSystem;
using Microsoft.Extensions.Logging;

namespace Lingarr.Server.Services;

public partial class SubtitlePostProcessingService(
    ILogger<SubtitlePostProcessingService> logger,
    ISubtitleQualityAnalyzer subtitleQualityAnalyzer) : ISubtitlePostProcessingService
{
    private const int MaxPerLineWarnings = 25;

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
        var perReasonCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var subtitle in translatedSubtitles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (var i = 0; i < subtitle.TranslatedLines.Count; i++)
            {
                var sourceLine = GetSourceLine(subtitle, i);
                var originalLine = subtitle.TranslatedLines[i] ?? string.Empty;
                var cleanedLine = CleanLine(originalLine, sourceLine);

                subtitle.TranslatedLines[i] = cleanedLine;

                var suspiciousReasons = subtitleQualityAnalyzer.GetSuspiciousReasons(
                    cleanedLine,
                    sourceLine,
                    translationRequest.TargetLanguage);

                if (suspiciousReasons.Count == 0)
                {
                    continue;
                }

                suspiciousCount++;
                foreach (var reason in suspiciousReasons)
                {
                    perReasonCounts[reason] = perReasonCounts.GetValueOrDefault(reason) + 1;
                }

                if (suspiciousCount <= MaxPerLineWarnings)
                {
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
                "Subtitle post-processing flagged suspicious lines. RequestId={RequestId}, Count={Count}, Reasons={Reasons}",
                translationRequest.Id,
                suspiciousCount,
                string.Join(",", perReasonCounts.Select(x => $"{x.Key}:{x.Value}")));

            if (suspiciousCount > MaxPerLineWarnings)
            {
                logger.LogInformation(
                    "Suppressed additional suspicious line warnings. RequestId={RequestId}, SuppressedCount={SuppressedCount}",
                    translationRequest.Id,
                    suspiciousCount - MaxPerLineWarnings);
            }
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
        if (string.IsNullOrWhiteSpace(input)) return input;
        if (!IsWrappedInQuotes(input) || IsWrappedInQuotes(sourceLine)) return input;
        return input[1..^1].Trim();
    }

    private static bool IsWrappedInQuotes(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length < 2) return false;
        return (trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '“' && trimmed[^1] == '”');
    }

    private static string NormalizeSpacing(string input)
    {
        var normalized = MultipleSpacesRegex().Replace(input, " ");
        normalized = SpaceBeforePunctuationRegex().Replace(normalized, "$1");
        return normalized.Trim();
    }

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultipleSpacesRegex();

    [GeneratedRegex(@"\s+([,.;:!?])")]
    private static partial Regex SpaceBeforePunctuationRegex();
}
