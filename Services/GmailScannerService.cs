using System.Text;
using System.Text.RegularExpressions;
using BillingNotificationService.Enums;
using BillingNotificationService.Models;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;

namespace BillingNotificationService.Services;

public class GmailScannerService
{
    private static readonly string[] Scopes = [GmailService.Scope.GmailReadonly];

    private readonly IConfiguration _config;
    private readonly ILogger<GmailScannerService> _logger;
    private readonly ClaudePdfParserService _claudeParser;
    private readonly BillPromptService _promptService;

    public GmailScannerService(IConfiguration config, ILogger<GmailScannerService> logger, ClaudePdfParserService claudeParser, BillPromptService promptService)
    {
        _config = config;
        _logger = logger;
        _claudeParser = claudeParser;
        _promptService = promptService;
    }

    public async Task<List<EmailPreview>> GetEmailPreviewsAsync(int year, int month, CancellationToken ct = default)
    {
        GmailService service;
        try { service = await CreateGmailServiceAsync(ct); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create Gmail service");
            return [];
        }

        var previews = new List<EmailPreview>();

        try
        {
            var labelMap = await ResolveLabelIdsAsync(service, ct);

            foreach (var (billingLabel, gmailLabelId) in labelMap)
            {
                var listRequest = service.Users.Messages.List("me");
                listRequest.LabelIds = new[] { gmailLabelId };
                listRequest.Q = BuildDateQuery(year, month, requirePdf: !_promptService.IsUtility(billingLabel));
                listRequest.MaxResults = _config.GetValue<long>("Gmail:MaxResults", 50);

                var response = await listRequest.ExecuteAsync(ct);
                if (response.Messages == null) continue;

                foreach (var msgRef in response.Messages)
                {
                    try
                    {
                        var getRequest = service.Users.Messages.Get("me", msgRef.Id);
                        getRequest.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
                        var message = await getRequest.ExecuteAsync(ct);

                        var headers = message.Payload?.Headers ?? [];
                        var subject = headers.FirstOrDefault(h => h.Name == "Subject")?.Value ?? "(no subject)";
                        var from = headers.FirstOrDefault(h => h.Name == "From")?.Value ?? "";
                        var dateStr = headers.FirstOrDefault(h => h.Name == "Date")?.Value ?? "";

                        if (!DateTime.TryParse(dateStr, out var date))
                            date = DateTimeOffset.FromUnixTimeMilliseconds(message.InternalDate ?? 0).LocalDateTime;

                        var pdfBytes = await GetPdfAttachmentAsync(service, message.Id!, message.Payload, ct);
                        decimal? amount;
                        string snippet;
                        if (pdfBytes != null)
                        {
                            var password = _config[$"Gmail:PdfPasswords:{billingLabel}"] ?? "";
                            (amount, _) = await _claudeParser.ExtractBillInfoFromPdfAsync(pdfBytes, password, _promptService.GetPdfPrompt(billingLabel), billingLabel.ToString(), ct);
                            snippet = "(PDF 附件)";
                        }
                        else
                        {
                            var bodyText = ExtractBody(message.Payload);
                            (amount, _) = await _claudeParser.ExtractBillInfoFromTextAsync(bodyText, _promptService.GetTextPrompt(billingLabel), billingLabel.ToString(), ct);
                            snippet = bodyText.Length > 500 ? bodyText[..500] + "…" : bodyText;
                        }

                        previews.Add(new EmailPreview(message.Id!, subject, from, date, amount, snippet, billingLabel));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to process message {MessageId}", msgRef.Id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching email previews");
        }

        return previews;
    }

    public async Task<List<string>> GetAllLabelNamesAsync()
    {
        var service = await CreateGmailServiceAsync(CancellationToken.None);
        var response = await service.Users.Labels.List("me").ExecuteAsync();
        return response.Labels.Select(l => l.Name).OrderBy(n => n).ToList();
    }

    public async Task<string> AuthorizeAsync()
    {
        var service = await CreateGmailServiceAsync(CancellationToken.None);
        var profile = await service.Users.GetProfile("me").ExecuteAsync();
        return profile.EmailAddress;
    }

    public async Task<List<BillingRecord>> ScanBillsForMonthAsync(int year, int month, CancellationToken ct = default)
    {
        GmailService service;
        try
        {
            service = await CreateGmailServiceAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create Gmail service. Ensure credentials.json exists and OAuth is completed.");
            return [];
        }

        var records = new List<BillingRecord>();

        try
        {
            var labelMap = await ResolveLabelIdsAsync(service, ct);

            _logger.LogInformation("Scanning {LabelCount} labels for {Year}/{Month:D2}", labelMap.Count, year, month);

            foreach (var (billingLabel, gmailLabelId) in labelMap)
            {
                var listRequest = service.Users.Messages.List("me");
                listRequest.LabelIds = new[] { gmailLabelId };
                listRequest.Q = BuildDateQuery(year, month, requirePdf: !_promptService.IsUtility(billingLabel));
                listRequest.MaxResults = _config.GetValue<long>("Gmail:MaxResults", 50);

                var response = await listRequest.ExecuteAsync(ct);
                if (response.Messages == null || response.Messages.Count == 0)
                {
                    _logger.LogInformation("No emails found for label {Label}", billingLabel);
                    continue;
                }

                _logger.LogInformation("Found {Count} emails for label {Label}", response.Messages.Count, billingLabel);

                foreach (var msgRef in response.Messages)
                {
                    try
                    {
                        var getRequest = service.Users.Messages.Get("me", msgRef.Id);
                        getRequest.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
                        var message = await getRequest.ExecuteAsync(ct);

                        var record = await ParseBillingRecordAsync(service, message, billingLabel, ct);
                        if (record != null)
                        {
                            records.Add(record);
                            _logger.LogInformation("Parsed: [{Label}] {Subject} = NT${Amount:N0}", billingLabel, record.Subject, record.Amount);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to process message {MessageId}", msgRef.Id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning Gmail messages");
        }

        // Per label, keep only the latest record (handles banks that send multiple emails per month)
        return records
            .GroupBy(r => r.Label)
            .Select(g =>
            {
                var latest = g.OrderByDescending(r => r.Date).First();
                if (g.Count() > 1)
                    _logger.LogInformation("Label {Label}: {Count} records found, keeping latest ({Date:yyyy-MM-dd})", latest.Label, g.Count(), latest.Date);
                return latest;
            })
            .ToList();
    }

    private async Task<BillingRecord?> ParseBillingRecordAsync(GmailService service, Message message, BillingLabel label, CancellationToken ct)
    {
        var headers = message.Payload?.Headers ?? [];
        var subject = headers.FirstOrDefault(h => h.Name == "Subject")?.Value ?? "(no subject)";
        var from = headers.FirstOrDefault(h => h.Name == "From")?.Value ?? "";
        var dateStr = headers.FirstOrDefault(h => h.Name == "Date")?.Value ?? "";

        if (!DateTime.TryParse(dateStr, out var date))
            date = DateTimeOffset.FromUnixTimeMilliseconds(message.InternalDate ?? 0).LocalDateTime;

        decimal amount = 0;
        DateOnly? dueDate = null;

        var pdfBytes = await GetPdfAttachmentAsync(service, message.Id!, message.Payload, ct);
        if (pdfBytes != null)
        {
            _logger.LogInformation("[{Label}] PDF found ({Bytes}B), sending to Claude", label, pdfBytes.Length);
            var password = _config[$"Gmail:PdfPasswords:{label}"] ?? "";
            (var pdfAmount, dueDate) = await _claudeParser.ExtractBillInfoFromPdfAsync(pdfBytes, password, _promptService.GetPdfPrompt(label), label.ToString(), ct);
            amount = pdfAmount ?? 0;
        }
        else
        {
            _logger.LogInformation("[{Label}] No PDF attachment — falling back to email body", label);
        }

        if (amount <= 0)
        {
            var bodyText = ExtractBody(message.Payload);
            (var textAmount, var textDueDate) = await _claudeParser.ExtractBillInfoFromTextAsync(bodyText, _promptService.GetTextPrompt(label), label.ToString(), ct);
            amount = textAmount ?? 0;
            dueDate ??= textDueDate;
        }

        if (amount <= 0)
        {
            _logger.LogWarning("[{Label}] No amount found in: {Subject}", label, subject);
            return null;
        }

        return new BillingRecord(message.Id!, subject, from, date, amount, label, DueDate: dueDate);
    }

    private async Task<byte[]?> GetPdfAttachmentAsync(GmailService service, string messageId, MessagePart? part, CancellationToken ct)
    {
        if (part == null) return null;

        var isPdf = part.MimeType?.Contains("pdf", StringComparison.OrdinalIgnoreCase) == true
                    || part.Filename?.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) == true;

        if (isPdf)
        {
            if (part.Body?.Data != null)
                return DecodeBase64UrlBytes(part.Body.Data);

            if (part.Body?.AttachmentId != null)
            {
                var attachment = await service.Users.Messages.Attachments
                    .Get("me", messageId, part.Body.AttachmentId)
                    .ExecuteAsync(ct);
                return DecodeBase64UrlBytes(attachment.Data);
            }
        }

        if (part.Parts != null)
        {
            foreach (var subPart in part.Parts)
            {
                var result = await GetPdfAttachmentAsync(service, messageId, subPart, ct);
                if (result != null) return result;
            }
        }

        return null;
    }

    private async Task<Dictionary<BillingLabel, string>> ResolveLabelIdsAsync(GmailService service, CancellationToken ct)
    {
        var allLabels = await service.Users.Labels.List("me").ExecuteAsync(ct);
        var enumNameMap = Enum.GetValues<BillingLabel>().ToDictionary(l => l.ToString(), l => l);

        return allLabels.Labels
            .Where(l => enumNameMap.ContainsKey(l.Name.Split('/').Last()))
            .ToDictionary(l => enumNameMap[l.Name.Split('/').Last()], l => l.Id);
    }

    private static string BuildDateQuery(int year, int month, bool requirePdf)
    {
        var start = new DateTime(year, month, 1);
        var range = $"after:{start:yyyy/MM/dd} before:{start.AddMonths(1):yyyy/MM/dd}";
        // filename:pdf filters out promotional emails that share the same credit card label but have no PDF
        return requirePdf ? $"{range} filename:pdf" : range;
    }

    private async Task<GmailService> CreateGmailServiceAsync(CancellationToken ct)
    {
        var credentialsPath = _config["Gmail:CredentialsPath"] ?? "credentials.json";
        var tokenStorePath = Path.GetFullPath(_config["Gmail:TokenStorePath"] ?? "token_store");

        if (!File.Exists(credentialsPath))
            throw new FileNotFoundException(
                $"Gmail credentials not found: {credentialsPath}. " +
                "Download OAuth credentials.json from Google Cloud Console (APIs & Services > Credentials > Desktop App).");

        await using var stream = new FileStream(credentialsPath, FileMode.Open, FileAccess.Read);
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            GoogleClientSecrets.FromStream(stream).Secrets,
            Scopes,
            "user",
            ct,
            new FileDataStore(tokenStorePath, true));

        return new GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "BillingNotificationService"
        });
    }

    private static string ExtractBody(MessagePart? part)
    {
        if (part == null) return string.Empty;

        if (part.Body?.Data != null)
        {
            var decoded = DecodeBase64Url(part.Body.Data);
            return part.MimeType == "text/html" ? StripHtml(decoded) : decoded;
        }

        if (part.Parts != null)
        {
            var textPart = part.Parts.FirstOrDefault(p => p.MimeType == "text/plain");
            if (textPart != null) return ExtractBody(textPart);

            var htmlPart = part.Parts.FirstOrDefault(p => p.MimeType == "text/html");
            if (htmlPart != null) return ExtractBody(htmlPart);

            return string.Join("\n", part.Parts.Select(ExtractBody));
        }

        return string.Empty;
    }

    private static string DecodeBase64Url(string base64Url)
    {
        var base64 = base64Url.Replace('-', '+').Replace('_', '/');
        var padding = (4 - base64.Length % 4) % 4;
        return Encoding.UTF8.GetString(Convert.FromBase64String(base64 + new string('=', padding)));
    }

    private static byte[] DecodeBase64UrlBytes(string base64Url)
    {
        var base64 = base64Url.Replace('-', '+').Replace('_', '/');
        var padding = (4 - base64.Length % 4) % 4;
        return Convert.FromBase64String(base64 + new string('=', padding));
    }

    private static string StripHtml(string html)
    {
        html = Regex.Replace(html, @"<(script|style)[^>]*?>.*?</(script|style)>", " ",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        html = Regex.Replace(html, @"<[^>]+>", " ");
        html = System.Net.WebUtility.HtmlDecode(html);
        return Regex.Replace(html, @"\s+", " ").Trim();
    }
}
