# Series Relay Launcher 開發者指南

[使用者指南](GUIDE.zh-TW.md) · [引擎版本清單](../Emulators/catalog.json) · [發行歷史](../Updates/history.json)

[English](DEVELOPMENT.md) · [简体中文](DEVELOPMENT.zh-CN.md) · 繁體中文 · [日本語](DEVELOPMENT.ja.md) · [한국어](DEVELOPMENT.ko.md)

本文對應啟動器 **v1.0.4**。Windows 桌面端負責執行個體、輸入配置和介面；Python 工作階段進程負責準備資源、啟動引擎、轉發日誌。偵錯某個建置前，先用發行清單中的專案 ID 和提交號定位實際執行的引擎。

| 要追蹤的問題 | 從這里進入 |
| --- | --- |
| 匯入校驗、執行個體與預設 | [GameImport.cs](../Source/GTA6Launcher/GameImport.cs)、[ImportCard.cs](../Source/GTA6Launcher/ImportCard.cs)、[GameLibrary.cs](../Source/GTA6Launcher/GameLibrary.cs) |
| 工作階段編排和預熱 | [DesktopLauncher.cs](../Source/GTA6Launcher/DesktopLauncher.cs)、[session.py](../Source/XCL-VIctory-Lab/desktop/session.py)、[warmup_job.py](../Source/XCL-VIctory-Lab/desktop/warmup_job.py) |
| 成就解析、歷史與顯示 | [AchievementJournal.cs](../Source/GTA6Launcher/AchievementJournal.cs)、[AchievementToast.cs](../Source/GTA6Launcher/AchievementToast.cs)、[AchievementTile.cs](../Source/GTA6Launcher/AchievementTile.cs) |
| 鍵鼠綁定與 HUD 資源 | [BindingWizard.cs](../Source/GTA6Launcher/BindingWizard.cs)、[GameKeyPrompts.cs](../Source/GTA6Launcher/GameKeyPrompts.cs)、[key_prompts.py](../Source/XCL-VIctory-Lab/desktop/key_prompts.py) |

## 從匯入來源到可啟動執行個體

同一次匯入有三個不同的完成條件。來源檢查回答“輸入指向什麼”，檔案校驗回答“本機資料和主程式是否匹配”，工作階段預載回答“當前引擎是否已準備接收啟動命令”。本機已解壓縮檔案可以在入庫前完成檔案校驗。歸檔和磁力連結會先入庫，再從大廳執行安裝。

| 輸入與階段 | 判定條件與輸出 | 發生故障時先看哪裡 |
| --- | --- | --- |
| 本機目錄，`Address` | 優先使用所選目錄或它的直接子目錄 `GameData` 中的 `MicrosoftGame.Config`。否則掃描同層 ZIP、7Z、RAR 或編號從 1 開始的分卷主檔案。多個主包交由用戶選擇。 | 區分目錄層級錯誤、清單不可讀、首卷缺失和分卷編號斷檔。 |
| 磁力連結，`Address` | 傳輸子進程讀取元資料。清單中出現 `MicrosoftGame.Config` 即走散裝本體路徑；只有支援的歸檔時記錄歸檔標記，留待解壓縮驗證。 | 60 秒元資料超時與 90 秒傳輸停滯是不同階段。 |
| EXE 來源，`Contents / External` | 自動查找沿清單里的相對路徑解析。獨立優化建置允許單獨存放與聲明檔案同名的程式。檔案校驗檢查 PE 簽名和 AMD64 機器類型，並檢驗四個必要 RPF 的頭部。 | 區分內容聲明、缺少 EXE、EXE 名不匹配和檔案格式不正確。主卷 EXE 屬於歸檔輸入。 |
| 歸檔，`Archive` | 普通解壓縮和釋放源卷都要目標目錄，釋放模式額外讀取確認。選擇變動使上次檢查失效。 | 缺少可寫輸出目錄、磁盤空間不足、源卷已刪除、解壓縮後沒有唯一的遊戲清單。 |
| 入庫，安裝，提交 | `InstallPlan` 分開保存本體與 EXE 的模式、來源和輸出，並保存必要的種子元資料。大廳安裝先處理單獨的 EXE，再處理本體，每個成功階段記錄輸出。最終校驗通過後以相同執行個體 ID 替換待安裝記錄。 | 檢查 `desktop/game-instances.xml` 的待安裝資訊、已生成的輸出目錄及當次日誌。正式安裝通知出現在成功提交後。 |

