using System.Text;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Messages;
using iText.Kernel.Pdf;

namespace BillingNotificationService.Services;

public class ClaudePdfParserService
{
    private static readonly Regex NumberPattern = new(@"\d[\d,]*(?:\.\d+)?");

    private readonly AnthropicClient _claude;
    private readonly ILogger<ClaudePdfParserService> _logger;

    public ClaudePdfParserService(IConfiguration config, ILogger<ClaudePdfParserService> logger)
    {
        var apiKey = config["Anthropic:ApiKey"]
            ?? throw new InvalidOperationException("Anthropic:ApiKey is not configured in appsettings");
        _claude = new AnthropicClient { ApiKey = apiKey };
        _logger = logger;
    }

    public async Task<(decimal? Amount, DateOnly? DueDate)> ExtractBillInfoFromPdfAsync(byte[] pdfBytes, string password, string prompt, string labelName, CancellationToken ct = default)
    {
        byte[] cleanPdf;
        try
        {
            cleanPdf = StripPassword(pdfBytes, password);
            _logger.LogInformation("[{Label}] iText7 decrypted: {OrigBytes}B → {CleanBytes}B", labelName, pdfBytes.Length, cleanPdf.Length);
            if (cleanPdf.Length < 500)
                _logger.LogWarning("[{Label}] Decrypted PDF is suspiciously small — wrong password or unsupported encryption", labelName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Label}] iText7 failed, sending raw bytes", labelName);
            cleanPdf = pdfBytes;
        }

        var base64 = Convert.ToBase64String(cleanPdf);

        try
        {
            var response = await _claude.Messages.Create(new MessageCreateParams
            {
                Model = Model.ClaudeHaiku4_5,
                MaxTokens = 50,
                Messages =
                [
                    new()
                    {
                        Role = Role.User,
                        Content = new List<ContentBlockParam>
                        {
                            new DocumentBlockParam { Source = new Base64PdfSource { Data = base64 } },
                            new TextBlockParam { Text = prompt }
                        }
                    }
                ]
            }, ct);

            return ParseBillInfo(response, labelName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Claude PDF call failed for {Label}", labelName);
            return (null, null);
        }
    }

    public async Task<(decimal? Amount, DateOnly? DueDate)> ExtractBillInfoFromTextAsync(string text, string prompt, string labelName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, null);

        try
        {
            var response = await _claude.Messages.Create(new MessageCreateParams
            {
                Model = Model.ClaudeHaiku4_5,
                MaxTokens = 50,
                Messages =
                [
                    new()
                    {
                        Role = Role.User,
                        Content = $"以下是帳單文字：\n\n{text}\n\n{prompt}"
                    }
                ]
            }, ct);

            return ParseBillInfo(response, labelName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Claude text fallback failed for {Label}", labelName);
            return (null, null);
        }
    }

    // Open encrypted PDF with iText7 and write out an unencrypted copy in memory
    private static byte[] StripPassword(byte[] pdfBytes, string password)
    {
        using var inputStream = new MemoryStream(pdfBytes);

        var readerProps = new ReaderProperties();
        if (!string.IsNullOrEmpty(password))
            readerProps.SetPassword(Encoding.Latin1.GetBytes(password));

        using var outputStream = new MemoryStream();

        var reader = new PdfReader(inputStream, readerProps);
        reader.SetUnethicalReading(true);
        var writer = new PdfWriter(outputStream);
        var doc = new PdfDocument(reader, writer);
        doc.Close();

        return outputStream.ToArray();
    }

    private (decimal? Amount, DateOnly? DueDate) ParseBillInfo(Message response, string labelName)
    {
        var raw = response.Content
            .Select(b => b.Value)
            .OfType<TextBlock>()
            .Select(b => b.Text)
            .FirstOrDefault()
            ?.Trim();

        _logger.LogInformation("Claude [{Label}] => '{Raw}'", labelName, raw ?? "(no response)");

        if (raw == null)
        {
            _logger.LogWarning("Empty response from Claude for {Label}", labelName);
            return (null, null);
        }

        var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var amount = ParseDecimal(lines[0], labelName);
        DateOnly? dueDate = lines.Length > 1 ? ParseDate(lines[1], labelName) : null;

        return (amount, dueDate);
    }

    private decimal? ParseDecimal(string raw, string labelName)
    {
        if (decimal.TryParse(raw.Replace(",", ""), out var direct) && direct >= 0)
            return direct;

        var match = NumberPattern.Match(raw);
        if (match.Success && decimal.TryParse(match.Value.Replace(",", ""), out var extracted) && extracted >= 0)
            return extracted;

        _logger.LogWarning("No parseable amount in '{Raw}' for {Label}", raw, labelName);
        return null;
    }

    private DateOnly? ParseDate(string raw, string labelName)
    {
        var normalized = raw.Replace("/", "").Replace("-", "");

        // ROC yyyMMdd (7 digits, e.g. 1140715 = 民國114年7月15日)
        if (normalized.Length == 7 && int.TryParse(normalized[..3], out var rocYear)
            && DateOnly.TryParseExact($"{rocYear + 1911}{normalized[3..]}", "yyyyMMdd", out var fromRoc))
            return fromRoc;

        // Gregorian yyyyMMdd fallback (8 digits)
        if (normalized.Length == 8 && DateOnly.TryParseExact(normalized, "yyyyMMdd", out var gregorian))
            return gregorian;

        _logger.LogWarning("No parseable due date in '{Raw}' for {Label}", raw, labelName);
        return null;
    }
}
