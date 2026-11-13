using BackgroundJobsDemo.Jobs;
using BackgroundJobsDemo.Scheduling;
using BackgroundJobsDemo.Services;
using Quartz;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// The demo's "database": an in-memory store of orders, reports and sent mail.
// Swap these for EF Core / a real outbox table in production — the jobs
// don't care, they only depend on the abstractions.
builder.Services.AddSingleton<OrderStore>();
builder.Services.AddSingleton<ReportStore>();
builder.Services.AddSingleton<FlakyDemoState>();
builder.Services.AddSingleton<IEmailSender, InMemoryEmailSender>();

// Quartz.NET, wired through the ASP.NET Core host so the scheduler starts
// and stops gracefully with the app (no orphaned threads on shutdown).
builder.Services.AddQuartz(q =>
{
    q.AddJobListener<LoggingJobListener>();

    // Daily sales report — 06:00 every day, durable so it survives restarts
    // of the scheduler configuration (the trigger is the schedule of record).
    q.AddJob<DailyReportJob>(opts => opts.WithIdentity("daily-report").StoreDurably());
    q.AddTrigger(opts => opts
        .ForJob("daily-report")
        .WithIdentity("daily-report-trigger")
        .WithCronSchedule("0 0 6 * * ?")
        .WithDescription("Builds the daily sales report at 06:00"));

    // Subscriber digest — every weekday at 08:30.
    q.AddJob<EmailDigestJob>(opts => opts.WithIdentity("email-digest").StoreDurably());
    q.AddTrigger(opts => opts
        .ForJob("email-digest")
        .WithIdentity("email-digest-trigger")
        .WithCronSchedule("0 30 8 ? * MON-FRI")
        .WithDescription("Sends the morning digest to subscribers"));

    // Stale-order cleanup — every 15 minutes, so abandoned carts never
    // sit in "pending" forever.
    q.AddJob<StaleOrderCleanupJob>(opts => opts.WithIdentity("stale-cleanup").StoreDurably());
    q.AddTrigger(opts => opts
        .ForJob("stale-cleanup")
        .WithIdentity("stale-cleanup-trigger")
        .WithSimpleSchedule(s => s.WithIntervalInMinutes(15).RepeatForever())
        .WithDescription("Expires pending orders older than 30 minutes"));

    // The flaky job — fires every minute and fails its first two attempts
    // on purpose, so you can watch Quartz retry it (see FlakyJob).
    q.AddJob<FlakyJob>(opts => opts.WithIdentity("flaky-demo").StoreDurably());
    q.AddTrigger(opts => opts
        .ForJob("flaky-demo")
        .WithIdentity("flaky-demo-trigger")
        .WithSimpleSchedule(s => s.WithIntervalInMinutes(1).RepeatForever())
        .WithDescription("Demo job that fails twice, then succeeds"));
});
builder.Services.AddQuartzHostedService(opts =>
{
    // Wait for running jobs to finish before the host shuts down.
    opts.WaitForJobsToComplete = true;
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/", () => new
{
    service = "BackgroundJobsDemo",
    description = "Quartz.NET background jobs in ASP.NET Core 8 — reports, digests, cleanup, retries.",
    endpoints = new[]
    {
        "GET  /jobs            — list scheduled jobs and their next fire time",
        "POST /jobs/{key}/trigger — run a job right now",
        "POST /jobs/{key}/pause   — pause a job",
        "POST /jobs/{key}/resume  — resume a paused job",
        "GET  /reports         — reports built by DailyReportJob",
        "GET  /orders          — orders in the store (seeded demo data)",
        "GET  /mail            — emails 'sent' by EmailDigestJob",
    }
});

app.MapGet("/jobs", async (ISchedulerFactory factory) =>
{
    var scheduler = await factory.GetScheduler();
    var jobKeys = await scheduler.GetJobKeys(Quartz.Impl.Matchers.GroupMatcher<JobKey>.AnyGroup());
    var jobs = new List<object>();
    foreach (var key in jobKeys.OrderBy(k => k.Name))
    {
        var triggers = await scheduler.GetTriggersOfJob(key);
        var trigger = triggers.FirstOrDefault();
        jobs.Add(new
        {
            job = key.Name,
            state = (await scheduler.GetTriggerState(trigger!.Key)).ToString(),
            nextFireTime = trigger.GetNextFireTimeUtc()?.LocalDateTime,
            trigger = trigger.Key.Name,
            description = trigger.Description,
        });
    }
    return Results.Ok(jobs);
});

app.MapPost("/jobs/{key}/trigger", async (string key, ISchedulerFactory factory) =>
{
    var scheduler = await factory.GetScheduler();
    var jobKey = new JobKey(key);
    if (!await scheduler.CheckExists(jobKey)) return Results.NotFound(new { error = $"No job named '{key}'." });
    await scheduler.TriggerJob(jobKey);
    return Results.Accepted($"/jobs/{key}", new { job = key, status = "triggered" });
});

app.MapPost("/jobs/{key}/pause", async (string key, ISchedulerFactory factory) =>
{
    var scheduler = await factory.GetScheduler();
    var jobKey = new JobKey(key);
    if (!await scheduler.CheckExists(jobKey)) return Results.NotFound(new { error = $"No job named '{key}'." });
    await scheduler.PauseJob(jobKey);
    return Results.Ok(new { job = key, status = "paused" });
});

app.MapPost("/jobs/{key}/resume", async (string key, ISchedulerFactory factory) =>
{
    var scheduler = await factory.GetScheduler();
    var jobKey = new JobKey(key);
    if (!await scheduler.CheckExists(jobKey)) return Results.NotFound(new { error = $"No job named '{key}'." });
    await scheduler.ResumeJob(jobKey);
    return Results.Ok(new { job = key, status = "resumed" });
});

app.MapGet("/reports", (ReportStore store) => Results.Ok(store.All));
app.MapGet("/orders", (OrderStore store) => Results.Ok(store.All));
app.MapGet("/mail", (IEmailSender sender) => Results.Ok(((InMemoryEmailSender)sender).Sent));

app.Run();

// Visible to the test project so WebApplicationFactory can boot the app.
public partial class Program { }
