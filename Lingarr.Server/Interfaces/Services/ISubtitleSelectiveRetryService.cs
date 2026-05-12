using Lingarr.Core.Entities;
using Lingarr.Server.Models.FileSystem;

namespace Lingarr.Server.Interfaces.Services;

public interface ISubtitleSelectiveRetryService
{
    Task<List<SubtitleItem>> RetrySuspiciousLines(
        List<SubtitleItem> translatedSubtitles,
        TranslationRequest translationRequest,
        string serviceType,
        bool stripSubtitleFormatting,
        CancellationToken cancellationToken = default);
}
