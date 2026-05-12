using Lingarr.Core.Configuration;
using Lingarr.Core.Entities;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Translation;
using Lingarr.Server.Models;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Lingarr.Server.Tests.Services;

public class SubtitleSelectiveRetryServiceTests
{
    [Fact]
    public async Task RetrySuspiciousLines_RetriesHighSeverity_AndImproves()
    {
        var settings = BuildSettings();
        var settingService = new Mock<ISettingService>();
        settingService.Setup(s => s.GetSettings(It.IsAny<IEnumerable<string>>())).ReturnsAsync(settings);

        var translationFactory = new Mock<ITranslationServiceFactory>();
        translationFactory.Setup(f => f.CreateTranslationService("openai")).Returns(new FakeTranslationService(_ => "Olá mundo"));

        var service = new SubtitleSelectiveRetryService(
            NullLogger<SubtitleSelectiveRetryService>.Instance,
            settingService.Object,
            translationFactory.Object,
            new SubtitleQualityAnalyzer());

        var subtitle = BuildSubtitle("TARGET LINE TO TRANSLATE olá", "hello world");
        var result = await service.RetrySuspiciousLines([subtitle], BuildRequest(), "openai", false, CancellationToken.None);

        Assert.Equal("Olá mundo", result[0].TranslatedLines[0]);
    }

