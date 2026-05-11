using Lingarr.Core.Entities;
using Lingarr.Server.Models.FileSystem;

namespace Lingarr.Server.Interfaces.Services;

public interface ISubtitlePostProcessingService
{
    Task<List<SubtitleItem>> Process(
        List<SubtitleItem> translatedSubtitles,
        TranslationRequest translationRequest,
        CancellationToken cancellationToken = default);
}