本機分卷的釋放依賴流的消費狀態。讀取下一卷時，前一卷只有完整消費後才可提前刪除。任務末尾核對輸出清單後再釋放剩余源卷。磁力歸檔會先下載檔案集再解壓縮。歸檔檢查成功只能證明當前階段具備所需輸入，後續仍可能發生解壓縮、檔案結構或 EXE 匹配錯誤。[來源狀態機](../Source/GTA6Launcher/ImportSource.cs) · [延遲安裝](../Source/GTA6Launcher/DeferredInstall.cs) · [本機歸檔](../Source/GTA6Launcher/Transfers/LocalImport.cs) · [磁力歸檔](../Source/GTA6Launcher/Transfers/MagnetArchives.cs)

## 工作階段協議與四類狀態通知

| 事件 | 生產者的證據 | 介面動作 |
| --- | --- | --- |
| `PRELOADING`、`STAGE` | 工作階段進入準備階段，後續階段名稱可能多次改變 | 處于預熱狀態時入隊「開始預載」，用階段消息更新按鈕詳情 |
| `COUNT`、`PROGRESS` | 前者是著色器數，後者是已處理與總管線數 | 只有後者可用於計算管線進度條 |
| `READY` | 引擎準備結束，可以接收 `PLAY` | 從預熱切到就緒時提示「預載完成」 |
| `DEVICE_READY`、`PLAYING` | D3D12 設備就緒與檢測到首幀分屬兩個階段 | 首幀帶來的 `PLAYING` 才觸發「遊戲啟動成功」，並把窗口 PID 交給成就浮層 |
| `EXIT`、`SHADER_ERROR`、`ERROR` | 遊戲退出、著色器準備失敗或其他工作階段錯誤 | 結束工作階段或展示錯誤，結合本輪日誌還原失敗階段 |

`LauncherNoticeKind` 的四個值為 `Installed`、`Preloading`、`Ready`、`Started`。隊列逐個顯示通知，單項生命周期約三秒。啟動成功通知優先定位遊戲所在顯示器，其他通知以啟動器工作區為基準。成就浮層位于同一顯示器時預留通知所占高度。`ResetSession` 清理已經失效的工作階段通知，保留已入隊的安裝完成通知。狀態通知不會寫入成就進度。[通知隊列和繪制](../Source/GTA6Launcher/LauncherNotice.cs)

## 成就事件如何變成本機記錄

**SR Achievement Event Monitoring System** 有兩條輸出路徑。實時路徑將事件送到桌面端的通知隊列；持久化路徑把同一條事件追加到本輪工作階段檔案，供大廳按執行個體重建歷史。

```text
引擎執行日誌
  └─ session.achievement_record → 歸一化事件
      ├─ LOG → AchievementToast.Session.Observe → 進度或解鎖通知
      └─ runs/<run>/achievements.log → AchievementJournal.ReadHistory → 大廳成就卡
```

### 采集與分類

`forward_runtime_logs` 將每行原始輸出送進 `achievement_record(raw, engine)`。解析器保留帶時區的原始時間戳，缺失時使用當前 UTC 時間，再附上引擎標記。對于 XUser 模組的 `local achievement update` 消息，只有 JSON 對象中的 `achievementId` 為 1–256 字符的字符串、`percentComplete` 為 0–100 的整數時才產生進度事件。布爾值、越界值和損壞的 JSON 會被丟棄。

| 歸一化事件 | 生產端的證據 | 消費端行為 |
| --- | --- | --- |
| `local-progress` | XUser 本地更新攜帶明確的 ID 和百分比 | 保留該 ID 的最高進度，100 計為完成 |
| `unlocked` | 有明確的成功解鎖語義，且可提取 ID | 以該 ID 的 100% 完成事件處理 |
| `query` | 成就服務的查詢請求，可帶 ID | 留作診斷 |
| `progress-update-request` | 請求訪問成就更新端點 | 留作診斷，等到有實際進度或解鎖證據才更改卡片 |

例如，下面是**合成的歸一化輸出**。它展示同一個 ID 的一次進度和一次完成，可用于核對解析器輸入。目錄中字符串 ID `"1"` 對應「仿若天堂」。

```text
2026-01-01T12:00:00Z [CGBJ achievement] local-progress {"achievementId":"1","percentComplete":42}
2026-01-01T12:01:00Z [CGBJ achievement] unlocked achievementId=1
```

