using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BillingNotificationService.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();

builder.Services.AddScoped<ClaudePdfParserService>();
builder.Services.AddScoped<GmailScannerService>();
builder.Services.AddScoped<LineMessagingService>();
builder.Services.AddScoped<BillingGroupingService>();
builder.Services.AddSingleton<BillingNotificationWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BillingNotificationWorker>());

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Step 1: Call this first to complete Google OAuth and verify Gmail connection
app.MapGet("/api/auth/gmail", async (GmailScannerService gmail) =>
{
    var email = await gmail.AuthorizeAsync();
    return Results.Ok(new { message = "Gmail authorization successful", email });
})
.WithName("AuthorizeGmail")
.WithOpenApi();

// Debug: list all Gmail label names — compare with BillingLabel enum values
app.MapGet("/api/debug/gmail-labels", async (GmailScannerService gmail) =>
{
    var labels = await gmail.GetAllLabelNamesAsync();
    return Results.Ok(labels);
})
.WithName("DebugGmailLabels")
.WithOpenApi();

// Preview emails found for a given month: GET /api/billing/emails?year=2026&month=6
app.MapGet("/api/billing/emails", async (
    int? year,
    int? month,
    GmailScannerService gmail,
    BillingGroupingService grouping,
    CancellationToken ct) =>
{
    var currentMonth = DateTime.Now;
    var targetYear = year ?? currentMonth.Year;
    var targetMonth = month ?? currentMonth.Month;

    var previews = await gmail.GetEmailPreviewsAsync(targetYear, targetMonth, ct);

    var transfers = grouping.GetGroups().Select(g =>
    {
        var cards = previews
            .Where(p => g.Labels.Contains(p.Label) && p.ParsedAmount.HasValue)
            .Select(p => new { label = p.Label.ToString(), amount = p.ParsedAmount!.Value })
            .ToList();
        return new
        {
            account = g.AccountDescription,
            transferAmount = cards.Sum(c => c.amount),
            cards
        };
    });

    return Results.Ok(new { year = targetYear, month = targetMonth, transfers, emails = previews });
})
.WithName("GetBillingEmails")
.WithOpenApi();

// Manual trigger: POST /api/billing/scan?year=2026&month=6
// Omit params to default to current month
app.MapPost("/api/billing/scan", async (
    int? year,
    int? month,
    BillingNotificationWorker worker,
    CancellationToken ct) =>
{
    var now = DateTime.Now;
    var targetYear = year ?? now.Year;
    var targetMonth = month ?? now.Month;

    var summaries = await worker.RunScanAsync(targetYear, targetMonth, ct);
    return Results.Ok(new
    {
        year = targetYear,
        month = targetMonth,
        transfers = summaries.Select(s => new
        {
            account = s.Account,
            transferAmount = s.Total,
            cards = s.Cards.Select(c => new { label = c.Label.ToString(), amount = c.Amount })
        })
    });
})
.WithName("TriggerBillingScan")
.WithOpenApi();

// LINE Messaging API webhook — log your LINE User ID when you send any message to the bot
app.MapPost("/api/line/webhook", async (HttpContext context, IConfiguration config, ILogger<Program> logger) =>
{
    var channelSecret = config["Line:ChannelSecret"] ?? "";
    using var ms = new MemoryStream();
    await context.Request.Body.CopyToAsync(ms);
    var bodyBytes = ms.ToArray();
    var body = Encoding.UTF8.GetString(bodyBytes);

    // Verify LINE signature to reject forged requests
    var signature = context.Request.Headers["X-Line-Signature"].FirstOrDefault() ?? "";
    if (!string.IsNullOrEmpty(channelSecret) && !string.IsNullOrEmpty(signature))
    {
        var expectedHash = Convert.ToBase64String(
            new HMACSHA256(Encoding.UTF8.GetBytes(channelSecret)).ComputeHash(bodyBytes));
        if (expectedHash != signature)
        {
            logger.LogWarning("LINE webhook: invalid signature");
            return Results.Unauthorized();
        }
    }

    try
    {
        using var doc = JsonDocument.Parse(body);
        foreach (var ev in doc.RootElement.GetProperty("events").EnumerateArray())
        {
            if (ev.TryGetProperty("source", out var source) &&
                source.TryGetProperty("userId", out var userId))
            {
                logger.LogWarning("==> Your LINE User ID: {UserId}  (copy this to Line:UserId in appsettings)", userId.GetString());
            }
        }
    }
    catch { /* non-event webhook calls (e.g., verify ping) are fine */ }

    return Results.Ok();
})
.WithName("LineWebhook")
.WithOpenApi();

app.Run();