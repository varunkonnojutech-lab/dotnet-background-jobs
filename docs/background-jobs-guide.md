# Background Jobs in .NET — the decision guide

## Quartz vs Hangfire vs IHostedService

| | `IHostedService` / `BackgroundService` | Quartz.NET | Hangfire |
|---|---|---|---|
| Scheduling | You write the timer loop | Cron + simple triggers, built in | Cron, built in |
| Persistence | None (in-memory only) | Optional ADO.NET job store | SQL Server / Redis storage, required |
| Retries | You write them | `JobExecutionException` refire, misfire policies | Automatic retries with backoff |
| Dashboard | None | None (build your own, like the `/jobs` endpoint here) | Built-in dashboard |
| Clustering | No | Yes, with a persistent store | Yes |
| Best for | One simple recurring task | Scheduled jobs inside your API, full control | Fire-and-forget + enqueue from anywhere, teams that want a UI |

**Rule of thumb:** one simple timer → `BackgroundService`. Scheduled jobs that live with your API and need cron, retries, and clustering → Quartz.NET. Jobs enqueued from web requests with a dashboard your ops team will actually open → Hangfire.

## Cron expressions in Quartz

Quartz cron has **six** fields (seconds first), unlike Linux cron's five:

```
seconds minutes hours day-of-month month day-of-week [year]
0       30      8     ?            *     MON-FRI
```

`?` means "no specific value" and is required in one of the two day fields. Common schedules:

| Expression | Meaning |
|---|---|
| `0 0 6 * * ?` | 06:00 every day |
| `0 30 8 ? * MON-FRI` | 08:30 weekdays |
| `0 0/15 * * * ?` | Every 15 minutes |
| `0 0 0 1 * ?` | Midnight on the 1st of the month |

## Misfires — what happens when the app was down at fire time?

If the scheduler was stopped when a trigger should have fired, Quartz applies a **misfire policy** when it restarts. The default for cron triggers is "smart policy" (fire once now, then resume the schedule). For a daily report that's usually right; for a "send exactly one email" job it isn't — make the job idempotent and the policy stops mattering. See `DailyReportJob`: one report row per date, overwritten on re-run.

## Retries — the two mechanisms

1. **Refire immediately**: throw `JobExecutionException` with `RefireImmediately = true`. Good for transient failures (a downstream API blipped). Bound it — count attempts in the `JobDataMap` and stop refiring after N.
2. **Reschedule with backoff**: catch the failure and schedule a new one-off trigger with a delay. Better when the downstream needs time to recover.

**The gotcha:** don't keep the attempt counter in the `JobDataMap`. Changes to it are only persisted after an execution *completes*, and a refired job re-reads the stored job detail — so the counter resets to 1 on every refire and the job retries forever in a tight loop (we watched this happen: 200,000+ fires). Keep retry state in an injected service or your own store, as `FlakyJob` does with `FlakyDemoState`. `[PersistJobDataAfterExecution]` persists the map after successful runs; it does not save you on the refire path.

## Concurrency

- `[DisallowConcurrentExecution]` on the job class: Quartz won't start a second instance while one is running. The missed fire becomes a misfire and follows the misfire policy.
- `[PersistJobDataAfterExecution]` is separate — it only controls JobDataMap persistence. You usually want both on stateful jobs.
- For multi-instance deployments, use a persistent job store with clustering (`UsePersistentStore` + `UseClustering`) so only one node fires each trigger.

## Graceful shutdown

`AddQuartzHostedService(opts => opts.WaitForJobsToComplete = true)` makes the host wait for running jobs before exiting. Without it, a deploy can kill a job mid-write. Always set it, and always honour `context.CancellationToken` inside long jobs so shutdown doesn't hang forever.

## Testing jobs

Jobs are plain classes — test the logic directly by calling `Execute` (pass `null!` for the context if the job never touches it). Test the *scheduling* behaviour (retries, triggers) against a real in-memory scheduler, as `JobTests.FlakyJob_FailsTwiceThenSucceeds_OnARealScheduler` does. Test the *control surface* (trigger/pause/resume) over HTTP with `WebApplicationFactory`, as `ApiTests` does.
