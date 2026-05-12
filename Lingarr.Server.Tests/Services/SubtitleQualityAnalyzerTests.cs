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
    public void Analyze_ReturnsWeightedScore()
    {
        var analysis = _analyzer.Analyze("TARGET LINE TO TRANSLATE: hello", "hello", "pt");

        Assert.True(analysis.Score >= 100);
        Assert.Contains(analysis.Issues, issue => issue is { Reason: "prompt_leakage", Weight: 100 });
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

    [Fact]
    public void Analyze_DoesNotFlagCjk_WhenTargetLanguageIsCjk()
    {
        var reasons = _analyzer.GetSuspiciousReasons("\u68c0\u67e5\u4e2d...", "checking", "zh");
        Assert.DoesNotContain("cjk", reasons);
    }

    [Fact]
    public void Analyze_DoesNotFlagCjk_WhenBilingualSourceCarriesCjkCue()
    {
        var reasons = _analyzer.GetSuspiciousReasons("ola \u4f60\u597d", "hello \u4f60\u597d", "pt");
        Assert.DoesNotContain("cjk", reasons);
    }

    [Fact]
    public void Analyze_DoesNotFlagEnglishLeftover_ForProtectedMixedLanguageTerms()
    {
        var reasons = _analyzer.GetSuspiciousReasons(
            "OpenAI GPT NASA Mars missao para teste",
            "OpenAI GPT NASA Mars mission",
            "pt");

        Assert.DoesNotContain("possible_english_leftover", reasons);
    }
}
