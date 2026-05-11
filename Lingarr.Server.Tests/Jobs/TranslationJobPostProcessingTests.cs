using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Translation;
using Lingarr.Server.Jobs;
using Lingarr.Server.Models;
using Lingarr.Server.Models.FileSystem;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Lingarr.Server.Tests.Jobs;

public class TranslationJobPostProcessingTests
{
    [Fact]
    public async Task Execute_AppliesPostProcessing_BeforeWritingSubtitles()
    {
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var dbContext = new LingarrDbContext(options);

        var translationRequest = new TranslationRequest
        {
            Id = 42,
            SubtitleToTranslate = "/tmp/source.en.srt",
            SourceLanguage = "en",
            TargetLanguage = "es",
            Status = TranslationStatus.Pending,
            Title = "Test"
        };
        dbContext.TranslationRequests.Add(translationRequest);
        await dbContext.SaveChangesAsync();

        var settings = new Dictionary<string, string>
        {
            [SettingKeys.Translation.ServiceType] = "fake",
            [SettingKeys.Translation.FixOverlappingSubtitles] = "false",
            [SettingKeys.Translation.StripSubtitleFormatting] = "false",
            [SettingKeys.Translation.AddTranslatorInfo] = "false",
            [SettingKeys.SubtitleValidation.ValidateSubtitles] = "false",
            [SettingKeys.SubtitleValidation.MaxFileSizeBytes] = "1048576",
            [SettingKeys.SubtitleValidation.MaxSubtitleLength] = "500",
            [SettingKeys.SubtitleValidation.MinSubtitleLength] = "2",
            [SettingKeys.SubtitleValidation.MinDurationMs] = "500",
            [SettingKeys.SubtitleValidation.MaxDurationSecs] = "10",
            [SettingKeys.Translation.AiContextPromptEnabled] = "false",
            [SettingKeys.Translation.AiContextBefore] = "0",
            [SettingKeys.Translation.AiContextAfter] = "0",
            [SettingKeys.Translation.UseBatchTranslation] = "false",
            [SettingKeys.Translation.MaxBatchSize] = "0",
            [SettingKeys.Translation.RemoveLanguageTag] = "true",
            [SettingKeys.Translation.UseSubtitleTagging] = "false",
            [SettingKeys.Translation.SubtitleTag] = ""
        };

        var settingService = new Mock<ISettingService>();
        settingService.Setup(s => s.GetSettings(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(settings);

        var subtitleService = new Mock<ISubtitleService>();
        subtitleService.Setup(s => s.ReadSubtitles(translationRequest.SubtitleToTranslate))
            .ReturnsAsync([
                new SubtitleItem
                {
                    Position = 1,
                    Lines = ["hello"],
                    PlaintextLines = ["hello"]
                }
            ]);
        subtitleService.Setup(s => s.CreateFilePath(translationRequest.SubtitleToTranslate, "", ""))
            .Returns("/tmp/output.es.srt");

        List<SubtitleItem>? writtenSubtitles = null;
        subtitleService.Setup(s => s.WriteSubtitles("/tmp/output.es.srt", It.IsAny<List<SubtitleItem>>(), false))
            .Callback<string, List<SubtitleItem>, bool>((_, subs, _) => writtenSubtitles = subs)
            .Returns(Task.CompletedTask);

        var translationFactory = new Mock<ITranslationServiceFactory>();
        translationFactory.Setup(f => f.CreateTranslationService("fake"))
            .Returns(new FakeTranslationService());

        var translationRequestService = new Mock<ITranslationRequestService>();
        translationRequestService.Setup(s => s.UpdateTranslationRequest(It.IsAny<TranslationRequest>(), It.IsAny<TranslationStatus>(), It.IsAny<string?>()))
            .ReturnsAsync((TranslationRequest _, TranslationStatus status, string? _) =>
            {
                translationRequest.Status = status;
                return translationRequest;
            });
        translationRequestService.Setup(s => s.UpdateActiveCount()).ReturnsAsync(0);
        translationRequestService.Setup(s => s.ClearMediaHash(It.IsAny<TranslationRequest>())).Returns(Task.CompletedTask);

        var postProcessor = new Mock<ISubtitlePostProcessingService>();
        postProcessor.Setup(p => p.Process(It.IsAny<List<SubtitleItem>>(), It.IsAny<TranslationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((List<SubtitleItem> subs, TranslationRequest _, CancellationToken _) =>
            {
                subs[0].TranslatedLines = ["post-processed"];
                return subs;
            });

        var job = new TranslationJob(
            NullLogger<TranslationJob>.Instance,
            settingService.Object,
            dbContext,
            Mock.Of<IProgressService>(),
            subtitleService.Object,
            Mock.Of<IScheduleService>(),
            Mock.Of<IStatisticsService>(),
            translationFactory.Object,
            translationRequestService.Object,
            Mock.Of<ITranslationRequestEventService>(),
            postProcessor.Object);

        await job.Execute(translationRequest, CancellationToken.None);

        postProcessor.Verify(p => p.Process(It.IsAny<List<SubtitleItem>>(), translationRequest, It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(writtenSubtitles);
        Assert.Equal("post-processed", writtenSubtitles![0].TranslatedLines![0]);
    }

    private sealed class FakeTranslationService : ITranslationService
    {
        public string? ModelName => "fake-model";

        public Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage,
            List<string>? contextLinesBefore, List<string>? contextLinesAfter, CancellationToken cancellationToken)
            => Task.FromResult("translated");

        public Task<List<SourceLanguage>> GetLanguages() => Task.FromResult(new List<SourceLanguage>());

        public Task<ModelsResponse> GetModels() => Task.FromResult(new ModelsResponse());
    }
}
