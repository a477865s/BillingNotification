using BillingNotificationService.Enums;

namespace BillingNotificationService.Models;

public record BillingRecord(
    string MessageId,
    string Subject,
    string From,
    DateTime Date,
    decimal Amount,
    BillingLabel Label,
    string Currency = "TWD",
    DateOnly? DueDate = null
);
