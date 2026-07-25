using System.Text;
using System.Text.Json;
using BillingNotificationService.Models;

namespace BillingNotificationService.Services;

public class LineMessagingService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<LineMessagingService> _logger;

    public LineMessagingService(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<LineMessagingService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    public async Task SendMonthlySummaryAsync(IReadOnlyList<PaymentGroup> groups, List<BillingRecord> records, int year, int month, CancellationToken ct = default)
    {
        var userId = _config["Line:UserId"];
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogError("Line:UserId is not configured. Send any message to your LINE bot and check logs for your User ID.");
            return;
        }

        await PushMessageAsync(userId, BuildSummaryMessage(groups, records, year, month), ct);

        var familyGroupId = _config["Line:FamilyUserId"];
        if (!string.IsNullOrEmpty(familyGroupId))
        {
            var utilityMessage = BuildUtilityMessage(groups, records, year, month);
            if (utilityMessage != null)
                await PushMessageAsync(familyGroupId, utilityMessage, ct);
        }
    }

    public async Task SendSummaryToUserAsync(string userId, IReadOnlyList<PaymentGroup> groups, List<BillingRecord> records, int year, int month, CancellationToken ct = default)
    {
        await PushMessageAsync(userId, BuildSummaryMessage(groups, records, year, month), ct);
    }

    private static string BuildSummaryMessage(IReadOnlyList<PaymentGroup> groups, List<BillingRecord> records, int year, int month)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"📊 {year}年{month}月帳單摘要");
        sb.AppendLine();

        if (records.Count == 0)
        {
            sb.Append("本月未找到符合的帳單信件。");
            return sb.ToString();
        }

        foreach (var group in groups)
        {
            var groupRecords = records.Where(r => group.Labels.Contains(r.Label)).ToList();
            if (groupRecords.Count == 0) continue;

            var total = groupRecords.Sum(r => r.Amount);
            sb.AppendLine($"💳 {group.AccountDescription}");
            sb.AppendLine($"   請準備 NT$ {total:N0}");
            foreach (var r in groupRecords.OrderByDescending(r => r.Amount))
                sb.AppendLine($"   • {r.Label}：NT$ {r.Amount:N0}");
            sb.AppendLine();
        }

        var ungrouped = records.Where(r => !groups.Any(g => g.Labels.Contains(r.Label))).ToList();
        if (ungrouped.Count > 0)
        {
            sb.AppendLine("🏠 居家費用");
            foreach (var r in ungrouped.OrderByDescending(r => r.Amount))
            {
                var dueDateStr = r.DueDate.HasValue ? $"，繳費期限{r.DueDate.Value:yyyy-MM-dd}" : "";
                sb.AppendLine($"   {r.Date.Month}月{r.Label}，共{r.Amount:N0}元{dueDateStr}");
            }
            sb.AppendLine();
        }

        sb.AppendLine($"💰 合計：NT$ {records.Sum(r => r.Amount):N0}");
        sb.Append($"📅 {year}/{month:D2}/01 ~ {year}/{month:D2}/{DateTime.DaysInMonth(year, month):D2}");

        return sb.ToString();
    }

    private static string? BuildUtilityMessage(IReadOnlyList<PaymentGroup> groups, List<BillingRecord> records, int year, int month)
    {
        var ungrouped = records.Where(r => !groups.Any(g => g.Labels.Contains(r.Label))).ToList();
        if (ungrouped.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine($"🏠 {year}年{month}月居家費用");
        sb.AppendLine();
        foreach (var r in ungrouped.OrderByDescending(r => r.Amount))
        {
            var dueDateStr = r.DueDate.HasValue ? $"，繳費期限{r.DueDate.Value:yyyy-MM-dd}" : "";
            sb.AppendLine($"   {r.Date.Month}月{r.Label}，共{r.Amount:N0}元{dueDateStr}");
        }
        return sb.ToString().TrimEnd();
    }

    public async Task ReplyMessageAsync(string replyToken, string text, CancellationToken ct = default)
    {
        var token = _config["Line:ChannelAccessToken"];
        if (string.IsNullOrEmpty(token)) return;

        var payload = new
        {
            replyToken,
            messages = new[] { new { type = "text", text } }
        };

        using var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.line.me/v2/bot/message/reply");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("LINE reply failed: {Status} {Body}", response.StatusCode, body);
        }
    }

    private async Task PushMessageAsync(string userId, string text, CancellationToken ct)
    {
        var token = _config["Line:ChannelAccessToken"];
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogError("Line:ChannelAccessToken is not configured.");
            return;
        }

        var payload = new
        {
            to = userId,
            messages = new[] { new { type = "text", text } }
        };

        using var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.line.me/v2/bot/message/push");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var response = await client.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
            _logger.LogInformation("LINE notification sent successfully");
        else
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("LINE notification failed: {Status} {Body}", response.StatusCode, body);
        }
    }
}
