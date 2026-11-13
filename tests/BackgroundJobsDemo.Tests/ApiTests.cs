using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BackgroundJobsDemo.Tests;

/// <summary>End-to-end: the HTTP control surface that operates the scheduler.</summary>
public sealed class ApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Root_ReturnsServiceInfo()
    {
        var response = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("BackgroundJobsDemo", body);
    }

    [Fact]
    public async Task JobsEndpoint_ListsAllFourScheduledJobs()
    {
        var jobs = await _client.GetFromJsonAsync<JsonElement[]>("/jobs");
        Assert.NotNull(jobs);
        var names = jobs.Select(j => j.GetProperty("job").GetString()).ToHashSet();
        Assert.Equal(new[] { "daily-report", "email-digest", "flaky-demo", "stale-cleanup" }, names.OrderBy(n => n));
    }

    [Fact]
    public async Task TriggerDailyReport_ProducesTodaysReport()
    {
        var trigger = await _client.PostAsync("/jobs/daily-report/trigger", null);
        Assert.Equal(HttpStatusCode.Accepted, trigger.StatusCode);

        // The job runs asynchronously after the trigger — poll for the report.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var reports = await _client.GetFromJsonAsync<JsonElement[]>("/reports");
            if (reports is { Length: > 0 }) return;
            await Task.Delay(200);
        }
        Assert.Fail("No report appeared within 10 seconds of triggering daily-report.");
    }

    [Fact]
    public async Task TriggerUnknownJob_Returns404()
    {
        var response = await _client.PostAsync("/jobs/no-such-job/trigger", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PauseAndResume_ChangeJobState()
    {
        var pause = await _client.PostAsync("/jobs/email-digest/pause", null);
        Assert.Equal(HttpStatusCode.OK, pause.StatusCode);

        var jobs = await _client.GetFromJsonAsync<JsonElement[]>("/jobs");
        var digest = jobs!.Single(j => j.GetProperty("job").GetString() == "email-digest");
        Assert.Equal("Paused", digest.GetProperty("state").GetString());

        var resume = await _client.PostAsync("/jobs/email-digest/resume", null);
        Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
    }

    [Fact]
    public async Task TriggerCleanup_ExpiresStaleOrders()
    {
        var trigger = await _client.PostAsync("/jobs/stale-cleanup/trigger", null);
        Assert.Equal(HttpStatusCode.Accepted, trigger.StatusCode);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var orders = await _client.GetFromJsonAsync<JsonElement[]>("/orders");
            if (orders!.Any(o => o.GetProperty("status").GetString() == "Expired")) return;
            await Task.Delay(200);
        }
        Assert.Fail("No order was expired within 10 seconds of triggering stale-cleanup.");
    }
}
