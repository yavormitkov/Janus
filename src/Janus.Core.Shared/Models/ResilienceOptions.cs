namespace Janus.Core.Shared.Models;

public class ResilienceOptions
{
    public const string SectionName = "Resilience";

    public string PipelineName { get; set; } = "DBRetryPipeline";
    public int MaxRetryAttempts { get; set; } = 3;
    public int DelaySeconds { get; set; } = 2;
    public bool UseJitter { get; set; } = true;
}
