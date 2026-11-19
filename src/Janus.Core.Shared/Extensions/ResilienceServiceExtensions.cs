using Polly;
using Polly.Retry;
using System;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Janus.Core.Shared.Models;
using Microsoft.Extensions.Configuration;


namespace Janus.Core.Shared.Extensions;

public static class ResilienceServiceExtensions
{
    public static IServiceCollection AddDataBaseResilience(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = configuration
            .GetSection(ResilienceOptions.SectionName)
            .Get<ResilienceOptions>() ?? new ResilienceOptions();

        services.AddResiliencePipeline(options.PipelineName, pipelineBuilder =>
        {
            pipelineBuilder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<Exception>(),
                MaxRetryAttempts = options.MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = options.UseJitter,
                Delay = TimeSpan.FromSeconds(options.DelaySeconds),
                OnRetry = args =>
                {
                    var currentActivity = Activity.Current;
                    if (currentActivity != null)
                    {
                        currentActivity.AddEvent(new ActivityEvent("TransientDbFault"));
                        currentActivity.SetTag("retry.attempt", args.AttemptNumber);
                        currentActivity.SetTag("retry.error", args.Outcome.Exception?.Message);
                    }

                    Console.WriteLine($"[RETRY WARNING] Attempt {args.AttemptNumber} failed. Retrying after {args.RetryDelay.TotalSeconds}s. Error: {args.Outcome.Exception?.Message}");
                    return default;
                }
            });
        });

        return services;
    }
}
