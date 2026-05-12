using Lingarr.Core.Entities;
using Lingarr.Server.Models.FileSystem;

namespace Lingarr.Server.Interfaces.Services;

public interface ISubtitleLlmReviewService
{
    Task<List<SubtitleItem>> ReviewLines(
        List<SubtitleItem> translatedSubtitles,
        TranslationRequest translationRequest,
        bool stripSubtitleFormatting,
        CancellationToken cancellationToken = default);
}
