# BillingNotificationService

每個月都要手動查各張信用卡帳單、算出要轉多少錢到哪個帳戶，煩了，所以寫了這個。

## 這個服務在做什麼

自動掃 Gmail 裡的信用卡帳單 PDF，用 AI 解析金額，依付款帳戶分組後推送 LINE 通知。

```
Gmail 收到帳單信 → 掃描 PDF → Claude AI 解析金額 → 分組 → LINE 推播
```

也可以直接傳訊息給 LINE Bot 觸發：

> 你：信用卡  
> Bot：📊 掃描中，請稍候...  
> Bot：（帳單分組結果）

---

## 功能

- **自動排程**：每月 1 日 09:00 自動掃上個月的帳單並推 LINE
- **手動觸發**：LINE 傳「信用卡」、「帳單」、「掃描」、「scan」任一關鍵字即觸發
- **PDF 解密**：iText7 處理有密碼保護的帳單 PDF
- **AI 解析**：Claude API 讀 PDF 內容萃取應繳金額
- **分組匯款**：依付款帳戶分組，直接告訴你每個帳戶要轉多少

### 分組邏輯

| 帳戶 | 信用卡 |
|------|--------|
| 郵局 | 富邦、中信、玉山、國泰 |
| 台新 Richart | 台新、華銀、星展、遠東 |
| 聯邦 | 聯邦 |

---

## 技術架構

- **Runtime**：ASP.NET Core 8 Minimal API + BackgroundService
- **Gmail 掃描**：Google Gmail API（OAuth 2.0）
- **PDF 解析**：iText7 解密 + Claude API（claude-opus-4-8）讀取
- **通知**：LINE Messaging API（Push + Reply）
- **部署**：Docker on Synology DS423+ NAS
- **公開 URL**：Tailscale Funnel（`https://<your-nas>.ts.net`）

---

## 心路歷程

### 起點

每個月月底到月初，要手動打開各家銀行 App 或信件查帳單金額，再算出郵局轉多少、台新轉多少，很麻煩。有時候還會漏看，導致忘記繳。

想說既然 Gmail 都收得到帳單，應該可以自動化。

### 第一個卡關：PDF 有密碼

各家銀行的帳單 PDF 都有密碼保護（通常是身分證後四碼或生日），直接讀讀不到。用 iText7 加上已知密碼格式解密後才能繼續。

### 第二個卡關：每家格式不一樣

富邦、台新、中信、遠東各家帳單版面完全不同。原本想用 regex 硬解，但格式差太多。後來改用 Claude API 直接讀 PDF 文字，請它「找出這張帳單的應繳金額」，效果好很多，也不需要針對每家銀行各別維護解析邏輯。

### 第三個卡關：`parsedAmount: 0` 的語意

帳款 0 元跟「找不到金額」是兩回事。前者是帳款已繳清（正常），後者是解析失敗（要警告）。最後用 `int?` 區分：`0` = 已繳清，`null` = 找不到。

### 部署到 NAS

本機開發完後想讓它 24 小時跑，剛好手邊有 Synology DS423+ 就拿來用。

遇到的問題：
- `token_store/` 和 `data/` 資料夾權限不足，container 寫不進去 → `chmod 777` 解決
- `docker restart` 不會載入新 image，要用 `docker compose down && up -d` 才能更新

### LINE Webhook 的公開 URL

LINE webhook 要求 HTTPS，家裡網路 port 443 被 ISP 擋，也沒有自己的 domain，最後用 Tailscale Funnel 解決——不需要開 port，不需要 domain，NAS 主動建 tunnel，直接拿到一個固定的 HTTPS URL。

---

## 前置條件

| 工具 | 用途 |
|------|------|
| .NET 8 SDK | 本機開發 / build |
| Docker Desktop | 本機 build image |
| Google Cloud Console 專案 | Gmail API 授權 |
| LINE Developers 帳號 | Messaging API |
| Anthropic API Key | Claude PDF 解析 |

---

## 從零開始設定

### 1. Google Cloud Console（Gmail API）

1. 前往 Google Cloud Console，建立新專案
2. 啟用 **Gmail API**
3. 建立 **OAuth 2.0 用戶端憑證**（類型選「桌面應用程式」）
4. 下載 JSON 檔，重新命名為 `credentials.json`，放到專案根目錄

### 2. LINE Developers（Messaging API）

1. 前往 LINE Developers Console，建立 Provider 和 Channel（類型：Messaging API）
2. 從 **Basic settings** 取得：
   - `ChannelId`
   - `ChannelSecret`
3. 從 **Messaging API** 取得：
   - `ChannelAccessToken`（點 Issue 產生）
4. `UserId` 先留空，第一次啟動服務後傳任意訊息給 Bot，從 log 取得

