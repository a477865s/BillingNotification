using System.Text;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Messages;
using BillingNotificationService.Models;
using iText.Kernel.Pdf;

namespace BillingNotificationService.Services;

public class ClaudePdfParserService
{
    private static readonly Regex NumberPattern = new(@"\d[\d,]*(?:\.\d+)?");

    // Per-bank hints for non-standard field naming
    private static readonly Dictionary<BillingLabel, string> LabelHints = new()
    {
        [BillingLabel.遠東信用卡] = "此銀行的應繳全額欄位名稱為「本期應繳總金額」，請直接取該欄位的數值。",
    };


    private readonly AnthropicClient _claude;
    private readonly ILogger<ClaudePdfParserService> _logger;

    public ClaudePdfParserService(IConfiguration config, ILogger<ClaudePdfParserService> logger)
    {
        var apiKey = config["Anthropic:ApiKey"]
            ?? throw new InvalidOperationException("Anthropic:ApiKey is not configured in appsettings");
        _claude = new AnthropicClient { ApiKey = apiKey };
        _logger = logger;
    }

    public async Task<decimal?> ExtractAmountFromPdfAsync(byte[] pdfBytes, string password, BillingLabel label, CancellationToken ct = default)
    {
        // Decrypt first so Claude receives a readable (unencrypted) PDF
        byte[] cleanPdf;
        try
        {
            cleanPdf = StripPassword(pdfBytes, password);
            _logger.LogInformation("[{Label}] iText7 decrypted: {OrigBytes}B → {CleanBytes}B", label, pdfBytes.Length, cleanPdf.Length);
            if (cleanPdf.Length < 500)
                _logger.LogWarning("[{Label}] Decrypted PDF is suspiciously small — wrong password or unsupported encryption", label);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Label}] iText7 failed, sending raw bytes", label);
            cleanPdf = pdfBytes;
        }

        var base64 = Convert.ToBase64String(cleanPdf);
        LabelHints.TryGetValue(label, out var hint);
        var hintText = hint != null ? $" {hint}" : "";

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
                            new TextBlockParam
                            {
                                Text = $"這是台灣信用卡帳單PDF。請找出當月「應繳全額」（即全額繳清，非最低應繳）。欄位可能標示為：本期應繳總金額、應繳總金額、本期應繳金額、本期帳款、帳單金額、Total Amount Due 等。{hintText}只回傳阿拉伯數字，不含任何貨幣符號或逗號，例如：3456。若找不到請回傳 0。"
                            }
                        }
                    }
                ]
            }, ct);

            return ParseAmount(response, label);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Claude PDF call failed for {Label}", label);
            return null;
        }
    }

    public async Task<decimal?> ExtractAmountFromTextAsync(string text, BillingLabel label, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            var response = await _claude.Messages.Create(new MessageCreateParams
            {
                Model = Model.ClaudeOpus4_8,
                MaxTokens = 50,
                Messages =
                [
                    new()
                    {
                        Role = Role.User,
                        Content = $"以下是信用卡帳單文字：\n\n{text}\n\n請找出「應繳總金額」或「本期應繳金額」（不是最低應繳金額）。只回傳阿拉伯數字，不含任何貨幣符號或逗號，例如：3456。若找不到請回傳 0。"
                    }
                ]
            }, ct);

            return ParseAmount(response, label);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Claude text fallback failed for {Label}", label);
            return null;
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
        doc.Close(); // flushes to outputStream and closes reader + writer

        return outputStream.ToArray();
    }

    private decimal? ParseAmount(Message response, BillingLabel label)
    {
        var raw = response.Content
            .Select(b => b.Value)
            .OfType<TextBlock>()
            .Select(b => b.Text)
            .FirstOrDefault()
            ?.Trim();

        _logger.LogInformation("Claude [{Label}] => '{Raw}'", label, raw ?? "(no response)");

        if (raw == null)
        {
            _logger.LogWarning("Empty response from Claude for {Label}", label);
            return null;
        }

        // Direct parse — includes 0 (bill already paid this cycle)
        if (decimal.TryParse(raw.Replace(",", ""), out var direct) && direct >= 0)
            return direct;

        // Regex fallback if Claude added surrounding text (e.g. "3,456 元")
        var match = NumberPattern.Match(raw);
        if (match.Success && decimal.TryParse(match.Value.Replace(",", ""), out var extracted) && extracted >= 0)
            return extracted;

        _logger.LogWarning("No parseable amount in Claude response '{Raw}' for {Label}", raw, label);
        return null;
    }
}