### 歸屬、去重與顯示

1. **按執行個體歸集。** `ReadHistory(lab, instance)` 枚舉 `desktop/play-history/<instance>/Desktop-*.time`，用標記檔名定位 `runs/<run>/achievements.log`。執行個體 ID 決定遍歷哪些會話，即使兩個執行個體的展示名相同也可以分別回放。
2. **按 ID 合并。** `TryProgress` 識別已知引擎標記里的兩類有效事件。歷史取每個 ID 的最大百分比，完成時間取帶有效時區的最早記錄。同一時刻同時有進度 100 和明確解鎖時，保留明確解鎖的證據。
3. **把資料接到界面。** `Session.Observe` 用本地目錄篩選 ID，并將新百分比與歷史最高值比較。只有嚴格增長才入隊，同一條日誌重放不會重復彈窗。收到 `PLAYING` 后，`Attach(pid)` 用遊戲窗口定位顯示器工作區。完成事件的展示優先級高于普通進度。

目錄通過 [GtaViAchievements.tsv](../Source/GTA6Launcher/Assets/GtaViAchievements.tsv) 載入。每行 12 列，依次為 ID、點數、五種語言的名稱、同順序的五種描述。名稱和描述按簡中、英文、繁中、日文、韓文排列。新增條目時需同步圖標資源。排查“日誌有 ID，卡片沒變”時，先看事件種類，再核對執行個體標記、目錄 ID 和歷史最高值。

### 復現一次進度回退與重復解鎖

在測試輸入中對已知 ID 依次傳入查詢、42%、12%、明確解鎖、同一解鎖的重放。預期依次為無進度、42%、仍為 42%、100%、仍為 100%。最后一次不應再彈出解鎖通知。若原始日誌里已經有進度而大廳不變，沿原始 `runtime.log`、歸一化 `achievements.log`、執行個體的會話標記、TSV 中的 ID 和歷史最高值依次核查。舊引擎標記能被歷史解析器識別；當前引擎卡的“支援成就”由在售版本實際接入的后端決定。[引擎卡實現](../Source/GTA6Launcher/ProjectHoverCard.cs)

## 鍵鼠映射與 HUD 重繪

**輸入和顯示是兩條鏈路。** 鍵位能觸發動作時，輸入鏈路已生效。HUD 仍顯示手柄圖標時，應沿提示資源鏈路排查。

### 輸入配置與會話協議

[MappingSteps.cs](../Source/GTA6Launcher/MappingSteps.cs) 定義向導的 18 個手柄輸入項。[BindingSlots.cs](../Source/GTA6Launcher/BindingSlots.cs) 管理用戶配置槽及獨立的內置預設，[GtaViPreset.cs](../Source/GTA6Launcher/GtaViPreset.cs) 處理預設的步行、瞄準和駕駛上下文。預設中的 F6 切換步行與駕駛，同一物理鍵可以隨上下文改變輸出。

`CurrentPlayCommand` 在啟動時確定輸入模式。手柄使用 `PLAY`；CGBJ 自定義映射使用 `PLAY<TAB>KEYBOARD` 加 `PAD=`，其中按固定順序傳送前 16 項虛擬鍵碼，Share 和 Guide 在這組字段之外。內置方案使用 `PLAY<TAB>GTA6_PRESET`。這里的 `<TAB>` 表示單個制表符。可選字段 `PROMPTS=` 攜帶 Base64 編碼的 ASCII 動作表，解碼后的兩行示例如下。

```text
INPUT_AIM=RMB
INPUT_JUMP=Space
```

`GameKeyPrompts.Command` 從選中配置生成動作表。`decode_prompts` 校驗 Base64、字段名和鍵帽標簽，再生成供資源處理使用的綁定表。準備階段和啟動階段都核對同一份配置；更換配置后，桌面端會重新安排準備任務。

### 文字和矢量圖標怎樣重繪

v1.0.4 的啟用條件包括 **CGBJ**、遊戲清單中的 **TitleId `7952987A`**、鍵鼠模式、已配置的映射槽和已開啟的「顯示實際綁定按鍵」。準備發生在 `READY` 之前。

