# BillingNotificationService

每個月都要手動查各張信用卡帳單、算出要轉多少錢到哪個帳戶，煩了，所以寫了這個。

## 這個服務在做什麼

自動掃 Gmail 裡的帳單信件，用 AI 解析金額，信用卡依付款帳戶分組推播給自己，水電瓦斯帳單則另外推給家人。

```
Gmail 收到帳單信 → 掃描 PDF/信件內容 → Claude AI 解析金額與繳費期限 → 分組 → LINE 推播
```

也可以直接傳訊息給 LINE Bot 觸發：

> 你：帳單  
> Bot：📊 掃描中，請稍候...  
> Bot：（帳單分組結果）

---

## 功能

- **自動排程**：每月 1 日、15 日 09:00 各掃一次上個月的帳單並推播（15 日那次重掃同一個月，避免漏抓晚到的帳單信），排程日可調整
- **手動觸發**：LINE 傳「信用卡」、「帳單」、「掃描」、「scan」任一關鍵字即觸發，結果只回覆給發話的人，不會廣播給其他人
- **PDF 解密**：iText7 處理有密碼保護的帳單 PDF
- **AI 解析**：Claude AI 讀 PDF 或信件內文，萃取應繳金額與繳費期限
- **分組匯款**：信用卡依付款帳戶分組，直接告訴你每個帳戶要轉多少
- **居家費用通知**：水電瓦斯帳單另外推給家人，含金額與繳費期限

### 信用卡分組邏輯

| 帳戶 | 信用卡 |
|------|--------|
| 郵局 | 富邦、中信、玉山、國泰 |
| 台新 Richart | 台新、華銀、星展、遠東 |
| 聯邦 | 聯邦 |

### 居家費用（推給家人）

| 類型 | 週期 |
|------|------|
| 台水帳單 | 單月 |
| 台電帳單 | 單月 |
| 新海瓦斯帳單 | 雙月 |

---

## 技術架構

- **Runtime**：ASP.NET Core 8 Minimal API + BackgroundService
- **Gmail 掃描**：Google Gmail API（OAuth 2.0）
- **PDF 解析**：iText7 解密 + Claude API（claude-haiku-4-5）讀取
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

### 擴充水電瓦斯帳單

信用卡都搞定了之後，想說水電費也是每個月要繳，而且帳單也是寄 Email，乾脆一起掃。

幾個跟信用卡不同的地方：

**沒有 PDF 的不用加 `filename:pdf` 篩選器**。信用卡帳單都是 PDF 附件，但水電費的帳單有時候只有 HTML 信件內文。原本 Gmail query 一律加 `filename:pdf`，後來改成依帳單類型決定是否加，水電瓦斯就直接掃信件全文。

**需要同時抓繳費期限**。信用卡只要金額，但水電費繳費有截止日，忘了繳要加滯納金。於是 prompt 改成請 Claude 回傳兩行：第一行金額、第二行繳費期限（民國年七位數），`ParseDate` 再把民國年 +1911 轉成西元存進 `DateOnly`。

**瓦斯是雙月帳單**。奇數月才有帳單，偶數月沒有。不需要特別處理，掃不到 email 就不會有紀錄，LINE 也就不會顯示，自然解決。

### 居家費用獨立推播

水電瓦斯帳單可能由不同的人負責繳費，不一定需要知道信用卡消費明細。加一個 `FamilyUserId` 設定，掃完之後如果有居家費用紀錄，就另外 push 一則只包含水電瓦斯的訊息給指定的 LINE 使用者。

取得對方的 LINE User ID：讓對方傳任意訊息給 Bot，從 log 就能看到 `LINE User ID: Uxxxxx`，填入設定即可。不填則不推送。

### Google OAuth token 七天過期

上線沒幾天 Gmail 掃描就失敗了，log 顯示 `invalid_grant`。原因是 Google Cloud 專案的 OAuth 同意畫面發布狀態是「測試中」，這個狀態下 refresh token 只有七天效期。

NAS 是 headless 環境，container 裡面跑不了瀏覽器，沒辦法直接重新授權。解法：