    [Fact]
    public async Task RetrySuspiciousLines_DoesNotRetry_LowSeverityOnly()
    {
        var settings = BuildSettings();
        var settingService = new Mock<ISettingService>();
        settingService.Setup(s => s.GetSettings(It.IsAny<IEnumerable<string>>())).ReturnsAsync(settings);

        var translationFactory = new Mock<ITranslationServiceFactory>();
        var fake = new FakeTranslationService(_ => "changed");
        translationFactory.Setup(f => f.CreateTranslationService("openai")).Returns(fake);

        var service = new SubtitleSelectiveRetryService(
            NullLogger<SubtitleSelectiveRetryService>.Instance,
            settingService.Object,
            translationFactory.Object,
            new SubtitleQualityAnalyzer());

        var line = "I need to find my brother right now";
        var subtitle = BuildSubtitle(line, line);
        var result = await service.RetrySuspiciousLines([subtitle], BuildRequest(), "openai", false, CancellationToken.None);

        Assert.Equal(line, result[0].TranslatedLines[0]);
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public async Task RetrySuspiciousLines_RespectsMaxAttempts_AndFallback()
    {
        var settings = BuildSettings(maxAttempts: "1");
        var settingService = new Mock<ISettingService>();
        settingService.Setup(s => s.GetSettings(It.IsAny<IEnumerable<string>>())).ReturnsAsync(settings);

        var translationFactory = new Mock<ITranslationServiceFactory>();
        var fake = new FakeTranslationService(_ => "检查中...");
        translationFactory.Setup(f => f.CreateTranslationService("openai")).Returns(fake);

        var service = new SubtitleSelectiveRetryService(
            NullLogger<SubtitleSelectiveRetryService>.Instance,
            settingService.Object,
            translationFactory.Object,
            new SubtitleQualityAnalyzer());

        var subtitle = BuildSubtitle("检查中...", "checking");
        var result = await service.RetrySuspiciousLines([subtitle], BuildRequest(), "openai", false, CancellationToken.None);

        Assert.Equal("检查中...", result[0].TranslatedLines[0]);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task RetrySuspiciousLines_SkipsWhenProviderNotAllowed()
    {
        var settings = BuildSettings();
        var settingService = new Mock<ISettingService>();
        settingService.Setup(s => s.GetSettings(It.IsAny<IEnumerable<string>>())).ReturnsAsync(settings);

        var translationFactory = new Mock<ITranslationServiceFactory>();

        var service = new SubtitleSelectiveRetryService(
            NullLogger<SubtitleSelectiveRetryService>.Instance,
            settingService.Object,
            translationFactory.Object,
            new SubtitleQualityAnalyzer());

        var subtitle = BuildSubtitle("检查中...", "checking");
        await service.RetrySuspiciousLines([subtitle], BuildRequest(), "deepl", false, CancellationToken.None);

        translationFactory.Verify(f => f.CreateTranslationService(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RetrySuspiciousLines_ThrowsWhenCancellationRequested()
    {
        var settings = BuildSettings();
        var settingService = new Mock<ISettingService>();
        settingService.Setup(s => s.GetSettings(It.IsAny<IEnumerable<string>>())).ReturnsAsync(settings);

        var translationFactory = new Mock<ITranslationServiceFactory>();
        translationFactory.Setup(f => f.CreateTranslationService("openai")).Returns(new FakeTranslationService(_ => "Olá mundo"));

        var service = new SubtitleSelectiveRetryService(
            NullLogger<SubtitleSelectiveRetryService>.Instance,
            settingService.Object,
            translationFactory.Object,
            new SubtitleQualityAnalyzer());

        var subtitle = BuildSubtitle("TARGET LINE TO TRANSLATE olá", "hello world");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.RetrySuspiciousLines([subtitle], BuildRequest(), "openai", false, cts.Token));
    }

    [Fact]
    public async Task RetrySuspiciousLines_ContinuesAfterTransientException()
    {
        var settings = BuildSettings(maxAttempts: "2");
        var settingService = new Mock<ISettingService>();
        settingService.Setup(s => s.GetSettings(It.IsAny<IEnumerable<string>>())).ReturnsAsync(settings);

        var translationFactory = new Mock<ITranslationServiceFactory>();
        var calls = 0;
        var fake = new FakeTranslationService(_ =>
        {
            calls++;
            if (calls == 1)
            {
                throw new InvalidOperationException("transient");
            }

            return "Olá mundo";
        });
        translationFactory.Setup(f => f.CreateTranslationService("openai")).Returns(fake);

        var service = new SubtitleSelectiveRetryService(
            NullLogger<SubtitleSelectiveRetryService>.Instance,
            settingService.Object,
            translationFactory.Object,
            new SubtitleQualityAnalyzer());

        var subtitle = BuildSubtitle("TARGET LINE TO TRANSLATE olá", "hello world");
        var result = await service.RetrySuspiciousLines([subtitle], BuildRequest(), "openai", false, CancellationToken.None);

        Assert.Equal("Olá mundo", result[0].TranslatedLines[0]);
        Assert.Equal(2, fake.Calls);
    }

    [Fact]
    public async Task RetrySuspiciousLines_PreservesSubtitleMetadata()
    {
        var settings = BuildSettings();
        var settingService = new Mock<ISettingService>();
        settingService.Setup(s => s.GetSettings(It.IsAny<IEnumerable<string>>())).ReturnsAsync(settings);

        var translationFactory = new Mock<ITranslationServiceFactory>();
        translationFactory.Setup(f => f.CreateTranslationService("openai")).Returns(new FakeTranslationService(_ => "Olá mundo"));

        var service = new SubtitleSelectiveRetryService(
            NullLogger<SubtitleSelectiveRetryService>.Instance,
            settingService.Object,
            translationFactory.Object,
            new SubtitleQualityAnalyzer());

        var subtitle = BuildSubtitle("TARGET LINE TO TRANSLATE olá", "hello world");
        subtitle.Position = 42;
        subtitle.StartTime = 1_250;
        subtitle.EndTime = 3_000;

        var result = await service.RetrySuspiciousLines([subtitle], BuildRequest(), "openai", false, CancellationToken.None);
        var retried = result[0];

        Assert.Equal(42, retried.Position);
        Assert.Equal(1_250, retried.StartTime);
        Assert.Equal(3_000, retried.EndTime);
        Assert.Single(retried.TranslatedLines);
    }

    private static Dictionary<string, string> BuildSettings(string maxAttempts = "1") => new()
    {
        [SettingKeys.Translation.SelectiveRetryEnabled] = "true",
        [SettingKeys.Translation.SelectiveRetryMaxAttempts] = maxAttempts,
        [SettingKeys.Translation.SelectiveRetryHighSeverityOnly] = "true",
        [SettingKeys.Translation.SelectiveRetryProviderScope] = "llm_only",
        [SettingKeys.Translation.SelectiveRetryLogAttempts] = "true"
    };

    private static TranslationRequest BuildRequest() => new()
    {
        Id = 1,
        Title = "t",
        SourceLanguage = "en",
        TargetLanguage = "pt",
        MediaType = Lingarr.Core.Enum.MediaType.Movie,
        Status = Lingarr.Core.Enum.TranslationStatus.Pending
    };

    private static SubtitleItem BuildSubtitle(string translated, string source) => new()
    {
        Position = 1,
        Lines = [source],
        PlaintextLines = [source],
        TranslatedLines = [translated]
    };

    private sealed class FakeTranslationService(Func<string, string> resultFunc) : ITranslationService
    {
        public int Calls { get; private set; }
        public string? ModelName => "fake";

        public Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, List<string>? contextLinesBefore,
            List<string>? contextLinesAfter, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(resultFunc(text));
        }

        public Task<List<SourceLanguage>> GetLanguages() => Task.FromResult(new List<SourceLanguage>());
        public Task<ModelsResponse> GetModels() => Task.FromResult(new ModelsResponse());
    }
}