| 處理階段 | 實現和約束 |
| --- | --- |
| 操作名解析 | `GameKeyPrompts.WithPrompts` 將上下文動作映射成鍵帽標簽。例如步行的瞄準走 `INPUT_AIM`，駕駛的油門走獨立車輛動作，兩者可以復用手柄扳機。 |
| 幫助文字 | `replace_labels` 在支援的字符串池中替換已識別的操作標記，例如將瞄準提示替換為 `[RMB]`。僅在替換結果能容納于原條目時寫入，保持后續字符串的索引位置。 |
| HUD 圖標 | `replace_button_glyphs` 從 Scaleform 圖標庫中把匯出符號關聯到動作。外輪廓、內底和鍵帽字樣分別生成矢量層，復用相同輪廓。Enter 與 Space 各有獨立圖形。 |
| 會話資源 | `create_copy` 以唯讀方式打開源 `data.rpf`，確認能替換共享圖標庫后，將變更寫入會話副本。歸檔內的壓縮條目須適配原空間。缺少可用圖標或超出資源容量時，準備過程會錯誤訊息。 |

定位問題時，在本輪日誌中找 `Keyboard prompts:`，分別核對 `HUD button glyphs` 和 `text entries` 的數量。文字條目數可以為零；圖標數為零會使該次提示準備失敗。想復現“輸入正確但提示仍舊”的情況，可以分別測試關閉開關、切換引擎、準備完成后更換映射三條路徑。

## 渲染、快取和組件修復

`prepare_nvidia` 先載入隨引擎發布的 `preload.env`，再合并 F1 菜單中支援的設定，調用原生 `preload`。部分設定會改變著色器快取鍵，例如 early depth。`prewarm.log` 可區分著色器快取條目、無法翻譯條目、已知管線和寫入原生管線庫的進度。快取已有數萬條著色器時，下一輪仍可能有管線工作。比較兩次耗時要使用同一執行個體資料、相同引擎提交和渲染選項，分別比較各階段，以免把掃描時間當成重新編譯。

渲染頁按探測結果、控制臺檔位、超分狀態與幀生成方式計算可選倍率；改變設定后會將已選倍率限制在有效范圍。VIctory-NVIDIA 的適配層更新 F1 菜單實際讀取的配置項，并保留無關設定。切換引擎后先檢查當前專案和實際寫入檔案，再診斷畫面差異。[渲染頁](../Source/GTA6Launcher/RenderSettings.cs) · [菜單適配層](../Source/GTA6Launcher/VictoryNvidiaMenu.cs)

組件索引按組記錄目標相對路徑、長度和 SHA-256。組件檢測可從 `Runtime/Components/Library.zip` 的同摘要條目或嵌入的恢復資源取得內容。Python 執行時會實際匯入所需模組，傳輸工具執行自檢。已安裝遊戲還會走遊戲檔案校驗；用戶缺失的遊戲資源要從其原來源補齊。索引與離線庫要來自同一發行構建。[修復邏輯](../Source/GTA6Launcher/ComponentLibrary.cs) · [組件自檢](../Source/GTA6Launcher/ComponentSettings.cs)

## 執行個體、存檔和日誌的資料邊界

| 位置 | 生命周期 | 常見排查點 |
| --- | --- | --- |
| `desktop/game-instances.xml`（相對實驗室目錄） | 執行個體 ID、檔案引用、配置、媒體和待安裝計劃。寫臨時檔案后替換 | 重命名不應產生新 ID。延遲安裝完成后繼續使用原 ID。 |
| `GameSaves/<instance>` | 當前機器的存檔槽，XCL 另有自己的布局。匯入 ZIP 先進入暫存目錄 | 切換存檔要在遊戲退出后進行。成就回放依據執行個體的會話標記。 |
| `Logs/Launcher-*.log` | 啟動器級日誌，逐行帶時間和來源。匯出時復制當下的有效長度 | 調試多個引擎時，保留出錯時刻、執行個體和引擎 ID。 |
| `runs/<run>/` | 一輪會話的原始日誌、預熱日誌和成就日誌 | 圖形準備問題先看 `prewarm.log`，成就問題要對照原始和歸一化日誌。 |

[存檔庫](../Source/GTA6Launcher/LocalSaveLibrary.cs) · [XCL 存檔庫](../Source/GTA6Launcher/XclSaveLibrary.cs) · [日誌匯出](../Source/GTA6Launcher/SessionLog.cs)

## 構建與回歸測試

Windows x64 構建環境需要 .NET 9 SDK、.NET Framework 4.8 和 4.6.2 targeting pack，以及 Python 3.11 或更高版本。發行包版本應與源碼匹配。將全量 ZIP 放入 `Updates` 后，在儲存庫根目錄執行以下命令。第一步按儲存庫中的 SHA-256 校驗包，再恢復離線組件和引擎輸入。

