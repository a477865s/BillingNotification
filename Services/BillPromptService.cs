using BillingNotificationService.Enums;

namespace BillingNotificationService.Services;

public class BillPromptService
{
    private static readonly HashSet<BillingLabel> UtilityLabels =
    [
        BillingLabel.台水帳單,
        BillingLabel.台電帳單,
        BillingLabel.新海瓦斯帳單,
    ];

    public bool IsUtility(BillingLabel label) => UtilityLabels.Contains(label);

    public string GetPdfPrompt(BillingLabel label) =>
        IsUtility(label) ? BuildUtilityPdfPrompt(label) : BuildCreditCardPdfPrompt(label);

    public string GetTextPrompt(BillingLabel label) =>
        IsUtility(label) ? BuildUtilityTextPrompt(label) : BuildCreditCardTextPrompt();

    private static string BuildCreditCardPdfPrompt(BillingLabel label)
    {
        var hint = label switch
        {
            BillingLabel.遠東信用卡 => " 此銀行的應繳全額欄位名稱為「本期應繳總金額」，請直接取該欄位的數值。",
            _ => ""
        };
        return $"這是台灣信用卡帳單PDF。請找出當月「應繳全額」（即全額繳清，非最低應繳）。欄位可能標示為：本期應繳總金額、應繳總金額、本期應繳金額、本期帳款、帳單金額、Total Amount Due 等。{hint}只回傳阿拉伯數字，不含任何貨幣符號或逗號，例如：3456。若找不到請回傳 0。";
    }

    private static string BuildCreditCardTextPrompt() =>
        "請找出「應繳總金額」或「本期應繳金額」（不是最低應繳金額）。只回傳阿拉伯數字，不含任何貨幣符號或逗號，例如：3456。若找不到請回傳 0。";

    private static string BuildUtilityPdfPrompt(BillingLabel label)
    {
        var billType = label switch
        {
            BillingLabel.台電帳單 => "電費",
            BillingLabel.台水帳單 => "水費",
            BillingLabel.新海瓦斯帳單 => "瓦斯費",
            _ => "費用"
        };
        return $"這是台灣{billType}帳單PDF。請找出：(1) 本期應繳金額；(2) 繳費期限。只回傳兩行：第一行為應繳金額（阿拉伯數字），第二行為繳費期限（民國年yyyMMdd格式，7位數）。若任一欄位找不到，該行請回傳 0。範例：\n1230\n1140715";
    }

    private static string BuildUtilityTextPrompt(BillingLabel label)
    {
        var billType = label switch
        {
            BillingLabel.台電帳單 => "電費",
            BillingLabel.台水帳單 => "水費",
            BillingLabel.新海瓦斯帳單 => "瓦斯費",
            _ => "費用"
        };
        return $"請找出：(1) 應繳金額或本期{billType}；(2) 繳費期限。只回傳兩行：第一行為金額（阿拉伯數字），第二行為繳費期限（民國年yyyMMdd格式，7位數）。若任一欄位找不到，該行請回傳 0。";
    }
}
