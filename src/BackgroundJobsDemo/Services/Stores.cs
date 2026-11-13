namespace BackgroundJobsDemo.Services;

/// <summary>One order in the demo store.</summary>
public record Order(Guid Id, string Customer, decimal Total, DateTimeOffset CreatedAt, string Status);

/// <summary>One report built by <see cref="Jobs.DailyReportJob"/>.</summary>
public record DailyReport(DateOnly Date, int OrderCount, decimal TotalRevenue, int ExpiredCount);

/// <summary>
/// In-memory order store. Thread-safe on purpose: jobs run on the
/// scheduler's threads while the API serves requests on others.
/// </summary>
public sealed class OrderStore
{
    private readonly List<Order> _orders = new();
    private readonly object _lock = new();

    public OrderStore()
    {
        // Seeded demo data: two completed orders from today, one stale
        // pending order from 2 hours ago (the cleanup job will expire it).
        var now = DateTimeOffset.UtcNow;
        _orders.Add(new Order(Guid.NewGuid(), "Acme Corp", 249.99m, now.AddHours(-5), "Completed"));
        _orders.Add(new Order(Guid.NewGuid(), "Globex", 89.50m, now.AddHours(-3), "Completed"));
        _orders.Add(new Order(Guid.NewGuid(), "Initech", 129.00m, now.AddHours(-2), "Pending"));
    }

    public IReadOnlyList<Order> All
    {
        get { lock (_lock) { return _orders.ToList(); } }
    }

    public void Add(Order order)
    {
        lock (_lock) { _orders.Add(order); }
    }

    /// <summary>Expires pending orders older than <paramref name="olderThan"/>; returns how many.</summary>
    public int ExpireStaleOrders(TimeSpan olderThan)
    {
        var cutoff = DateTimeOffset.UtcNow - olderThan;
        lock (_lock)
        {
            var stale = _orders.Where(o => o.Status == "Pending" && o.CreatedAt < cutoff).ToList();
            foreach (var order in stale)
            {
                _orders.Remove(order);
                _orders.Add(order with { Status = "Expired" });
            }
            return stale.Count;
        }
    }

    public (int Count, decimal Revenue) TodaysCompletedTotals(DateOnly date)
    {
        lock (_lock)
        {
            var todays = _orders.Where(o => o.Status == "Completed"
                && DateOnly.FromDateTime(o.CreatedAt.DateTime) == date);
            return (todays.Count(), todays.Sum(o => o.Total));
        }
    }
}

/// <summary>In-memory report store, keyed by date so the daily report job is idempotent.</summary>
public sealed class ReportStore
{
    private readonly Dictionary<DateOnly, DailyReport> _reports = new();
    private readonly object _lock = new();

    public IReadOnlyList<DailyReport> All
    {
        get { lock (_lock) { return _reports.Values.OrderBy(r => r.Date).ToList(); } }
    }

    public DailyReport Upsert(DailyReport report)
    {
        lock (_lock) { _reports[report.Date] = report; return report; }
    }

    public bool HasReportFor(DateOnly date)
    {
        lock (_lock) { return _reports.ContainsKey(date); }
    }
}

/// <summary>Abstraction over sending mail — jobs depend on this, not on SMTP.</summary>
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct = default);
}

/// <summary>Attempt counter for the FlakyJob demo — see FlakyJob for why
/// this lives in a service instead of the JobDataMap.</summary>
public sealed class FlakyDemoState
{
    private int _attempts;

    public int Attempts => Volatile.Read(ref _attempts);

    public int NextAttempt() => Interlocked.Increment(ref _attempts);
}

/// <summary>Test-double-friendly sender that records mail instead of sending it.</summary>
public sealed class InMemoryEmailSender : IEmailSender
{
    private readonly List<SentEmail> _sent = new();
    private readonly object _lock = new();

    public record SentEmail(string To, string Subject, string Body, DateTimeOffset SentAt);

    public IReadOnlyList<SentEmail> Sent
    {
        get { lock (_lock) { return _sent.ToList(); } }
    }

    public Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        lock (_lock) { _sent.Add(new SentEmail(to, subject, body, DateTimeOffset.UtcNow)); }
        return Task.CompletedTask;
    }
}
