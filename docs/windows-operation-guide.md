# Windows 影子运行操作手册

本文是 Windows 上采集 Discard Advisor 有效验证证据的完整流程。当前
发布 cohort 为：插件 `0.4.13`、规则集 `0.3.4`、HDT `1.53.11`、炉石
构建 `247416`。

在诊断日志出现 `gate_decision` 且状态为 `Enabled` 之前，不要开始累计
5 局 Shadow 对局。

## 1. 准备环境

安装以下软件：

- Windows 10 或更高版本，64 位。
- Hearthstone Deck Tracker (HDT) `v1.53.11`。
- .NET 8 SDK。
- PowerShell 5.1 或更高版本。

确认 .NET SDK 可用：

```powershell
dotnet --info
```

插件按 HDT `v1.53.11` 编译。安装或更新插件前必须退出 HDT。

## 2. 获取最新项目代码

如果项目是 Git 克隆，在项目目录执行：

```powershell
git pull origin master
```

如果项目来自 GitHub ZIP，请重新下载最新 `master` 压缩包并解压到新
目录。ZIP 解压目录没有 Git 历史，不能执行 `git pull`。

以下命令以 `$repo` 表示项目目录，请按实际解压位置调整：

```powershell
$repo = "D:\downloads\brave\121909-hearthstone_helper-master\121909-hearthstone_helper-master"
Set-Location $repo
```

仅对当前 PowerShell 窗口允许运行项目脚本：

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
```

若 ZIP 文件被 Windows 标记为来自 Internet，在确认目录正确后解除阻止：

```powershell
Get-ChildItem $repo -Recurse -File | Unblock-File
```

## 3. 新建版本 Cohort

进度检查只统计当前 `0.4.13 / 0.3.4` cohort；旧版 `0.4.4` 至 `0.4.12`
的诊断数据会自动忽略，不会阻止新 5 局的计数。无需为了开始新 cohort
移动旧目录。

若要整理旧证据，先完全退出 HDT 后再执行以下可选归档命令：

```powershell
$hdtData = "$env:APPDATA\HearthstoneDeckTracker"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"

if (Test-Path "$hdtData\DiscardAdvisor") {
    Move-Item "$hdtData\DiscardAdvisor" "$hdtData\DiscardAdvisor-pre-0.4.13-$stamp"
}
```

不需要移动其他 HDT 数据。旧 `Replays` 可以保留，但最终归档时只应包含
本 cohort 新采集的回放。

## 4. 构建并安装 Shadow 插件

在 `$repo` 中下载固定的 HDT 引用并安装插件：

```powershell
.\scripts\bootstrap-hdt-reference.ps1
.\scripts\install-shadow-plugin.ps1
```

第一个脚本会将 HDT 引用下载到：

```text
<项目目录>\.artifacts\hdt\1.53.11\Hearthstone Deck Tracker
```

若已经在其他位置安装了 HDT `v1.53.11`，直接传入包含
`Hearthstone Deck Tracker.exe` 的目录：

```powershell
.\scripts\install-shadow-plugin.ps1 `
  -HdtReferenceDir "C:\Path\To\Hearthstone Deck Tracker"
```

若使用便携式 HDT，同时传入其数据目录和插件目录：

```powershell
.\scripts\install-shadow-plugin.ps1 `
  -HdtReferenceDir "C:\Path\To\Hearthstone Deck Tracker" `
  -PluginDirectory "C:\Path\To\HDTData\Plugins\DiscardAdvisor" `
  -HdtDataDirectory "C:\Path\To\HDTData"
```

安装器会写入 `mode: shadow` 的 `settings.json`。不要手动改为
`experimental`。

## 5. 在 HDT 中启用插件

1. 启动 HDT。
2. 打开 `Options > Tracker > Plugins`。
3. 启用 `Discard Advisor`。
4. 保持 `shadow` 模式。

Shadow 模式会计算建议并记录诊断数据，但不会显示推荐 Overlay。

## 6. 使用精确目标卡组

门禁只接受下列精确的 30 张狂野弃牌术。莫瑞甘的灵界当前为 2 费；镀银
魔像必须使用当前重印版 `WON_098`，而不是旧版 `KAR_205`。

```text
AAEBAf0GBM4Hj4ID8aEG9qEGDbW5A9XRA9DhA5iSBauSBZXKBteXB4SZB6StB8ayB9a+B9m+B8+/BwA=
```

```text
2x 咒怨之墓
2x 续连熵能
2x 派对邪犬
2x 栉龙
1x 灵魂之火
1x 莫瑞甘的灵界
2x 邪恶低语
2x 骨网之卵
2x 助祭耗材
1x 维希度斯的窟穴
2x 魔眼秘术师
1x 镀银魔像
2x 行尸
2x 时空之爪
2x 地狱公爵
2x 灵魂弹幕
2x 古尔丹之手
```

只能在 `RANKED_WILD` 中使用该卡组。替换任意一张牌都会得到
`DeckMismatch`，不会生成可计数的 Snapshot。

## 7. 首局验证

先完成一局真实狂野天梯，再检查文件和日志：

```powershell
Get-ChildItem "$env:APPDATA\HearthstoneDeckTracker\Replays" -Filter *.hdtreplay

