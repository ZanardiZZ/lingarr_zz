using Lingarr.Server.Models;

namespace Lingarr.Server.Interfaces.Services;

public interface ISubtitleQualityAnalyzer
{
    SubtitleQualityAnalysis Analyze(
        string translatedLine,
        string sourceLine,
        string? targetLanguage,
        IReadOnlyDictionary<string, string>? protectedTerms = null);

    List<string> GetSuspiciousReasons(
        string translatedLine,
        string sourceLine,
        string? targetLanguage,
        IReadOnlyDictionary<string, string>? protectedTerms = null);
}