```powershell
py -3 prepare_inputs.py Updates/Launcher-1.0.4.zip --portable
dotnet build Source/GTA6Launcher/GTA6Launcher.csproj -c Release
py -3 -m unittest discover -s Source/GTA6Launcher/Tools/tests -p test_cgbj_log.py
py -3 Source/GTA6Launcher/Tools/tests/test_import_archive.py
powershell -NoProfile -File Source/GTA6Launcher/Tools/tests/test_launcher_update.ps1 -Helper Source/GTA6Launcher/Updater/bin/Release/net462/Palmline.Update.exe
```

只構建桌面端時，`prepare_inputs.py` 可省略 `--portable`。完整的會話測試使用引擎執行目錄。測試覆蓋成就查詢與解鎖分類、歷史進度合并、鍵帽字形、字符串池位置以及預載后配置失效等情況。浮層可用 Release 的 net48 構建和 Windows PowerShell 的 STA 模式單獨檢查。[會話測試](../Source/GTA6Launcher/Tools/tests/test_cgbj_log.py) · [浮層驗證腳本](../Source/GTA6Launcher/Tools/tests/verify_achievement_overlay.ps1)

## 發布與版本檢查

1. 遞增啟動器和日誌控制臺的版本。在 [history.json](../Updates/history.json) 追加版本號、`normal` 或 `critical` 及用戶能讀懂的變更說明。
2. 恢復可攜版輸入后，在空輸出目錄制作完整發行包。命令為 `py -3 Source/GTA6Launcher/Tools/package_portable.py --runtime-from Source/Runtime --output-dir Build`。檔名用 `Launcher-X.Y.Z.zip`，校驗檔案用 ASCII，內容為小寫 SHA-256、兩個空格和檔名。同步包內 `Sources/Launcher.zip` 中的 `GTA6Launcher/Assets/ComponentIndex.tsv`。
3. 將全量 ZIP、校驗檔案、同一構建的升級工具及配置放入 `Updates`。升級工具的發布名稱為 `Upgrade-Launcher.exe` 和 `Upgrade-Launcher.exe.config`。執行 `py -3 prepare_release_assets.py` 生成模擬器獨立包、清單及發行說明，再執行 `py -3 verify_release.py` 檢查包完整性和用戶資料隔離。
4. 在官方儲存庫創建對應標簽的 GitHub Release，以生成的 [RELEASE.md](../Updates/RELEASE.md) 為正文，保留末尾的完整歷史和最低支援版本標記。上傳對應版本的啟動器全量 ZIP、模擬器 ZIP、兩者的 SHA-256 及離線升級工具和配置，并將發行版設為 Latest。Git 中保留源碼、清單和校驗檔案，大型二進制隨 Release 分發。

客戶端按版本號篩選「本地版本之后、目標版本及之前」的記錄，分別統計普通與關鍵更新。1.0.1 → 1.0.3 會跨過一次關鍵更新和一次普通更新，1.0.2 → 1.0.3 只有一次普通更新。兩種路徑使用同一份最新全量包。[更新器實現](../Source/GTA6Launcher/LauncherUpdate.cs) · [熱更新清單](../Updates/latest.json)

### 舊版離線升級

驗證 1.0.4 發布時應覆蓋三組版本。1.0.1 到 1.0.4 跨過一個關鍵更新和兩個普通更新，1.0.3 到 1.0.4 只有一個普通更新，1.0.4 自查應為當前版本。在線更新器校驗資產來源、摘要和 ZIP 根目錄，暫存受管檔案，在替換前寫入事務日誌。失敗時使用回滾副本恢復。可先用臨時舊版安裝及測試存檔驗證交接與用戶資料保留，再檢查公開 Latest 返回值。[發行解析](../Source/GTA6Launcher/Updater/ReleasePackage.cs) · [事務安裝](../Source/GTA6Launcher/Updater/PackageInstall.cs)

對于 1.0.0 和 1.0.1 可攜版，先關閉遊戲與啟動器。將全量 ZIP、對應的 `.sha256`、`Upgrade-Launcher.exe` 及其 `.config` 放在同一目錄，執行升級工具并指定舊 `Launcher.exe` 所在目錄。工具校驗摘要后就地升級，保留本地遊戲、存檔和配置。升級完成后使用 `SRLauncher.exe` 啟動。
