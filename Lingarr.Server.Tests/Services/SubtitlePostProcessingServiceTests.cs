using Lingarr.Core.Entities;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lingarr.Server.Tests.Services;

public class SubtitlePostProcessingServiceTests
{
    private readonly SubtitlePostProcessingService _service =
        new(NullLogger<SubtitlePostProcessingService>.Instance);

    [Theory]
    [InlineData("Translation in Portuguese: Olá", "Olá")]
    [InlineData("Translation: Olá", "Olá")]
    [InlineData("Tradução: Olá", "Olá")]
    [InlineData("Here is the translation: Olá", "Olá")]
    public async Task Process_RemovesLeadingAssistantLabels(string input, string expected)
    {
        var result = await ProcessSingleLine(input, "hello");
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task Process_RemovesPromptLeakageMarkers()
    {
        var result = await ProcessSingleLine("TARGET LINE Olá CONTEXT BEFORE teste SUBTITLE_DATA_START", "hello");
        Assert.Equal("Olá teste", result);
    }

    [Theory]
    [InlineData("Olá. Note: extra guidance", "Olá.")]
    [InlineData("Olá. Nota: observação", "Olá.")]
    public async Task Process_RemovesTrailingNotes(string input, string expected)
    {
        var result = await ProcessSingleLine(input, "hello");
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task Process_UnwrapsQuotes_WhenSourceIsNotQuoted()
    {
        var result = await ProcessSingleLine("\"Olá\"", "hello");
        Assert.Equal("Olá", result);
    }

    [Fact]
    public async Task Process_DoesNotUnwrapQuotes_WhenSourceIsQuoted()
    {
        var result = await ProcessSingleLine("\"Olá\"", "\"hello\"");
        Assert.Equal("\"Olá\"", result);
    }

    [Fact]
    public async Task Process_NormalizesSpacingAndPunctuationSpacing()
    {
        var result = await ProcessSingleLine("Olá   ,   mundo  !", "hello");
        Assert.Equal("Olá, mundo!", result);
    }

    [Fact]
    public async Task Process_LogsSuspicious_WhenLineContainsCjkAfterCleaning()
    {
        var logger = new TestLogger<SubtitlePostProcessingService>();
        var service = new SubtitlePostProcessingService(logger);

        await ProcessSingleLine("你好 mundo", "hello", service);

        Assert.Contains(logger.Logs, x => x.Contains("Reasons=cjk"));
    }

    [Fact]
    public async Task Process_LogsSuspicious_WhenPromptLeakageRemains()
    {
        var logger = new TestLogger<SubtitlePostProcessingService>();
        var service = new SubtitlePostProcessingService(logger);

        await ProcessSingleLine("Use CONTEXT_BEFORE token", "hello", service);

        Assert.Contains(logger.Logs, x => x.Contains("prompt_leakage"));
    }

    [Fact]
    public async Task Process_LogsSuspicious_WhenAssistantLabelRemains()
    {
        var logger = new TestLogger<SubtitlePostProcessingService>();
        var service = new SubtitlePostProcessingService(logger);

        await ProcessSingleLine("This contains Translation in Portuguese label", "hello", service);

        Assert.Contains(logger.Logs, x => x.Contains("assistant_label"));
    }

    [Fact]
    public async Task Process_LogsSuspicious_WhenRepeatedSegmentDetected()
    {
        var logger = new TestLogger<SubtitlePostProcessingService>();
        var service = new SubtitlePostProcessingService(logger);

        await ProcessSingleLine("we go now we go now", "go");

        Assert.Contains(logger.Logs, x => x.Contains("repeated_segment"));
    }

    [Fact]
    public async Task Process_LogsSuspicious_WhenExcessiveLengthComparedToSource()
    {
        var logger = new TestLogger<SubtitlePostProcessingService>();
        var service = new SubtitlePostProcessingService(logger);
        var longLine = new string('a', 121);

        await ProcessSingleLine(longLine, "short", service);

        Assert.Contains(logger.Logs, x => x.Contains("excessive_length"));
    }

    [Fact]
    public async Task Process_LogsSuspicious_WhenEnglishLeftoversDetected()
    {
        var logger = new TestLogger<SubtitlePostProcessingService>();
        var service = new SubtitlePostProcessingService(logger);

        await ProcessSingleLine("I need to find my brother right now", "I need to find my brother right now", service);

        Assert.Contains(logger.Logs, x => x.Contains("possible_english_leftover"));
    }

    [Fact]
    public async Task Process_DoesNotLogEnglishLeftover_WhenTargetLanguageIsEnglish()
    {
        var logger = new TestLogger<SubtitlePostProcessingService>();
        var service = new SubtitlePostProcessingService(logger);

        await ProcessSingleLine(
            "I need to find my brother right now",
            "I need to find my brother right now",
            service,
            sourceLanguage: "pt",
            targetLanguage: "en");

        Assert.DoesNotContain(logger.Logs, x => x.Contains("possible_english_leftover"));
    }

    [Fact]
    public async Task Process_LogsSuspicious_WhenParentheticalCueMissing()
    {
        var logger = new TestLogger<SubtitlePostProcessingService>();
        var service = new SubtitlePostProcessingService(logger);

        await ProcessSingleLine("Ele chegou", "(whispering) He arrived", service);

        Assert.Contains(logger.Logs, x => x.Contains("missing_parenthetical_cue"));
    }

    [Fact]
    public async Task Process_LogsSuspicious_WhenSpeakerLabelAdded()
    {
        var logger = new TestLogger<SubtitlePostProcessingService>();
        var service = new SubtitlePostProcessingService(logger);

        await ProcessSingleLine("JOHN: Vamos.", "Let's go.", service);

        Assert.Contains(logger.Logs, x => x.Contains("added_speaker_label"));
    }

    [Fact]
    public async Task Process_LogsSuspicious_WhenProperNounLikelyChanged()
    {
        var logger = new TestLogger<SubtitlePostProcessingService>();
        var service = new SubtitlePostProcessingService(logger);

        await ProcessSingleLine("Marta chegou agora.", "Maria arrived now.", service);

        Assert.Contains(logger.Logs, x => x.Contains("changed_proper_noun"));
    }

    [Fact]
    public async Task Process_DoesNotLogChangedProperNoun_ForSentenceStartCapitalizationOnly()
    {
        var logger = new TestLogger<SubtitlePostProcessingService>();
        var service = new SubtitlePostProcessingService(logger);

        await ProcessSingleLine("Ele corre rápido.", "The boy runs fast.", service);

        Assert.DoesNotContain(logger.Logs, x => x.Contains("changed_proper_noun"));
    }

    [Fact]
    public async Task Process_LogsSuspicious_WhenBracketCueMissing()
    {
        var logger = new TestLogger<SubtitlePostProcessingService>();
        var service = new SubtitlePostProcessingService(logger);

        await ProcessSingleLine("Ela chegou.", "[whispering] She arrived.", service);

        Assert.Contains(logger.Logs, x => x.Contains("missing_parenthetical_cue"));
    }

    [Fact]
    public async Task Process_CleansRealWorldExample_AssistantLabelAndTrailingNote()
    {
        var result = await ProcessSingleLine(
            "Translation in Portuguese: Você poderia me trazer outra limonada? Note: This is a machine translation.",
            "Could you bring me another lemonade?");

        Assert.Equal("Você poderia me trazer outra limonada?", result);
    }

    [Fact]
    public async Task Process_CleansRealWorldExample_SubtitleDataMarkers()
    {
        var result = await ProcessSingleLine("SUBTITLE_DATA_START Pronto? SUBTITLE_DATA_END", "Ready?");
        Assert.Equal("Pronto?", result);
    }

    [Fact]
    public async Task Process_CleansRealWorldExample_TraducaoPrefix()
    {
        var result = await ProcessSingleLine("Tradução: A casa de Stevie tem ar condicionado.", "Stevie's house has air conditioning.");
        Assert.Equal("A casa de Stevie tem ar condicionado.", result);
    }

    [Fact]
    public async Task Process_CleansRealWorldExample_HereIsTheTranslationPrefix()
    {
        var result = await ProcessSingleLine("Here is the translation: Vamos pegar mais.", "Let's get more.");
        Assert.Equal("Vamos pegar mais.", result);
    }

    [Fact]
    public async Task Process_FlagsRealWorldExample_CjkLineAsSuspicious()
    {
        var logger = new TestLogger<SubtitlePostProcessingService>();
        var service = new SubtitlePostProcessingService(logger);

        await ProcessSingleLine("检查中...", "Checking...", service);

        Assert.Contains(logger.Logs, x => x.Contains("Reasons=cjk"));
    }

    [Fact]
    public async Task Process_FlagsRealWorldExample_TargetLineToTranslateRepetitionAsSuspicious()
    {
        var logger = new TestLogger<SubtitlePostProcessingService>();
        var service = new SubtitlePostProcessingService(logger);

        await ProcessSingleLine(
            "A TARGET LINE TO TRANSLATE: texto A TARGET LINE TO TRANSLATE: texto A TARGET LINE TO TRANSLATE: texto",
            "some source",
            service);

        Assert.Contains(logger.Logs, x => x.Contains("prompt_leakage") || x.Contains("repeated_segment"));
    }

    [Fact]
    public async Task Process_CleansRealWorldExample_UnwrapsQuotesWhenSourceIsNotQuoted()
    {
        var result = await ProcessSingleLine(
            "\"Pelo menos terei um lugar para treinar meus saltos do penhasco.\"",
            "at least I'll have a place to work on my cliff diving.");

        Assert.Equal("Pelo menos terei um lugar para treinar meus saltos do penhasco.", result);
    }

    private async Task<string> ProcessSingleLine(
        string translatedLine,
        string sourceLine,
        SubtitlePostProcessingService? service = null,
        string sourceLanguage = "en",
        string targetLanguage = "pt")
    {
        var subtitle = new SubtitleItem
        {
            Position = 1,
            TranslatedLines = [translatedLine],
            PlaintextLines = [sourceLine],
            Lines = [sourceLine]
        };

        var request = new TranslationRequest
        {
            Id = 123,
            Title = "test",
            SourceLanguage = sourceLanguage,
            TargetLanguage = targetLanguage,
            MediaType = Lingarr.Core.Enum.MediaType.Movie,
            Status = Lingarr.Core.Enum.TranslationStatus.Pending
        };
        var processor = service ?? _service;
        var result = await processor.Process([subtitle], request, CancellationToken.None);

        return result[0].TranslatedLines[0];
    }

    private sealed class TestLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<string> Logs { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Logs.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
