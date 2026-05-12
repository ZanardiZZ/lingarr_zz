using Lingarr.Core.Configuration;
using Lingarr.Core.Entities;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Translation;
using Lingarr.Server.Models;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Services.Subtitle;

namespace Lingarr.Server.Services;

public class SubtitleSelectiveRetryService(
    ILogger<SubtitleSelectiveRetryService> logger,
    ISettingService settingService,
    ITranslationServiceFactory translationServiceFactory,
    ISubtitleQualityAnalyzer subtitleQualityAnalyzer) : ISubtitleSelectiveRetryService
{
    private static readonly HashSet<string> HighSeverityReasons =
    ["prompt_leakage", "assistant_label", "cjk", "repeated_segment", "excessive_length"];

    private static readonly HashSet<string> LlmProviderScope =
    ["openai", "localai", "gemini", "anthropic", "deepseek"];

    public async Task<List<SubtitleItem>> RetrySuspiciousLines(
        List<SubtitleItem> translatedSubtitles,
        TranslationRequest translationRequest,
        string serviceType,
        bool stripSubtitleFormatting,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingService.GetSettings([
            SettingKeys.Translation.SelectiveRetryEnabled,
            SettingKeys.Translation.SelectiveRetryMaxAttempts,
            SettingKeys.Translation.SelectiveRetryHighSeverityOnly,
            SettingKeys.Translation.SelectiveRetryProviderScope,
            SettingKeys.Translation.SelectiveRetryLogAttempts
        ]);

        if (!ParseBool(settings, SettingKeys.Translation.SelectiveRetryEnabled, true))
        {
            return translatedSubtitles;
        }

        var providerScope = settings[SettingKeys.Translation.SelectiveRetryProviderScope];
        if (!IsProviderAllowed(serviceType, providerScope))
        {
            logger.LogInformation("Selective retry skipped due to provider scope. RequestId={RequestId}, ServiceType={ServiceType}, Scope={Scope}",
                translationRequest.Id, serviceType, providerScope);
            return translatedSubtitles;
        }

        var highSeverityOnly = ParseBool(settings, SettingKeys.Translation.SelectiveRetryHighSeverityOnly, true);
        var logAttempts = ParseBool(settings, SettingKeys.Translation.SelectiveRetryLogAttempts, true);
        var maxAttempts = int.TryParse(settings[SettingKeys.Translation.SelectiveRetryMaxAttempts], out var parsedAttempts)
            ? Math.Clamp(parsedAttempts, 0, 2)
            : 1;

        if (maxAttempts <= 0)
        {
            return translatedSubtitles;
        }

        var translationService = translationServiceFactory.CreateTranslationService(serviceType);
        var translator = new SubtitleTranslationService(translationService, logger);

        foreach (var subtitle in translatedSubtitles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (var i = 0; i < subtitle.TranslatedLines.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var sourceLine = GetSourceLine(subtitle, i, stripSubtitleFormatting);
                var currentBest = subtitle.TranslatedLines[i] ?? string.Empty;
                var currentReasons = subtitleQualityAnalyzer.GetSuspiciousReasons(currentBest, sourceLine, translationRequest.TargetLanguage);

                if (currentReasons.Count == 0)
                {
                    continue;
                }

                var retryReasons = highSeverityOnly
                    ? currentReasons.Where(r => HighSeverityReasons.Contains(r)).ToList()
                    : currentReasons;

                if (retryReasons.Count == 0)
                {
                    if (logAttempts)
                    {
                        logger.LogInformation("Selective retry skipped. RequestId={RequestId}, Position={Position}, LineIndex={LineIndex}, Reasons={Reasons}, Outcome=skipped_low_severity",
                            translationRequest.Id, subtitle.Position, i, string.Join(",", currentReasons));
                    }
                    continue;
                }

                var bestHighSeverityCount = CountHighSeverity(currentReasons);

                for (var attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var retryPrompt = BuildRetryPrompt(sourceLine, currentBest, retryReasons, translationRequest.TargetLanguage);
                        var retryRaw = await translator.TranslateSubtitleLine(new TranslateAbleSubtitleLine
                        {
                            SubtitleLine = retryPrompt,
                            SourceLanguage = translationRequest.SourceLanguage,
                            TargetLanguage = translationRequest.TargetLanguage
                        }, cancellationToken);

                        var retryCleaned = stripSubtitleFormatting
                            ? SubtitleFormatterService.RemoveMarkup(retryRaw)
                            : retryRaw.Trim();

                        var retryReasonsDetected = subtitleQualityAnalyzer.GetSuspiciousReasons(retryCleaned, sourceLine, translationRequest.TargetLanguage);
                        var retryHighSeverityCount = CountHighSeverity(retryReasonsDetected);

                        var improved = retryHighSeverityCount < bestHighSeverityCount
                                       || (retryHighSeverityCount == bestHighSeverityCount
                                           && retryReasonsDetected.Count < currentReasons.Count
                                           && !string.IsNullOrWhiteSpace(retryCleaned));

                        if (improved)
                        {
                            currentBest = retryCleaned;
                            currentReasons = retryReasonsDetected;
                            bestHighSeverityCount = retryHighSeverityCount;
                        }

                        if (logAttempts)
                        {
                            logger.LogInformation(
                                "Selective retry attempt completed. RequestId={RequestId}, Position={Position}, LineIndex={LineIndex}, Attempt={Attempt}, Improved={Improved}, ReasonsBefore={ReasonsBefore}, ReasonsAfter={ReasonsAfter}",
                                translationRequest.Id,
                                subtitle.Position,
                                i,
                                attempt,
                                improved,
                                string.Join(",", retryReasons),
                                string.Join(",", retryReasonsDetected));
                        }

                        if (bestHighSeverityCount == 0)
                        {
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex,
                            "Selective retry failed. RequestId={RequestId}, Position={Position}, LineIndex={LineIndex}, Attempt={Attempt}",
                            translationRequest.Id,
                            subtitle.Position,
                            i,
                            attempt);
                    }
                }

                subtitle.TranslatedLines[i] = currentBest;
            }
        }

        return translatedSubtitles;
    }

    private static bool ParseBool(Dictionary<string, string> settings, string key, bool defaultValue)
        => settings.TryGetValue(key, out var value) ? value == "true" : defaultValue;

    private static int CountHighSeverity(IEnumerable<string> reasons)
        => reasons.Count(reason => HighSeverityReasons.Contains(reason));

    private static bool IsProviderAllowed(string serviceType, string providerScope)
    {
        if (string.Equals(providerScope, "all", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return LlmProviderScope.Contains(serviceType);
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

    private static string BuildRetryPrompt(string sourceText, string previousTranslation, List<string> reasons, string targetLanguage)
    {
        return $"""
You are correcting a subtitle translation.
Target language: {targetLanguage}
Detected issues: {string.Join(", ", reasons)}

SOURCE_TEXT:
{sourceText}

PREVIOUS_TRANSLATION:
{previousTranslation}

Return only the corrected translated subtitle text.
Do not add labels, notes, explanations, markdown, quotes, JSON, or metadata.
Preserve natural subtitle phrasing and keep line breaks when appropriate.
""";
    }
}
