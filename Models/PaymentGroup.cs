using BillingNotificationService.Enums;

namespace BillingNotificationService.Models;

public record PaymentGroup(string AccountDescription, IReadOnlyList<BillingLabel> Labels);

public record PaymentGroupSummary(string Account, decimal Total, IReadOnlyList<BillingRecord> Cards);
