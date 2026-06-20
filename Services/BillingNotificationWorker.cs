using System.Text.Json;
using BillingNotificationService.Models;

namespace BillingNotificationService.Services;

public class BillingNotificationWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly IConfiguration _config;
    private readonly ILogger<BillingNotificationWorker> _logger;

    public BillingNotificationWorker(IServiceProvider sp, IConfiguration config, ILogger<BillingNotificationWorker> logger)
    {
        _sp = sp;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BillingNotificationWorker started");

        // Catch up if the scheduled run for this month was missed
        await RunIfOverdueAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var nextRun = CalculateNextRunTime();
            _logger.LogInformation("Next scheduled billing scan: {NextRun:yyyy-MM-dd HH:mm}", nextRun);

            try { await Task.Delay(nextRun - DateTime.Now, stoppingToken); }
            catch (OperationCanceledException) { break; }

            if (stoppingToken.IsCancellationRequested) break;

            var target = nextRun.AddMonths(-1);
            await RunScanAsync(target.Year, target.Month, stoppingToken);
        }
    }

    // Called from the manual API endpoint and the scheduled loop
    public async Task<List<PaymentGroupSummary>> RunScanAsync(int year, int month, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting billing scan for {Year}/{Month:D2}", year, month);
        try
        {
            using var scope = _sp.CreateScope();
            var gmail = scope.ServiceProvider.GetRequiredService<GmailScannerService>();
            var line = scope.ServiceProvider.GetRequiredService<LineMessagingService>();
            var grouping = scope.ServiceProvider.GetRequiredService<BillingGroupingService>();

            var records = await gmail.ScanBillsForMonthAsync(year, month, ct);
            _logger.LogInformation("Found {Count} billing records for {Year}/{Month:D2}", records.Count, year, month);

            var groups = grouping.GetGroups();
            var summaries = groups
                .Select(g =>
                {
                    var cards = records.Where(r => g.Labels.Contains(r.Label)).ToList();
                    return new PaymentGroupSummary(g.AccountDescription, cards.Sum(r => r.Amount), cards);
                })
                .ToList();

            await line.SendMonthlySummaryAsync(groups, records, year, month, ct);
            SaveLastRunDate(DateTime.Now);

            return summaries;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during billing scan for {Year}/{Month:D2}", year, month);
            return [];
        }
    }

    private async Task RunIfOverdueAsync(CancellationToken ct)
    {
        var scheduledDay = _config.GetValue("BillingNotification:ScheduleDayOfMonth", 1);
        var now = DateTime.Now;

        if (now.Day < scheduledDay) return;

        var scheduledThisMonth = new DateTime(now.Year, now.Month, scheduledDay);
        if (ReadLastRunDate() < scheduledThisMonth)
        {
            var target = now.AddMonths(-1);
            _logger.LogInformation("Running overdue scan for {Month}", target.ToString("yyyy/MM"));
            await RunScanAsync(target.Year, target.Month, ct);
        }
    }

    private DateTime CalculateNextRunTime()
    {
        var scheduledDay = _config.GetValue("BillingNotification:ScheduleDayOfMonth", 1);
        var scheduledHour = _config.GetValue("BillingNotification:ScheduleHour", 9);
        var now = DateTime.Now;

        var candidate = new DateTime(now.Year, now.Month, scheduledDay, scheduledHour, 0, 0);
        return candidate > now ? candidate : candidate.AddMonths(1);
    }

    private string LastRunFile => _config["BillingNotification:LastRunFile"] ?? "last_run.json";

    private DateTime ReadLastRunDate()
    {
        try
        {
            if (File.Exists(LastRunFile))
            {
                var data = JsonSerializer.Deserialize<LastRunData>(File.ReadAllText(LastRunFile));
                return data?.LastRun ?? DateTime.MinValue;
            }
        }
        catch { }
        return DateTime.MinValue;
    }

    private void SaveLastRunDate(DateTime date)
    {
        try { File.WriteAllText(LastRunFile, JsonSerializer.Serialize(new LastRunData { LastRun = date })); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to save last run date"); }
    }

    private sealed class LastRunData
    {
        public DateTime LastRun { get; set; }
    }
}
