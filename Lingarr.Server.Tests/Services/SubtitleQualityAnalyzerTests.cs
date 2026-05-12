using Lingarr.Server.Services;

namespace Lingarr.Server.Tests.Services;

public class SubtitleQualityAnalyzerTests
{
    private readonly SubtitleQualityAnalyzer _analyzer = new();

    [Fact]
    public void Analyze_FlagsPromptLeakageMarkers()
    {
        var reasons = _analyzer.GetSuspiciousReasons("TARGET LINE TO TRANSLATE: oi CONTEXT", "hello", "pt");
        Assert.Contains("prompt_leakage", reasons);
    }

    [Fact]
    public void Analyze_FlagsCjk()
    {
        var reasons = _analyzer.GetSuspiciousReasons("检查中...", "checking", "pt");
        Assert.Contains("cjk", reasons);
    }

    [Fact]
    public void Analyze_FlagsRepeatedSegment()
    {
        var reasons = _analyzer.GetSuspiciousReasons("TARGET LINE TO TRANSLATE TARGET LINE TO TRANSLATE", "x", "pt");
        Assert.Contains("repeated_segment", reasons);
    }

    [Fact]
    public void Analyze_FlagsExcessiveLength()
    {
        var reasons = _analyzer.GetSuspiciousReasons(new string('a', 121), "short", "pt");
        Assert.Contains("excessive_length", reasons);
    }

    [Fact]
    public void Analyze_DoesNotFlagEnglishLeftover_WhenTargetLanguageIsEnglish()
    {
        var reasons = _analyzer.GetSuspiciousReasons("I need to find my brother", "I need to find my brother", "en");
        Assert.DoesNotContain("possible_english_leftover", reasons);
    }
}