1. 本機重新跑一次 OAuth 流程（會開瀏覽器），產生新的 `token_store`
2. 用 File Station 把 `token_store` 資料夾覆蓋到 NAS 上
3. `docker restart` 讓 container 重新讀 token
4. Google Cloud Console → 目標對象 → 發布狀態改成「實際運作中」，之後 token 就不會再過期了

### 換到 MacBook Pro 開發

原本都在 Windows 上開發，換了新的 MacBook Pro 之後遇到幾個環境問題：

**`dotnet` 指令找不到**：裝完 .NET SDK 沒有自動加進 PATH，要自己在 `~/.zshrc` 補：

```bash
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$PATH:$HOME/.dotnet:$HOME/.dotnet/tools"
```

**Docker image 架構不對**：MacBook Pro（Apple Silicon）是 arm64，但 NAS 是 x86_64。原本 Windows 上直接 `docker build` 沒問題，換 Mac 之後要加 `--platform linux/amd64`，不然 image 搬到 NAS 上會跑不起來。

**Container Manager 誤報「意外停止」**：透過 SSH 下 `docker compose down && up -d` 重新部署時，因為不是從 Container Manager 介面操作，它的監控機制會把這次重啟當成「意外停止」跳通知，其實只是正常部署流程，不是真的 crash。

**`.DS_Store`**：macOS Finder 幫每個資料夾產生的中繼資料檔案，跟專案無關，加進 `.gitignore` 忽略掉。

### 手動查詢只回覆給自己

原本不管是排程還是手動在 LINE 問 Bot，結果都會廣播給自己跟家人。但平常想隨口問一下這個月刷多少，其實不需要驚動家人。改成：

- **排程**（每月 1、15 日）：維持原本廣播給 `Line:UserId` + `Line:FamilyUserId`
- **手動關鍵字觸發**：只回覆給發話的那個人（從 LINE webhook event 的 `source.userId` 取得），而且不會更新 `last_run.json`，不會影響排程的補跑判斷

### 排程改成一個月兩次

原本一個月只在 1 號掃一次上個月的帳單，但有些銀行信件會晚到，1 號那次可能會漏掉。改成 1 號、15 號都重掃同一個月，15 號那次算是補漏網之魚。排程日可以用 `BillingNotification:ScheduleDaysOfMonth`（預設 `[1, 15]`）搭配 `BillingNotification:ScheduleHour`（預設 `9`）調整。

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
      "富邦信用卡": "密碼",
      "新海瓦斯帳單": "密碼"
    }
  },
  "Line": {
    "ChannelId": "你的 ChannelId",
    "ChannelSecret": "你的 ChannelSecret",
    "ChannelAccessToken": "你的 AccessToken",
    "UserId": "",
    "FamilyUserId": ""
  },
  "Anthropic": {
    "ApiKey": "sk-ant-..."
  }
}
```

PDF 密碼通常是身分證字號、後四碼、或生日，各家銀行不同，自己試。水電費帳單如果有加密也是一樣。`FamilyUserId` 取得方式：讓對方傳訊息給 Bot，從 log 取得 User ID 後填入（留空則不推送）。

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
    "UserId": "你的 UserId",
    "FamilyUserId": "你家人的 UserId"
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

**1. `Enums/BillingLabel.cs` — 加入新的 Label**

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

Windows：

```powershell
cd "C:\Users\lin\Desktop\SideProject\BillingNotificationService"
docker build -t billing-notification-service:latest .
docker save billing-notification-service:latest -o billing-service.tar
```

macOS（Apple Silicon 要加 `--platform`，NAS 是 x86_64，架構不一致 image 會跑不起來）：

```bash
cd ~/Desktop/SideProject/BillingNotification
docker build --platform linux/amd64 -t billing-notification-service:latest .
docker save billing-notification-service:latest -o billing-service.tar
```

### 第二步：上傳到 NAS

用 FileStation 把 `billing-service.tar` 上傳到 `/docker/billing-service/`，覆蓋舊檔。有開 SSH 的話也可以直接 `scp`：

```bash
scp -P <NAS SSH port> billing-service.tar <帳號>@<NAS位址>:/volume1/docker/billing-service/
```

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
