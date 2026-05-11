using Lingarr.Core.Entities;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models.FileSystem;

namespace Lingarr.Server.Services;

public class SubtitlePostProcessingService : ISubtitlePostProcessingService
{
    public Task<List<SubtitleItem>> Process(
        List<SubtitleItem> translatedSubtitles,
        TranslationRequest translationRequest,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(translatedSubtitles);
    }
}
