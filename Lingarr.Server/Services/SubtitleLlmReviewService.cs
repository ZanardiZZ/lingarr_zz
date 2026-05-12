using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Translation;
using Lingarr.Server.Models;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Services.Subtitle;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Lingarr.Server.Services;

public class SubtitleLlmReviewService(
    ILogger<SubtitleLlmReviewService> logger,
    LingarrDbContext dbContext,
    ISettingService settingService,
    ITranslationServiceFactory translationServiceFactory,
    ISubtitleQualityAnalyzer subtitleQualityAnalyzer) : ISubtitleLlmReviewService
{
    private static readonly HashSet<string> CloudLlmProviders =
    ["openai", "gemini", "anthropic", "deepseek"];

    public async Task<List<SubtitleItem>> ReviewLines(
        List<SubtitleItem> translatedSubtitles,
        TranslationRequest translationRequest,
        bool stripSubtitleFormatting,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingService.GetSettings([
            SettingKeys.Translation.LlmReviewerEnabled,
            SettingKeys.Translation.LlmReviewerProvider,
            SettingKeys.Translation.LlmReviewerSamplePercent,
            SettingKeys.Translation.LlmReviewerLogAttempts
        ]);

        if (!ParseBool(settings, SettingKeys.Translation.LlmReviewerEnabled, false))
        {
            return translatedSubtitles;
        }

        var provider = NormalizeProvider(settings.GetValueOrDefault(
            SettingKeys.Translation.LlmReviewerProvider,
            "openai"));
        var samplePercent = ParseInt(settings, SettingKeys.Translation.LlmReviewerSamplePercent, 10, 0, 100);
        var logAttempts = ParseBool(settings, SettingKeys.Translation.LlmReviewerLogAttempts, true);

        var candidates = BuildCandidates(
            translatedSubtitles,
            translationRequest.TargetLanguage,
            stripSubtitleFormatting,
            samplePercent);

        if (candidates.Count == 0)
        {
            await PersistReviewSummary(translationRequest.Id, provider, 0, 0, 0, 0, 0, [], cancellationToken);
            return translatedSubtitles;
        }

        var translationService = translationServiceFactory.CreateTranslationService(provider);
        var translator = new SubtitleTranslationService(translationService, logger);

        var reviewedCount = 0;
        var changedCount = 0;
        var failedCount = 0;
        var suspiciousReviewedCount = 0;
        var sampledReviewedCount = 0;
        var reasonCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reviewedCount++;

            if (candidate.IsSuspicious)
            {
                suspiciousReviewedCount++;
                CountReasons(reasonCounts, candidate.Reasons);
            }
            else
            {
                sampledReviewedCount++;
            }

            try
            {
                var prompt = BuildReviewPrompt(
                    candidate.SourceLine,
                    candidate.CurrentTranslation,
                    candidate.Reasons,
                    translationRequest.TargetLanguage);

                var reviewRaw = await translator.TranslateSubtitleLine(new TranslateAbleSubtitleLine
                {
                    SubtitleLine = prompt,
                    SourceLanguage = translationRequest.SourceLanguage,
                    TargetLanguage = translationRequest.TargetLanguage
                }, cancellationToken);

                var reviewCleaned = CleanReviewResponse(reviewRaw, stripSubtitleFormatting);
                if (string.IsNullOrWhiteSpace(reviewCleaned))
                {
                    failedCount++;
                    if (logAttempts)
                    {
                        logger.LogInformation(
                            "LLM reviewer kept previous translation due to empty response. RequestId={RequestId}, Position={Position}, LineIndex={LineIndex}, Provider={Provider}, Outcome=empty_response",
                            translationRequest.Id,
                            candidate.Subtitle.Position,
                            candidate.LineIndex,
                            provider);
                    }
                    continue;
                }

                if (!string.Equals(candidate.CurrentTranslation, reviewCleaned, StringComparison.Ordinal))
                {
                    candidate.Subtitle.TranslatedLines[candidate.LineIndex] = reviewCleaned;
                    changedCount++;
                }

                if (logAttempts)
                {
                    logger.LogInformation(
                        "LLM reviewer completed. RequestId={RequestId}, Position={Position}, LineIndex={LineIndex}, Provider={Provider}, CandidateType={CandidateType}, Changed={Changed}, Reasons={Reasons}",
                        translationRequest.Id,
                        candidate.Subtitle.Position,
                        candidate.LineIndex,
                        provider,
                        candidate.IsSuspicious ? "suspicious" : "sampled",
                        !string.Equals(candidate.CurrentTranslation, reviewCleaned, StringComparison.Ordinal),
                        string.Join(",", candidate.Reasons));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failedCount++;
                logger.LogWarning(ex,
                    "LLM reviewer failed. RequestId={RequestId}, Position={Position}, LineIndex={LineIndex}, Provider={Provider}, Outcome=failed",
                    translationRequest.Id,
                    candidate.Subtitle.Position,
                    candidate.LineIndex,
                    provider);
            }
        }

        AssertMetadataInvariants(candidates, translationRequest.Id);

        logger.LogInformation(
            "LLM reviewer summary. RequestId={RequestId}, Provider={Provider}, Reviewed={Reviewed}, Changed={Changed}, Failed={Failed}, SuspiciousReviewed={SuspiciousReviewed}, SampledReviewed={SampledReviewed}",
            translationRequest.Id,
            provider,
            reviewedCount,
            changedCount,
            failedCount,
            suspiciousReviewedCount,
            sampledReviewedCount);

        await PersistReviewSummary(
            translationRequest.Id,
            provider,
            reviewedCount,
            changedCount,
            failedCount,
            suspiciousReviewedCount,
            sampledReviewedCount,
            reasonCounts,
            cancellationToken);

        return translatedSubtitles;
    }

    private List<ReviewCandidate> BuildCandidates(
        List<SubtitleItem> translatedSubtitles,
        string targetLanguage,
        bool stripSubtitleFormatting,
        int samplePercent)
    {
        var suspiciousCandidates = new List<ReviewCandidate>();
        var goodCandidates = new List<ReviewCandidate>();

        foreach (var subtitle in translatedSubtitles)
        {
            var originalPosition = subtitle.Position;
            var originalStartTime = subtitle.StartTime;
            var originalEndTime = subtitle.EndTime;
            var originalCueLineCount = subtitle.TranslatedLines.Count;

            for (var i = 0; i < subtitle.TranslatedLines.Count; i++)
            {
                var sourceLine = GetSourceLine(subtitle, i, stripSubtitleFormatting);
                var currentTranslation = subtitle.TranslatedLines[i] ?? string.Empty;
                var analysis = subtitleQualityAnalyzer.Analyze(
                    currentTranslation,
                    sourceLine,
                    targetLanguage);

                var candidate = new ReviewCandidate(
                    subtitle,
                    i,
                    sourceLine,
                    currentTranslation,
                    analysis.Reasons,
                    analysis.Reasons.Count > 0,
                    originalPosition,
                    originalStartTime,
                    originalEndTime,
                    originalCueLineCount);

                if (candidate.IsSuspicious)
                {
                    suspiciousCandidates.Add(candidate);
                }
                else
                {
                    goodCandidates.Add(candidate);
                }
            }
        }

        var sampleCount = (int)Math.Ceiling(goodCandidates.Count * (samplePercent / 100d));
        var sampledCandidates = goodCandidates
            .OrderByDescending(candidate => candidate.CurrentTranslation.Length)
            .ThenBy(candidate => candidate.Subtitle.Position)
            .ThenBy(candidate => candidate.LineIndex)
            .Take(sampleCount);

        return suspiciousCandidates
            .Concat(sampledCandidates)
            .OrderBy(candidate => candidate.Subtitle.Position)
            .ThenBy(candidate => candidate.LineIndex)
            .ToList();
    }

    private static bool ParseBool(Dictionary<string, string> settings, string key, bool defaultValue)
        => settings.TryGetValue(key, out var value) ? value == "true" : defaultValue;

    private static int ParseInt(Dictionary<string, string> settings, string key, int defaultValue, int min, int max)
    {
        if (!settings.TryGetValue(key, out var value) || !int.TryParse(value, out var parsed))
        {
            return defaultValue;
        }

        return Math.Clamp(parsed, min, max);
    }

    private static string NormalizeProvider(string? provider)
    {
        var normalized = string.IsNullOrWhiteSpace(provider)
            ? "openai"
            : provider.Trim().ToLowerInvariant();

        return CloudLlmProviders.Contains(normalized) ? normalized : "openai";
    }

    private static string CleanReviewResponse(string response, bool stripSubtitleFormatting)
    {
        var cleaned = response.Trim();
        return stripSubtitleFormatting
            ? SubtitleFormatterService.RemoveMarkup(cleaned).Trim()
            : cleaned;
    }

    private static void CountReasons(Dictionary<string, int> reasonCounts, IEnumerable<string> reasons)
    {
        foreach (var reason in reasons)
        {
            reasonCounts[reason] = reasonCounts.GetValueOrDefault(reason) + 1;
        }
    }

    private static string GetSourceLine(SubtitleItem subtitle, int lineIndex, bool stripSubtitleFormatting)
    {
        var sourceLines = stripSubtitleFormatting ? subtitle.PlaintextLines : subtitle.Lines;
        if (lineIndex < sourceLines.Count)
        {
            return sourceLines[lineIndex] ?? string.Empty;
        }

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

    private static string BuildReviewPrompt(
        string sourceText,
        string currentTranslation,
        List<string> reasons,
        string targetLanguage)
    {
        var reasonsSection = reasons.Count == 0
            ? "No local issues were detected. This line was selected for quality sampling."
            : string.Join(", ", reasons);

        return $"""
You are reviewing a subtitle translation.
Target language: {targetLanguage}
Detected issues: {reasonsSection}

SOURCE_TEXT:
{sourceText}

CURRENT_TRANSLATION:
{currentTranslation}

Return only the best translated subtitle text.
Do not add labels, notes, explanations, markdown, quotes, JSON, or metadata.
Preserve natural subtitle phrasing and keep line breaks when appropriate.
""";
    }

    private static void AssertMetadataInvariants(IEnumerable<ReviewCandidate> candidates, int requestId)
    {
        foreach (var candidate in candidates)
        {
            if (candidate.Subtitle.Position != candidate.OriginalPosition
                || candidate.Subtitle.StartTime != candidate.OriginalStartTime
                || candidate.Subtitle.EndTime != candidate.OriginalEndTime
                || candidate.Subtitle.TranslatedLines.Count != candidate.OriginalCueLineCount)
            {
                throw new InvalidOperationException(
                    $"LLM reviewer modified subtitle metadata invariants. RequestId={requestId}, Position={candidate.OriginalPosition}");
            }
        }
    }

    private async Task PersistReviewSummary(
        int translationRequestId,
        string provider,
        int reviewedCount,
        int changedCount,
        int failedCount,
        int suspiciousReviewedCount,
        int sampledReviewedCount,
        Dictionary<string, int> reasonCounts,
        CancellationToken cancellationToken)
    {
        var request = await dbContext.TranslationRequests
            .FirstOrDefaultAsync(r => r.Id == translationRequestId, cancellationToken);

        if (request == null)
        {
            return;
        }

        request.LlmReviewReviewedCount = reviewedCount;
        request.LlmReviewChangedCount = changedCount;
        request.LlmReviewFailedCount = failedCount;
        request.LlmReviewSuspiciousReviewedCount = suspiciousReviewedCount;
        request.LlmReviewSampledReviewedCount = sampledReviewedCount;
        request.LlmReviewProvider = provider;
        request.LlmReviewReasonCountsJson = JsonSerializer.Serialize(reasonCounts);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private sealed record ReviewCandidate(
        SubtitleItem Subtitle,
        int LineIndex,
        string SourceLine,
        string CurrentTranslation,
        List<string> Reasons,
        bool IsSuspicious,
        int OriginalPosition,
        int OriginalStartTime,
        int OriginalEndTime,
        int OriginalCueLineCount);
}
