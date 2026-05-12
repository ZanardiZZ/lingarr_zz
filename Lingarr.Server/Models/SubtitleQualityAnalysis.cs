namespace Lingarr.Server.Models;

public sealed record SubtitleQualityIssue(string Reason, int Weight);

public sealed class SubtitleQualityAnalysis
{
    public SubtitleQualityAnalysis(IEnumerable<SubtitleQualityIssue> issues)
    {
        Issues = issues.ToList();
        Score = Issues.Sum(issue => issue.Weight);
    }

    public List<SubtitleQualityIssue> Issues { get; }
    public int Score { get; }
    public List<string> Reasons => Issues.Select(issue => issue.Reason).ToList();
}
