namespace Lingarr.Server.Models;

public class SelectiveRetrySummary
{
    public int Attempted { get; set; }
    public int Improved { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public Dictionary<string, int> ReasonDistribution { get; set; } = [];
}
