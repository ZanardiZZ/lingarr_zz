using Lingarr.Core.Enum;

namespace Lingarr.Core.Entities;

public class TranslationRequest : BaseEntity
{
    public string? JobId  { get; set; }
    public int? MediaId  { get; set; }
    public required string Title { get; set; }
    public required string SourceLanguage { get; set; }
    public required string TargetLanguage { get; set; }
    public string? SubtitleToTranslate { get; set; }
    public string? TranslatedSubtitle { get; set; }
    public required MediaType MediaType { get; set; }
    public required TranslationStatus Status { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public string? StackTrace { get; set; }
    public int? SelectiveRetryAttemptedCount { get; set; }
    public int? SelectiveRetryImprovedCount { get; set; }
    public int? SelectiveRetryFailedCount { get; set; }
    public int? SelectiveRetrySkippedCount { get; set; }
    public string? SelectiveRetryReasonCountsJson { get; set; }
}
