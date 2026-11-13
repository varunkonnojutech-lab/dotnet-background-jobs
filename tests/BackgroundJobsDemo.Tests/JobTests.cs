using BackgroundJobsDemo.Jobs;
using BackgroundJobsDemo.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;

namespace BackgroundJobsDemo.Tests;

/// <summary>Job logic, tested without HTTP — jobs are plain classes.</summary>
public sealed class JobTests
{
    private static OrderStore SeededStore() => new();

    [Fact]
    public async Task DailyReportJob_BuildsReportFromCompletedOrders()
    {
        var orders = SeededStore();
        var reports = new ReportStore();
        var job = new DailyReportJob(orders, reports, NullLogger<DailyReportJob>.Instance);

        await job.Execute(null!); // Job logic never touches the context.

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.True(reports.HasReportFor(today));
        var report = reports.All.Single(r => r.Date == today);
        Assert.Equal(2, report.OrderCount);          // the two seeded completed orders
        Assert.Equal(249.99m + 89.50m, report.TotalRevenue);
    }

    [Fact]
    public async Task DailyReportJob_IsIdempotent_RunningTwiceDoesNotDoubleCount()
    {
        var orders = SeededStore();
        var reports = new ReportStore();
        var job = new DailyReportJob(orders, reports, NullLogger<DailyReportJob>.Instance);

        await job.Execute(null!);
        await job.Execute(null!);

        Assert.Single(reports.All); // one report per date, overwritten not duplicated
        Assert.Equal(2, reports.All.Single().OrderCount);
    }

    [Fact]
    public async Task StaleOrderCleanupJob_ExpiresOnlyStalePendingOrders()
    {
        var orders = SeededStore();
        var job = new StaleOrderCleanupJob(orders, NullLogger<StaleOrderCleanupJob>.Instance);

        await job.Execute(null!);

        var all = orders.All;
        Assert.Equal(2, all.Count(o => o.Status == "Completed")); // untouched
        Assert.Single(all.Where(o => o.Status == "Expired"));     // the 2-hour-old pending order
        Assert.DoesNotContain(all, o => o.Status == "Pending");
    }

    [Fact]
    public async Task EmailDigestJob_SendsOneEmailPerSubscriber()
    {
        var sender = new InMemoryEmailSender();
        var job = new EmailDigestJob(new ReportStore(), sender, NullLogger<EmailDigestJob>.Instance);

        await job.Execute(null!);

        Assert.Equal(2, sender.Sent.Count);
        Assert.All(sender.Sent, mail => Assert.Contains("digest", mail.Subject, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FlakyJob_FailsTwiceThenSucceeds_OnARealScheduler()
    {
        // The retry behaviour only exists inside Quartz, so test it there:
        // a real in-memory scheduler, a real trigger, and the real
        // refire-on-failure path. The scheduler is built through
        // Microsoft DI so Quartz can construct the job (it needs a
        // FlakyDemoState and an ILogger<FlakyJob>).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<FlakyDemoState>();
        services.AddQuartz();
        await using var provider = services.BuildServiceProvider();
        var state = provider.GetRequiredService<FlakyDemoState>();
        var scheduler = await provider.GetRequiredService<ISchedulerFactory>().GetScheduler();
        await scheduler.Start();
        try
        {
            await scheduler.ScheduleJob(
                JobBuilder.Create<FlakyJob>().WithIdentity("flaky-test").Build(),
                TriggerBuilder.Create().WithIdentity("flaky-test-trigger").StartNow().Build());

            // Poll until the job has succeeded (attempt counter reaches 3)
            // or the test times out.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (state.Attempts >= 3) return;
                await Task.Delay(200);
            }
            Assert.Fail("FlakyJob did not reach attempt 3 within 15 seconds — retries are not working.");
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: true);
        }
    }
}