### 3. 設定檔

建立 `appsettings.Development.json`（不進 git）：

```json
{
  "Gmail": {
    "PdfPasswords": {
      "中信信用卡": "密碼",
      "台新信用卡": "密碼",
      "富邦信用卡": "密碼"
    }
  },
  "Line": {
    "ChannelId": "你的 ChannelId",
    "ChannelSecret": "你的 ChannelSecret",
    "ChannelAccessToken": "你的 AccessToken",
    "UserId": ""
  },
  "Anthropic": {
    "ApiKey": "sk-ant-..."
  }
}
```

PDF 密碼通常是身分證字號、後四碼、或生日，各家銀行不同，自己試。

### 4. 第一次 Gmail OAuth 授權

服務啟動後，瀏覽器打開：

```
http://localhost:5240/api/auth/gmail
```

會跳出 Google 授權畫面，完成後會在專案目錄產生 `token_store/`，之後就不用再授權了。

> 注意：`token_store/` 不進 git，但要跟著 `credentials.json` 一起上傳到 NAS，否則 NAS 上的 container 會找不到授權。

### 5. 取得 LINE UserId

1. 啟動服務
2. 用 LINE 傳任意訊息給你的 Bot
3. 看 log：`Your LINE User ID: Uxxxxx`
4. 把這個 ID 填到設定檔的 `UserId` 欄位

### 6. NAS 上的敏感設定

NAS 用 `appsettings.Production.json`（放在 `/volume1/docker/billing-service/`）：

```json
{
  "Gmail": {
    "PdfPasswords": {
      "中信信用卡": "密碼",
      "台新信用卡": "密碼"
    }
  },
  "Line": {
    "ChannelId": "你的 ChannelId",
    "ChannelSecret": "你的 ChannelSecret",
    "ChannelAccessToken": "你的 AccessToken",
    "UserId": "你的 UserId"
  },
  "Anthropic": {
    "ApiKey": "sk-ant-..."
  },
  "BillingNotification": {
    "ScheduleDayOfMonth": 1,
    "ScheduleHour": 9,
    "LastRunFile": "/app/data/last_run.json"
  }
}
```

---

## 新增信用卡

假設要新增「玉山商務卡」，需要改三個地方：

**1. `Models/BillingLabel.cs` — 加入新的 Label**

```csharp
public enum BillingLabel
{
    // 現有的...
    玉山商務信用卡,  // 新增
}
```

**2. `Services/GmailScannerService.cs` — 設定 Gmail 標籤對應**

找到 Label → Gmail 標籤名稱的對應，加入新卡的 Gmail 標籤名稱。

**3. `Services/BillingGroupingService.cs` — 加入付款分組**

```csharp
new PaymentGroup("郵局", new[]
{
    BillingLabel.富邦信用卡,
    BillingLabel.玉山商務信用卡,  // 新增到對應帳戶
})
```

**4. `appsettings.Development.json` — 加入 PDF 密碼**

```json
"Gmail": {
  "PdfPasswords": {
    "玉山商務信用卡": "密碼"
  }
}
```

---

## 推版流程（更新程式後）

### 第一步：本機 build

```powershell
cd "C:\Users\lin\Desktop\SideProject\BillingNotificationService"
docker build -t billing-notification-service:latest .
docker save billing-notification-service:latest -o billing-service.tar
```

### 第二步：上傳到 NAS

用 FileStation 把 `billing-service.tar` 上傳到 `/docker/billing-service/`，覆蓋舊檔。

### 第三步：NAS load 新 image

```bash
sudo docker load -i /volume1/docker/billing-service/billing-service.tar
```

### 第四步：重建 container

```bash
sudo docker compose -f /volume1/docker/billing-service/docker-compose.yml down && \
sudo docker compose -f /volume1/docker/billing-service/docker-compose.yml up -d
```

---

## NAS 上的檔案結構

```
/volume1/docker/billing-service/
├── billing-service.tar              # Docker image
├── docker-compose.yml
├── credentials.json                 # Google OAuth 憑證
├── appsettings.Production.json      # 敏感設定（不進 git）
├── token_store/                     # Gmail OAuth token（chmod 777）
│   └── Google.Apis.Auth.OAuth2.Responses.TokenResponse-user
└── data/                            # 執行狀態（chmod 777）
    └── last_run.json
```

---

## API

| Method | Path | 說明 |
|--------|------|------|
| GET | `/api/auth/gmail` | Gmail OAuth 授權（初次設定用） |
| GET | `/api/billing/emails?year=2026&month=6` | 預覽帳單明細 |
| POST | `/api/billing/scan?year=2026&month=6` | 手動觸發掃描 |
| POST | `/api/line/webhook` | LINE Webhook 接收端 |
