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
using System.Text.RegularExpressions;

namespace Lingarr.Server.Services;

public class SubtitleSelectiveRetryService(
    ILogger<SubtitleSelectiveRetryService> logger,
    LingarrDbContext dbContext,
    ISettingService settingService,
    ITranslationServiceFactory translationServiceFactory,
    ISubtitleQualityAnalyzer subtitleQualityAnalyzer) : ISubtitleSelectiveRetryService
{
    private const int HighSeverityScoreFloor = 60;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly HashSet<string> LlmProviderScope =
    ["openai", "localai", "gemini", "anthropic", "deepseek"];

    private static readonly HashSet<string> NonNameLeadingWords =
    ["I", "The", "A", "An", "We", "You", "He", "She", "They", "It", "This", "That"];

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
            SettingKeys.Translation.SelectiveRetryLogAttempts,
            SettingKeys.Translation.SelectiveRetryScoreThreshold,
            SettingKeys.Translation.SelectiveRetryImprovementMargin,
            SettingKeys.Translation.SelectiveRetryGlossary,
            SettingKeys.Translation.SelectiveRetryProperNounLockEnabled,
            SettingKeys.Translation.SelectiveRetryProtectedPatterns
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
        var configuredScoreThreshold = ParseInt(settings, SettingKeys.Translation.SelectiveRetryScoreThreshold, 25, 0, 200);
        var retryScoreThreshold = highSeverityOnly
            ? Math.Max(configuredScoreThreshold, HighSeverityScoreFloor)
            : configuredScoreThreshold;
        var improvementMargin = ParseInt(settings, SettingKeys.Translation.SelectiveRetryImprovementMargin, 10, 0, 100);
        var properNounLockEnabled = ParseBool(settings, SettingKeys.Translation.SelectiveRetryProperNounLockEnabled, false);
        var glossary = ParseGlossary(
            settings.GetValueOrDefault(SettingKeys.Translation.SelectiveRetryGlossary, "{}"),
            translationRequest.SourceLanguage,
            translationRequest.TargetLanguage);
        var protectedPatterns = ParseProtectedPatterns(
            settings.GetValueOrDefault(SettingKeys.Translation.SelectiveRetryProtectedPatterns, "[]"));

        if (maxAttempts <= 0)
        {
            return translatedSubtitles;
        }

        var translationService = translationServiceFactory.CreateTranslationService(serviceType);
        var translator = new SubtitleTranslationService(translationService, logger);

        var retryAttemptedCount = 0;
        var retryImprovedCount = 0;
        var retryFailedCount = 0;
        var retrySkippedCount = 0;
        var retryReasonCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var subtitle in translatedSubtitles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var originalPosition = subtitle.Position;
            var originalStartTime = subtitle.StartTime;
            var originalEndTime = subtitle.EndTime;
            var originalCueLineCount = subtitle.TranslatedLines.Count;

            for (var i = 0; i < subtitle.TranslatedLines.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var sourceLine = GetSourceLine(subtitle, i, stripSubtitleFormatting);
                var currentBest = subtitle.TranslatedLines[i] ?? string.Empty;
                var protectedTerms = BuildProtectedTerms(sourceLine, glossary, properNounLockEnabled, protectedPatterns);
                var currentAnalysis = subtitleQualityAnalyzer.Analyze(
                    currentBest,
                    sourceLine,
                    translationRequest.TargetLanguage,
                    protectedTerms);
                var currentReasons = currentAnalysis.Reasons;

                if (currentReasons.Count == 0)
                {
                    continue;
                }

                CountReasons(retryReasonCounts, currentReasons);

                if (currentAnalysis.Score < retryScoreThreshold)
                {
                    retrySkippedCount++;
                    if (logAttempts)
                    {
                        logger.LogInformation("Selective retry skipped. RequestId={RequestId}, Position={Position}, LineIndex={LineIndex}, Score={Score}, Threshold={Threshold}, Reasons={Reasons}, Outcome=skipped_below_threshold",
                            translationRequest.Id,
                            subtitle.Position,
                            i,
                            currentAnalysis.Score,
                            retryScoreThreshold,
                            string.Join(",", currentReasons));
                    }
                    continue;
                }

                for (var attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    retryAttemptedCount++;

                    try
                    {
                        var retryPrompt = BuildRetryPrompt(
                            sourceLine,
                            currentBest,
                            currentReasons,
                            translationRequest.TargetLanguage,
                            protectedTerms);
                        var retryRaw = await translator.TranslateSubtitleLine(new TranslateAbleSubtitleLine
                        {
                            SubtitleLine = retryPrompt,
                            SourceLanguage = translationRequest.SourceLanguage,
                            TargetLanguage = translationRequest.TargetLanguage
                        }, cancellationToken);

                        var retryCleaned = stripSubtitleFormatting
                            ? SubtitleFormatterService.RemoveMarkup(retryRaw)
                            : retryRaw.Trim();

                        var retryAnalysis = subtitleQualityAnalyzer.Analyze(
                            retryCleaned,
                            sourceLine,
                            translationRequest.TargetLanguage,
                            protectedTerms);
                        var retryReasonsDetected = retryAnalysis.Reasons;
                        var analysisBeforeRetry = currentAnalysis;
                        var reasonsBeforeRetry = currentReasons;

                        var protectedTermsSatisfied = ProtectedTermsSatisfied(retryCleaned, protectedTerms);
                        var improved = protectedTermsSatisfied && IsMeaningfullyImproved(
                            retryCleaned,
                            analysisBeforeRetry,
                            retryAnalysis,
                            improvementMargin);

                        if (improved)
                        {
                            currentBest = retryCleaned;
                            currentAnalysis = retryAnalysis;
                            currentReasons = retryReasonsDetected;
                            retryImprovedCount++;
                        }

                        if (logAttempts)
                        {
                            logger.LogInformation(
                                "Selective retry attempt completed. RequestId={RequestId}, Position={Position}, LineIndex={LineIndex}, StartTime={StartTime}, EndTime={EndTime}, Attempt={Attempt}, Improved={Improved}, Outcome={Outcome}, ScoreBefore={ScoreBefore}, ScoreAfter={ScoreAfter}, Margin={Margin}, ReasonsBefore={ReasonsBefore}, ReasonsAfter={ReasonsAfter}",
                                translationRequest.Id,
                                subtitle.Position,
                                i,
                                subtitle.StartTime,
                                subtitle.EndTime,
                                attempt,
                                improved,
                                improved ? "improved" : protectedTermsSatisfied ? "not_improved" : "protected_term_changed",
                                analysisBeforeRetry.Score,
                                retryAnalysis.Score,
                                improvementMargin,
                                string.Join(",", reasonsBeforeRetry),
                                string.Join(",", retryReasonsDetected));
                        }

                        if (currentAnalysis.Score < retryScoreThreshold)
                        {
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        retryFailedCount++;
                        logger.LogWarning(ex,
                            "Selective retry failed. RequestId={RequestId}, Position={Position}, LineIndex={LineIndex}, StartTime={StartTime}, EndTime={EndTime}, Attempt={Attempt}, Outcome=failed",
                            translationRequest.Id,
                            subtitle.Position,
                            i,
                            subtitle.StartTime,
                            subtitle.EndTime,
                            attempt);
                    }
                }

                subtitle.TranslatedLines[i] = currentBest;
            }

            if (subtitle.Position != originalPosition
                || subtitle.StartTime != originalStartTime
                || subtitle.EndTime != originalEndTime
                || subtitle.TranslatedLines.Count != originalCueLineCount)
            {
                throw new InvalidOperationException(
                    $"Selective retry modified subtitle metadata invariants. RequestId={translationRequest.Id}, Position={originalPosition}");
            }
        }

        logger.LogInformation(
            "Selective retry summary. RequestId={RequestId}, Attempted={Attempted}, Improved={Improved}, Failed={Failed}, Skipped={Skipped}",
            translationRequest.Id,
            retryAttemptedCount,
            retryImprovedCount,
            retryFailedCount,
            retrySkippedCount);

        await PersistRetrySummary(
            translationRequest.Id,
            retryAttemptedCount,
            retryImprovedCount,
            retryFailedCount,
            retrySkippedCount,
            retryReasonCounts,
            cancellationToken);

        return translatedSubtitles;
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

    private static bool IsMeaningfullyImproved(
        string retryCleaned,
        SubtitleQualityAnalysis currentAnalysis,
        SubtitleQualityAnalysis retryAnalysis,
        int improvementMargin)
    {
        if (string.IsNullOrWhiteSpace(retryCleaned))
        {
            return false;
        }

        return retryAnalysis.Score + improvementMargin <= currentAnalysis.Score;
    }

    private static void CountReasons(Dictionary<string, int> reasonCounts, IEnumerable<string> reasons)
    {
        foreach (var reason in reasons)
        {
            reasonCounts[reason] = reasonCounts.GetValueOrDefault(reason) + 1;
        }
    }

    private static Dictionary<string, string> BuildProtectedTerms(
        string sourceLine,
        IReadOnlyDictionary<string, string> glossary,
        bool properNounLockEnabled,
        IReadOnlyCollection<string> protectedPatterns)
    {
        var protectedTerms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var glossaryTerm in glossary)
        {
            if (ContainsWholeTerm(sourceLine, glossaryTerm.Key))
            {
                protectedTerms[glossaryTerm.Key] = glossaryTerm.Value;
            }
        }

        if (!properNounLockEnabled)
        {
            return protectedTerms;
        }

        foreach (var name in ExtractProperNouns(sourceLine))
        {
            protectedTerms.TryAdd(name, name);
        }

        foreach (var pattern in protectedPatterns)
        {
            MatchCollection matches;
            try
            {
                matches = Regex.Matches(sourceLine, pattern, RegexOptions.None, RegexTimeout);
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }

            foreach (Match match in matches)
            {
                var value = match.Value.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    protectedTerms.TryAdd(value, value);
                }
            }
        }

        return protectedTerms;
    }

    private static Dictionary<string, string> ParseGlossary(string rawGlossary, string sourceLanguage, string targetLanguage)
    {
        var glossary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(rawGlossary))
        {
            return glossary;
        }

        try
        {
            using var document = JsonDocument.Parse(rawGlossary);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return glossary;
            }

            var flatGlossaryDetected = false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    flatGlossaryDetected = true;
                    AddGlossaryTerm(glossary, property.Name, property.Value.GetString());
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.Object
                    || !LanguagePairMatches(property.Name, sourceLanguage, targetLanguage))
                {
                    continue;
                }

                foreach (var term in property.Value.EnumerateObject())
                {
                    if (term.Value.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    AddGlossaryTerm(glossary, term.Name, term.Value.GetString());
                }
            }

            if (flatGlossaryDetected)
            {
                return glossary;
            }
        }
        catch (JsonException)
        {
            return glossary;
        }

        return glossary;
    }

    private static void AddGlossaryTerm(Dictionary<string, string> glossary, string sourceTerm, string? targetTerm)
    {
        if (string.IsNullOrWhiteSpace(sourceTerm) || string.IsNullOrWhiteSpace(targetTerm))
        {
            return;
        }

        glossary[sourceTerm.Trim()] = targetTerm.Trim();
    }

    private static List<string> ParseProtectedPatterns(string rawPatterns)
    {
        if (string.IsNullOrWhiteSpace(rawPatterns))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(rawPatterns);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return document.RootElement
                .EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.String)
                .Select(element => element.GetString())
                .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
                .Select(pattern => pattern!.Trim())
                .Where(IsValidRegex)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool IsValidRegex(string pattern)
    {
        try
        {
            _ = Regex.Match(string.Empty, pattern, RegexOptions.None, RegexTimeout);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool LanguagePairMatches(string pairKey, string sourceLanguage, string targetLanguage)
    {
        var separator = pairKey.Contains("->", StringComparison.Ordinal) ? "->" : ":";
        var separatorIndex = pairKey.IndexOf(separator, StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex + separator.Length >= pairKey.Length)
        {
            return false;
        }

        var source = pairKey[..separatorIndex].Trim();
        var target = pairKey[(separatorIndex + separator.Length)..].Trim();

        return LanguageMatches(source, sourceLanguage) && LanguageMatches(target, targetLanguage);
    }

    private static bool LanguageMatches(string configured, string actual)
    {
        if (configured == "*")
        {
            return true;
        }

        var configuredNormalized = NormalizeLanguageCode(configured);
        var actualNormalized = NormalizeLanguageCode(actual);

        return configuredNormalized == actualNormalized
            || configuredNormalized == PrimaryLanguageSubtag(actualNormalized)
            || PrimaryLanguageSubtag(configuredNormalized) == actualNormalized;
    }

    private static string NormalizeLanguageCode(string language)
        => language.Trim().Replace('_', '-').ToLowerInvariant();

    private static string PrimaryLanguageSubtag(string language)
    {
        var separatorIndex = language.IndexOf('-');
        return separatorIndex >= 0 ? language[..separatorIndex] : language;
    }

    private static List<string> ExtractProperNouns(string sourceLine)
    {
        var names = new List<string>();
        foreach (Match match in Regex.Matches(sourceLine, @"\b[A-Z][a-z]+(?:\s+[A-Z][a-z]+)*\b", RegexOptions.None, RegexTimeout))
        {
            var value = match.Value.Trim();
            var hasWhitespace = value.Contains(' ');
            if (!hasWhitespace && match.Index == 0)
            {
                continue;
            }

            if (hasWhitespace && match.Index == 0 && NonNameLeadingWords.Contains(value))
            {
                continue;
            }

            names.Add(value);
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool ProtectedTermsSatisfied(string line, IReadOnlyDictionary<string, string> protectedTerms)
        => protectedTerms.Count == 0 || protectedTerms.All(term => ContainsWholeTerm(line, term.Value));

    private static bool ContainsWholeTerm(string text, string term)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(term))
        {
            return false;
        }

        var pattern = $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(term)}(?![\p{{L}}\p{{N}}_])";
        return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase, RegexTimeout);
    }

    private async Task PersistRetrySummary(
        int translationRequestId,
        int attemptedCount,
        int improvedCount,
        int failedCount,
        int skippedCount,
        Dictionary<string, int> reasonCounts,
        CancellationToken cancellationToken)
    {
        var request = await dbContext.TranslationRequests
            .FirstOrDefaultAsync(r => r.Id == translationRequestId, cancellationToken);

        if (request == null)
        {
            return;
        }

        request.SelectiveRetryAttemptedCount = attemptedCount;
        request.SelectiveRetryImprovedCount = improvedCount;
        request.SelectiveRetryFailedCount = failedCount;
        request.SelectiveRetrySkippedCount = skippedCount;
        request.SelectiveRetryReasonCountsJson = JsonSerializer.Serialize(reasonCounts);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

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

    private static string BuildRetryPrompt(
        string sourceText,
        string previousTranslation,
        List<string> reasons,
        string targetLanguage,
        IReadOnlyDictionary<string, string> protectedTerms)
    {
        var immutableTermsSection = protectedTerms.Count == 0
            ? string.Empty
            : $"""

IMMUTABLE_TERMS:
{string.Join(Environment.NewLine, protectedTerms.Select(term => $"- {term.Key} -> {term.Value}"))}

Every immutable term must appear exactly as specified in the corrected subtitle.
Do not translate, rename, transliterate, or omit immutable terms unless the mapping above explicitly says to.
""";

        return $"""
You are correcting a subtitle translation.
Target language: {targetLanguage}
Detected issues: {string.Join(", ", reasons)}
{immutableTermsSection}

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
