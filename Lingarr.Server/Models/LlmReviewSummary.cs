namespace Lingarr.Server.Models;

public class LlmReviewSummary
{
    public int Reviewed { get; set; }
    public int Changed { get; set; }
    public int Failed { get; set; }
    public int SuspiciousReviewed { get; set; }
    public int SampledReviewed { get; set; }
    public string? Provider { get; set; }
    public Dictionary<string, int> ReasonDistribution { get; set; } = [];
}
