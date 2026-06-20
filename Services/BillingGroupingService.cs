using BillingNotificationService.Models;

namespace BillingNotificationService.Services;

public class BillingGroupingService
{
    public IReadOnlyList<PaymentGroup> GetGroups() =>
    [
        new("郵局", [BillingLabel.富邦信用卡,BillingLabel.中信信用卡,BillingLabel.玉山信用卡,BillingLabel.國泰信用卡]),
        new("台新Richart", [BillingLabel.台新信用卡,BillingLabel.華銀信用卡,BillingLabel.星展信用卡,BillingLabel.遠東信用卡]),
        new("聯邦帳戶末四碼XXXX", [BillingLabel.聯邦信用卡]),
    ];
}
