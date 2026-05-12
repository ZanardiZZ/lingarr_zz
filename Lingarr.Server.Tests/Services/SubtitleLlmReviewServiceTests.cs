using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Translation;
using Lingarr.Server.Models;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text.Json;

namespace Lingarr.Server.Tests.Services;

public class SubtitleLlmReviewServiceTests
{
    [Fact]
    public async Task ReviewLines_ReviewsEverySuspiciousLine_EvenLowScore()
    {
        var fake = new FakeTranslationService(_ => "Eu preciso encontrar meu irmao agora");
        var service = BuildService(fake, BuildDbContext(), BuildSettings(samplePercent: "0"));

        var original = "I need to find my brother right now";
        var subtitle = BuildSubtitle(1, original, original);
        var result = await service.ReviewLines([subtitle], BuildRequest(), false, CancellationToken.None);

        Assert.Equal("Eu preciso encontrar meu irmao agora", result[0].TranslatedLines[0]);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task ReviewLines_SamplesGoodLinesByLongestTranslation()
    {
        var fake = new FakeTranslationService(_ => "reviewed");
        var service = BuildService(fake, BuildDbContext(), BuildSettings(samplePercent: "10"));
        var subtitles = Enumerable.Range(1, 20)
            .Select(position => BuildSubtitle(
                position,
                $"source {position} " + string.Join(' ', Enumerable.Range(1, position).Select(i => $"word{i}")),
                $"linha traduzida numero {position} " + string.Join(' ', Enumerable.Range(1, position).Select(i => $"palavra{i}"))))
            .ToList();

        await service.ReviewLines(subtitles, BuildRequest(), false, CancellationToken.None);

        Assert.Equal(2, fake.Calls);
        Assert.Contains(fake.Inputs, input => input.Contains("linha traduzida numero 20"));
        Assert.Contains(fake.Inputs, input => input.Contains("linha traduzida numero 19"));
    }

    [Fact]
    public async Task ReviewLines_DoesNotDuplicateSuspiciousLinesInSampling()
    {
        var fake = new FakeTranslationService(_ => "reviewed");
        var service = BuildService(fake, BuildDbContext(), BuildSettings(samplePercent: "100"));
        var subtitles = new List<SubtitleItem>
        {
            BuildSubtitle(1, "hello world", "TARGET LINE TO TRANSLATE hello"),
            BuildSubtitle(2, "short source", "Boa linha")
        };

        await service.ReviewLines(subtitles, BuildRequest(), false, CancellationToken.None);

        Assert.Equal(2, fake.Calls);
    }

    [Fact]
    public async Task ReviewLines_AcceptsValidResponseEvenWhenStillSuspicious()
    {
        var fake = new FakeTranslationService(_ => "TARGET LINE TO TRANSLATE worse");
        var service = BuildService(fake, BuildDbContext(), BuildSettings(samplePercent: "0"));
        var subtitle = BuildSubtitle(1, "hello world", "TARGET LINE TO TRANSLATE hello");

        var result = await service.ReviewLines([subtitle], BuildRequest(), false, CancellationToken.None);

        Assert.Equal("TARGET LINE TO TRANSLATE worse", result[0].TranslatedLines[0]);
    }

    [Fact]
    public async Task ReviewLines_KeepsOriginalWhenResponseEmptyOrException()
    {
        var calls = 0;
        var fake = new FakeTranslationService(_ =>
        {
            calls++;
            if (calls == 1)
            {
                return "   ";
            }

            throw new InvalidOperationException("transient");
        });
        var service = BuildService(fake, BuildDbContext(), BuildSettings(samplePercent: "0"));
        var subtitles = new List<SubtitleItem>
        {
            BuildSubtitle(1, "hello", "TARGET LINE TO TRANSLATE hello"),
            BuildSubtitle(2, "checking", "\u68c0\u67e5\u4e2d...")
        };

        var result = await service.ReviewLines(subtitles, BuildRequest(), false, CancellationToken.None);

        Assert.Equal("TARGET LINE TO TRANSLATE hello", result[0].TranslatedLines[0]);
        Assert.Equal("\u68c0\u67e5\u4e2d...", result[1].TranslatedLines[0]);
        Assert.Equal(2, fake.Calls);
    }

    [Fact]
    public async Task ReviewLines_PreservesSubtitleMetadata()
    {
        var fake = new FakeTranslationService(_ => "Ola mundo");
        var service = BuildService(fake, BuildDbContext(), BuildSettings(samplePercent: "0"));
        var subtitle = BuildSubtitle(42, "hello world", "TARGET LINE TO TRANSLATE hello");
        subtitle.StartTime = 1_250;
        subtitle.EndTime = 3_000;

        var result = await service.ReviewLines([subtitle], BuildRequest(), false, CancellationToken.None);

        Assert.Equal(42, result[0].Position);
        Assert.Equal(1_250, result[0].StartTime);
        Assert.Equal(3_000, result[0].EndTime);
        Assert.Single(result[0].TranslatedLines);
    }

    [Fact]
    public async Task ReviewLines_PersistsSummary()
    {
        var fake = new FakeTranslationService(_ => "Ola mundo");
        var dbContext = BuildDbContext();
        var request = BuildRequest();
        dbContext.TranslationRequests.Add(request);
        await dbContext.SaveChangesAsync();
        var service = BuildService(fake, dbContext, BuildSettings(samplePercent: "0"));

        await service.ReviewLines(
            [BuildSubtitle(1, "hello world", "TARGET LINE TO TRANSLATE hello")],
            request,
            false,
            CancellationToken.None);

        var savedRequest = await dbContext.TranslationRequests.SingleAsync();
        var reasonCounts = JsonSerializer.Deserialize<Dictionary<string, int>>(
            savedRequest.LlmReviewReasonCountsJson ?? "{}");

        Assert.Equal(1, savedRequest.LlmReviewReviewedCount.GetValueOrDefault());
        Assert.Equal(1, savedRequest.LlmReviewChangedCount.GetValueOrDefault());
        Assert.Equal(0, savedRequest.LlmReviewFailedCount.GetValueOrDefault());
        Assert.Equal(1, savedRequest.LlmReviewSuspiciousReviewedCount.GetValueOrDefault());
        Assert.Equal(0, savedRequest.LlmReviewSampledReviewedCount.GetValueOrDefault());
        Assert.Equal("openai", savedRequest.LlmReviewProvider);
        Assert.NotNull(reasonCounts);
        Assert.Equal(1, reasonCounts["prompt_leakage"]);
    }

    private static SubtitleLlmReviewService BuildService(
        FakeTranslationService fake,
        LingarrDbContext dbContext,
        Dictionary<string, string> settings)
    {
        var settingService = new Mock<ISettingService>();
        settingService.Setup(s => s.GetSettings(It.IsAny<IEnumerable<string>>())).ReturnsAsync(settings);

        var translationFactory = new Mock<ITranslationServiceFactory>();
        translationFactory.Setup(f => f.CreateTranslationService("openai")).Returns(fake);

        return new SubtitleLlmReviewService(
            NullLogger<SubtitleLlmReviewService>.Instance,
            dbContext,
            settingService.Object,
            translationFactory.Object,
            new SubtitleQualityAnalyzer());
    }

    private static Dictionary<string, string> BuildSettings(
        string enabled = "true",
        string provider = "openai",
        string samplePercent = "10") => new()
    {
        [SettingKeys.Translation.LlmReviewerEnabled] = enabled,
        [SettingKeys.Translation.LlmReviewerProvider] = provider,
        [SettingKeys.Translation.LlmReviewerSamplePercent] = samplePercent,
        [SettingKeys.Translation.LlmReviewerLogAttempts] = "true"
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

    private static LingarrDbContext BuildDbContext()
    {
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new LingarrDbContext(options);
    }

    private static SubtitleItem BuildSubtitle(int position, string source, string translated) => new()
    {
        Position = position,
        Lines = [source],
        PlaintextLines = [source],
        TranslatedLines = [translated]
    };

    private sealed class FakeTranslationService(Func<string, string> resultFunc) : ITranslationService
    {
        public int Calls { get; private set; }
        public List<string> Inputs { get; } = [];
        public string? ModelName => "fake";

        public Task<string> TranslateAsync(
            string text,
            string sourceLanguage,
            string targetLanguage,
            List<string>? contextLinesBefore,
            List<string>? contextLinesAfter,
            CancellationToken cancellationToken)
        {
            Calls++;
            Inputs.Add(text);
            return Task.FromResult(resultFunc(text));
        }

        public Task<List<SourceLanguage>> GetLanguages() => Task.FromResult(new List<SourceLanguage>());
        public Task<ModelsResponse> GetModels() => Task.FromResult(new ModelsResponse());
    }
}