Get-ChildItem "$env:APPDATA\HearthstoneDeckTracker\DiscardAdvisor\Fixtures" -Filter *.snapshot.json

Get-Content "$env:APPDATA\HearthstoneDeckTracker\DiscardAdvisor\Diagnostics\discard-advisor.jsonl" -Tail 50
```

正确结果应满足：

- 完成的对局生成一个 HDT `*.hdtreplay` 文件。
- 支持的局面生成一个或多个 `*.snapshot.json` 文件。
- 诊断日志包含状态为 `Enabled` 的 `gate_decision`。
- 完整对局中至少有一次 disposition 为 `Published` 的分析，才会计入
  5 局目标。

对局外出现 `UnsupportedMode` 是正常的。真实对局中出现
`DeckMismatch`、`UnsupportedPatch`、`UnsupportedHdtVersion`、
`UnsupportedCardDefinitions` 或 `UnsupportedHearthDb` 时，立即停止采集
并按第 14 节排查。

## 8. 收集 Shadow 对局

使用精确卡组完成至少 5 局真实 `RANKED_WILD` 对局，并保留回放、Fixture
和诊断日志。默认位置如下：

```text
%APPDATA%\HearthstoneDeckTracker\Replays\*.hdtreplay
%APPDATA%\HearthstoneDeckTracker\DiscardAdvisor\Fixtures\*.snapshot.json
%APPDATA%\HearthstoneDeckTracker\DiscardAdvisor\Diagnostics\discard-advisor.jsonl*
```

定期检查进度：

```powershell
.\scripts\check-shadow-progress.ps1 `
  -InputPath `
    "$env:APPDATA\HearthstoneDeckTracker\Replays", `
    "$env:APPDATA\HearthstoneDeckTracker\DiscardAdvisor\Fixtures", `
    "$env:APPDATA\HearthstoneDeckTracker\DiscardAdvisor\Diagnostics"
```

在全部门槛满足前，它会打印 `Offline regression: FAIL` 和
`Shadow thresholds: NOT MET`，但不会因进度不足抛出异常。这是进度结果，
不是安装失败。报告中的
Snapshot、request、analysis 和 `Shadow games with published analyses` 数字
应随有效对局增长。

## 9. 自动操作实验工具

插件会将最新粗略建议原子写入：

```text
%APPDATA%\HearthstoneDeckTracker\DiscardAdvisor\Automation\current-advice.json
```

文件包含当前 `gameId`、`stateId`、首选路线、实体所在区域和是否允许自动
执行。运行器每次只执行路线的第一步，随后等待新的 `stateId`；搜索超时、
低置信度、低随机覆盖率或未建模随机一费随从效果会阻止执行。

运行器依赖固定的炉石窗口布局。开始前：

1. 保持 HDT 和插件运行。
2. 将炉石设为窗口化或无边框窗口，不要在运行过程中改变尺寸或位置。
3. 进入传统对战，选中本文的精确狂野套牌，停留在可以点击“开始”的页面。
4. 复制并校准 `tools\windows-match-runner\default-layout.json`，尤其是
   `deckSlot`、`playButton`、`mulliganConfirm` 和 `continueButton`。坐标以
   `referenceWidth/referenceHeight` 为基准，运行时会随炉石窗口等比例缩放。
5. 确保炉石窗口标题与 `-WindowTitle` 一致，中文客户端通常仍为
   `Hearthstone`。

先运行一次只校验窗口和坐标、不启动对局也不点击鼠标的命令：

```powershell
.\tools\windows-match-runner\start-match-runner.ps1 `
  -ValidateOnly `
  -LayoutPath .\tools\windows-match-runner\default-layout.json
```

正式自动进行 5 局传统对战：

```powershell
.\tools\windows-match-runner\start-match-runner.ps1 `
  -MatchCount 5 `
  -LayoutPath .\tools\windows-match-runner\default-layout.json
```

任意时刻按 `Ctrl+Shift+F12` 可请求紧急停止。也可以在命令输出的会话目录
创建名为 `STOP` 的空文件。运行器不会关闭炉石或 HDT，超时和不支持动作会
停止整个会话并保留已产生的记录。建议被低置信度、覆盖率或未知实体阻断
超过 30 秒时也会停止；这 30 秒内可人工操作，使插件生成新的 `stateId`。

完成后将记录提交并推送到已经配置好凭据的 GitHub 仓库：

```powershell
.\tools\windows-match-runner\start-match-runner.ps1 `
  -MatchCount 5 `
  -LayoutPath .\tools\windows-match-runner\default-layout.json `
  -UploadToGitHub `
  -RepositoryPath $repo `
  -GitRemote origin
```

