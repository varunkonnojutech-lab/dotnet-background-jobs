using BackgroundJobsDemo.Services;
using Quartz;

namespace BackgroundJobsDemo.Jobs;

/// <summary>
/// Builds the daily sales report. Idempotent: re-running it for the same
/// date overwrites the same report instead of double-counting, so a
/// misfire or a manual re-trigger is safe.
/// </summary>
[DisallowConcurrentExecution] // Never run two report builds at once.
public sealed class DailyReportJob(OrderStore orders, ReportStore reports, ILogger<DailyReportJob> logger)
    : IJob
{
    public Task Execute(IJobExecutionContext context)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var (count, revenue) = orders.TodaysCompletedTotals(today);
        var expired = orders.All.Count(o => o.Status == "Expired"
            && DateOnly.FromDateTime(o.CreatedAt.DateTime) == today);

        var report = new DailyReport(today, count, revenue, expired);
        reports.Upsert(report);
        logger.LogInformation("Daily report for {Date}: {Count} orders, {Revenue:C} revenue, {Expired} expired",
            today, count, revenue, expired);
        return Task.CompletedTask;
    }
}

/// <summary>Sends the morning digest to subscribers (recorded, not really sent).</summary>
[DisallowConcurrentExecution]
public sealed class EmailDigestJob(ReportStore reports, IEmailSender sender, ILogger<EmailDigestJob> logger)
    : IJob
{
    // In a real app these come from a subscribers table.
    private static readonly string[] Subscribers = ["ops@example.com", "sales@example.com"];

    public async Task Execute(IJobExecutionContext context)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var latest = reports.All.LastOrDefault();
        var body = latest is null
            ? $"No report yet for {today}. The daily report job runs at 06:00."
            : $"Daily digest for {today}: {latest.OrderCount} orders, {latest.TotalRevenue:C} revenue.";

        // The context is null in unit tests that call Execute directly;
        // in production Quartz always supplies one.
        var ct = context?.CancellationToken ?? CancellationToken.None;
        foreach (var subscriber in Subscribers)
        {
            await sender.SendAsync(subscriber, $"Morning digest — {today}", body, ct);
        }
        logger.LogInformation("Digest sent to {Count} subscribers", Subscribers.Length);
    }
}

/// <summary>Expires pending orders older than 30 minutes — the classic
/// "abandoned cart" cleanup every shop needs.</summary>
public sealed class StaleOrderCleanupJob(OrderStore orders, ILogger<StaleOrderCleanupJob> logger) : IJob
{
    public Task Execute(IJobExecutionContext context)
    {
        var expired = orders.ExpireStaleOrders(TimeSpan.FromMinutes(30));
        logger.LogInformation("Stale-order cleanup expired {Count} orders", expired);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Demo job that fails its first two executions on purpose, then succeeds.
/// It shows the two things every background job needs: a retry policy and
/// a counter so a job knows which attempt it is on.
/// <para>
/// Quartz retries a job that throws <see cref="JobExecutionException"/>
/// with <c>RefireImmediately = true</c>. After the configured attempts the
/// failure is final — in production, wire that to an alert, not silence.
/// </para>
/// <para>
/// The attempt counter lives in the injected <see cref="FlakyDemoState"/>
/// service, not in the JobDataMap — on purpose. A counter stored in the
/// JobDataMap resets on every refire (Quartz re-reads the stored job
/// detail), which we watched spin this job in a tight retry loop forever.
/// </para>
/// </summary>
public sealed class FlakyJob(FlakyDemoState state, ILogger<FlakyJob> logger) : IJob
{
    public Task Execute(IJobExecutionContext context)
    {
        var attempt = state.NextAttempt();
        logger.LogInformation("FlakyJob attempt {Attempt}", attempt);

        if (attempt <= 2)
        {
            // Ask Quartz to fire this job again right away.
            throw new JobExecutionException($"Simulated failure on attempt {attempt}")
            {
                RefireImmediately = true,
            };
        }

        logger.LogInformation("FlakyJob succeeded on attempt {Attempt}", attempt);
        return Task.CompletedTask;
    }
}
