using BillingNotificationService.Enums;

namespace BillingNotificationService.Models;

public record EmailPreview(
    string MessageId,
    string Subject,
    string From,
    DateTime Date,
    decimal? ParsedAmount,
    string BodySnippet,
    BillingLabel Label
);
