using Quartz;

namespace BackgroundJobsDemo.Scheduling;

/// <summary>
/// Logs every job's lifecycle — start, success, failure, veto. In
/// production this is where you'd push job metrics (duration, failures)
/// to your observability stack instead of just logging.
/// </summary>
public sealed class LoggingJobListener(ILogger<LoggingJobListener> logger) : IJobListener
{
    public string Name => "logging-listener";

    public Task JobToBeExecuted(IJobExecutionContext context, CancellationToken ct = default)
    {
        logger.LogInformation("Job {Job} starting (fire #{RefireCount})",
            context.JobDetail.Key, context.RefireCount);
        return Task.CompletedTask;
    }

    public Task JobExecutionVetoed(IJobExecutionContext context, CancellationToken ct = default)
    {
        logger.LogWarning("Job {Job} was vetoed by a trigger listener", context.JobDetail.Key);
        return Task.CompletedTask;
    }

    public Task JobWasExecuted(IJobExecutionContext context, JobExecutionException? exception,
        CancellationToken ct = default)
    {
        if (exception is null)
        {
            logger.LogInformation("Job {Job} finished in {Duration}ms",
                context.JobDetail.Key, context.JobRunTime.TotalMilliseconds);
        }
        else
        {
            logger.LogError(exception, "Job {Job} failed after {Duration}ms",
                context.JobDetail.Key, context.JobRunTime.TotalMilliseconds);
        }
        return Task.CompletedTask;
    }
}
