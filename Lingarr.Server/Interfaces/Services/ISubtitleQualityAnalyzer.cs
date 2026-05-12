namespace Lingarr.Server.Interfaces.Services;

public interface ISubtitleQualityAnalyzer
{
    List<string> GetSuspiciousReasons(string translatedLine, string sourceLine, string? targetLanguage);
}
