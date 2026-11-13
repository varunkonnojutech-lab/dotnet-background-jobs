# Background Jobs in .NET 8 — Quartz.NET Done Right

[![CI](https://github.com/varunkonnojutech-lab/dotnet-background-jobs/actions/workflows/ci.yml/badge.svg)](https://github.com/varunkonnojutech-lab/dotnet-background-jobs/actions/workflows/ci.yml)
[![Open in GitHub Codespaces](https://github.com/codespaces/badge.svg)](https://codespaces.new/varunkonnojutech-lab/dotnet-background-jobs)

Every real app has work that shouldn't happen inside a web request: nightly reports, morning digests, cleaning up abandoned carts, retrying a flaky third-party call. This repo shows how to do that work properly in .NET 8 with **Quartz.NET** — scheduled, observable, and safe to re-run.

## Who this is for

- **.NET developers** who have only ever used `Task.Delay` loops or `IHostedService` timers and want a real scheduler.
- **Backend engineers** who need cron schedules, retries, misfire handling, and graceful shutdown — without standing up Hangfire's dashboard or a separate worker service.
- **Interview prep**: "How do you run background jobs in .NET?" is a common senior-level question. This repo is the answer, with code.

## What you'll learn

1. **Quartz.NET in ASP.NET Core** — `AddQuartz` + `AddQuartzHostedService`, DI job factory, graceful shutdown (`WaitForJobsToComplete`).
2. **Cron triggers** — daily reports at 06:00, weekday digests at 08:30, cleanup every 15 minutes.
3. **Idempotent jobs** — the daily report overwrites one row per date, so a misfire or manual re-trigger never double-counts.
4. **Retries that actually work** — `JobExecutionException` with `RefireImmediately`, and the JobDataMap gotcha that makes naive attempt counters spin forever (the counter lives in an injected service instead).
5. **No overlapping runs** — `[DisallowConcurrentExecution]` on jobs that must never run twice at once.
6. **Operating the scheduler over HTTP** — list jobs, trigger, pause, and resume from minimal API endpoints.
7. **When NOT to use Quartz** — see `docs/background-jobs-guide.md` for the Quartz vs Hangfire vs `IHostedService` decision guide.

## The demo

Four jobs, all visible and operable through the API:

| Job | Schedule | What it does |
|---|---|---|
| `daily-report` | 06:00 daily (cron) | Builds the day's sales report from completed orders |
| `email-digest` | 08:30 Mon–Fri (cron) | Sends the morning digest to subscribers |
| `stale-cleanup` | Every 15 min | Expires pending orders older than 30 minutes |
| `flaky-demo` | Every 1 min | Fails twice on purpose, then succeeds — watch the retry |

## Quick start

```bash
dotnet run --project src/BackgroundJobsDemo
```

Then:

```bash
# What is scheduled, and when does each job fire next?
curl localhost:5000/jobs

# Run the daily report right now instead of waiting for 06:00
curl -X POST localhost:5000/jobs/daily-report/trigger
curl localhost:5000/reports

# Pause the digest (and resume it later)
curl -X POST localhost:5000/jobs/email-digest/pause
curl -X POST localhost:5000/jobs/email-digest/resume

# See what the jobs produced
curl localhost:5000/orders   # orders, including ones the cleanup expired
curl localhost:5000/mail     # "sent" digest emails
```

Swagger UI is available at `/swagger` in Development.

## Project layout

```
src/BackgroundJobsDemo/
├── Program.cs                  # Quartz config + minimal API control surface
├── Jobs/
│   └── Jobs.cs                 # DailyReportJob, EmailDigestJob, StaleOrderCleanupJob, FlakyJob
├── Scheduling/
│   └── LoggingJobListener.cs   # Logs every job start/finish/failure (hook metrics here)
└── Services/
    └── Stores.cs               # In-memory order/report/mail stores (swap for EF Core)
tests/BackgroundJobsDemo.Tests/
├── JobTests.cs                 # Job logic + a real-scheduler retry test
└── ApiTests.cs                 # End-to-end: trigger/pause/resume over HTTP
docs/
└── background-jobs-guide.md    # Quartz vs Hangfire vs IHostedService, cron, misfires, clustering
```

## The three rules of background jobs

1. **Make every job idempotent.** A job will run twice — misfires, restarts, manual triggers. Design so the second run is a no-op (upsert by date, dedupe by idempotency key).
2. **Never let a job overlap itself.** `[DisallowConcurrentExecution]` on anything that writes shared state.
3. **Fail loudly, retry deliberately.** Log every failure, retry with backoff, and alert when retries are exhausted — never swallow a failed job silently.

## Next steps

- Swap the in-memory stores for EF Core and give Quartz a persistent ADO.NET job store (`UsePersistentStore`) so schedules survive restarts and scale to a cluster.
- Add an outbox table so jobs publish events instead of doing side effects inline — see the [outbox-pattern](https://github.com/varunkonnojutech-lab/outbox-pattern) repo.
- Push job duration/failure metrics to OpenTelemetry — see [dotnet-observability](https://github.com/varunkonnojutech-lab/dotnet-observability).

## License

MIT — see [LICENSE](LICENSE).