上传前必须保证仓库能正常执行 `git push`，且 Git 暂存区必须为空；运行器
不会把其他已暂存修改混入证据提交。工作树中的未暂存修改不会被加入。
每次运行会生成独立的 `validation-runs/<session-id>` 提交，内容包括运行器
事件、会话摘要，以及仅属于本次会话的诊断、Fixture、回放和建议历史。

## 10. 可选专家标注

先运行离线回归，生成盲审包：

```powershell
.\scripts\run-offline-regression.ps1 `
  -InputPath `
    "$env:APPDATA\HearthstoneDeckTracker\Replays", `
    "$env:APPDATA\HearthstoneDeckTracker\DiscardAdvisor\Fixtures", `
    "$env:APPDATA\HearthstoneDeckTracker\DiscardAdvisor\Diagnostics"
```

盲审包位置：

```text
.artifacts\offline-regression\expert-review-pack.json
```

当前验证 profile 不要求专家标注。`expert-review-pack.json` 和标注工具
仍保留用于可选的离线质量研究，但不会影响 Shadow 或可见测试门禁。

## 11. 最终离线回归

完成 5 局对局后，传入回放、Fixture 和诊断三类输入：

```powershell
.\scripts\run-offline-regression.ps1 `
  -InputPath `
    "$env:APPDATA\HearthstoneDeckTracker\Replays", `
    "$env:APPDATA\HearthstoneDeckTracker\DiscardAdvisor\Fixtures", `
    "$env:APPDATA\HearthstoneDeckTracker\DiscardAdvisor\Diagnostics"
```

检查以下报告：

```text
.artifacts\offline-regression\offline-regression.json
.artifacts\offline-regression\offline-regression.md
```

可见建议门禁要求：单一匹配的插件/规则版本 cohort、至少 5 局包含
Published 分析的完整 Shadow 对局，以及全部合法性、延迟、过期结果和未
支持交互门槛均通过。不要求专家标注。

## 12. 归档并校验证据

启用可见建议前先归档证据。若 `Replays` 中有较早或无关对局，传入仅包含
本 cohort 回放的干净目录或明确的回放文件，而不是整个旧目录。

```powershell
.\scripts\archive-validation-evidence.ps1 `
  -RegressionReportPath .\.artifacts\offline-regression\offline-regression.json `
  -ReplayPath "$env:APPDATA\HearthstoneDeckTracker\Replays" `
  -FixturePath "$env:APPDATA\HearthstoneDeckTracker\DiscardAdvisor\Fixtures" `
  -DiagnosticsPath "$env:APPDATA\HearthstoneDeckTracker\DiscardAdvisor\Diagnostics"
```

命令会输出证据目录。用该精确路径进行严格验证：

```powershell
.\scripts\verify-validation-evidence.ps1 `
  -EvidencePath <上述命令输出的证据目录> `
  -RequireVisiblePrerequisites
```

## 13. 启用小规模可见建议测试

仅在证据校验成功后执行：

```powershell
.\scripts\enable-visible-test.ps1 `
  -RegressionReportPath .\.artifacts\offline-regression\offline-regression.json
```

脚本会原子性地将 `settings.json` 改为 `experimental`。不要手动编辑该
文件。可见测试是独立的小 cohort，继续保留其诊断数据，并与 Shadow 证据
分开归档。

## 14. 常见问题

| 现象 | 处理方式 |
| --- | --- |
| PowerShell 提示禁止运行脚本 | 在当前窗口执行 `Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass`。 |
| 安装器找不到 HDT `v1.53.11` | 运行 `bootstrap-hdt-reference.ps1`，或传入包含 `Hearthstone Deck Tracker.exe` 的 `-HdtReferenceDir`。 |
| 没有 `Fixtures` 目录 | 确认插件已启用、卡组精确匹配且完成真实对局；随后查看诊断日志门禁状态。 |
| `DeckMismatch` | 重新导入本文的精确套牌代码，不要替换任何一张牌。 |
| `UnsupportedPatch` 或指纹不匹配 | 更新到最新项目源码并重装插件。`0.4.13` 对当前 HDT `CardDefs.base.xml` 接受哈希 `1D9CF031FB1FE37A39FDF4F515702BCC2425EB71BA8BE0A236948372B15A38BB`；其他哈希请保留诊断日志以便排查。 |
| 已有 `Enabled`，但仍是 `Snapshots: 0/0` | 确认已安装 `0.4.13` 并执行一回合后运行 `Get-Content "$env:APPDATA\HearthstoneDeckTracker\DiscardAdvisor\Diagnostics\discard-advisor.jsonl" -Tail 50`。若有 `snapshot_capture_skipped`，其 `reason` 会说明缺失的 HDT 实体；不要将该局计入 5 局。 |
| 进度检查退出码为 1 | 默认进度检查在未满 `5/5` 时会正常返回；仅传入 `-RequireAcceptance` 时才会严格以非零退出。读取生成的 JSON 和 Markdown 报告。 |
| historical games 或 snapshots ignored 大于 0 | 诊断和 Fixture 目录保留了旧版本数据；当前 `0.4.13 / 0.3.4` cohort 会单独统计，无需清理旧数据。 |
