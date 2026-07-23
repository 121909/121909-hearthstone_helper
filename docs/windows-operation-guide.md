# Windows 影子运行操作手册

本文是 Windows 上采集 Discard Advisor 有效验证证据的完整流程。当前
发布 cohort 为：插件 `0.4.5`、规则集 `0.3.2`、HDT `1.53.11`、炉石
构建 `247416`。

在诊断日志出现 `gate_decision` 且状态为 `Enabled` 之前，不要开始累计
50 局 Shadow 对局。

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

旧版 `0.4.4 / 0.3.1` 的诊断数据不能与当前 `0.4.5 / 0.3.2` 混用。升级
前先归档旧插件数据，以下操作不会删除旧证据：

```powershell
$hdtData = "$env:APPDATA\HearthstoneDeckTracker"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"

if (Test-Path "$hdtData\DiscardAdvisor") {
    Move-Item "$hdtData\DiscardAdvisor" "$hdtData\DiscardAdvisor-pre-0.4.5-$stamp"
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

门禁只接受下列精确的 30 张狂野弃牌术。莫瑞甘的灵界当前为 2 费，但其
CardId 和套牌代码没有变化。

```text
AAEBAf0GA84Hj4ID9qEGDtSzArW5A9XRA9DhA5iSBauSBZXKBteXB4SZB6StB8ayB9a+B9m+B8+/BwA=
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
  50 局目标。

对局外出现 `UnsupportedMode` 是正常的。真实对局中出现
`DeckMismatch`、`UnsupportedPatch`、`UnsupportedHdtVersion`、
`UnsupportedCardDefinitions` 或 `UnsupportedHearthDb` 时，立即停止采集
并按第 13 节排查。

## 8. 收集 Shadow 对局

使用精确卡组完成至少 50 局真实 `RANKED_WILD` 对局，并保留回放、Fixture
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

在全部门槛满足前，该命令会故意返回非零退出码并打印
`Offline regression: FAIL`。这是进度结果，不是安装失败。报告中的
Snapshot、request、analysis 和 `Shadow games with published analyses` 数字
应随有效对局增长。

## 9. 生成专家标注

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

至少为 200 个复杂局面创建标注。每个熟悉卡组的审阅者使用稳定的匿名
`ReviewerId`；排序选项必须来自盲审包中的 `option-*`：

```powershell
.\scripts\create-expert-annotation.ps1 `
  -ReviewPack .\.artifacts\offline-regression\expert-review-pack.json `
  -StateId <review-pack 中的 state-id> `
  -ReviewerId <匿名且稳定的审阅者标识> `
  -RankedOption option-1,option-2,option-3
```

每条标注可排序一到三个选项。默认输出到：

```text
.artifacts\offline-regression\annotations
```

## 10. 最终离线回归

收集完对局和标注后，传入四类输入：

```powershell
.\scripts\run-offline-regression.ps1 `
  -InputPath `
    "$env:APPDATA\HearthstoneDeckTracker\Replays", `
    "$env:APPDATA\HearthstoneDeckTracker\DiscardAdvisor\Fixtures", `
    "$env:APPDATA\HearthstoneDeckTracker\DiscardAdvisor\Diagnostics", `
    ".\.artifacts\offline-regression\annotations"
```

检查以下报告：

```text
.artifacts\offline-regression\offline-regression.json
.artifacts\offline-regression\offline-regression.md
```

可见建议门禁要求：单一匹配的插件/规则版本 cohort、至少 50 局包含
Published 分析的完整 Shadow 对局、至少 200 个合格专家标注、主路线
Top-3 一致率至少 80%，以及全部合法性、延迟、过期结果和未支持交互门槛
均通过。

## 11. 归档并校验证据

启用可见建议前先归档证据。若 `Replays` 中有较早或无关对局，传入仅包含
本 cohort 回放的干净目录或明确的回放文件，而不是整个旧目录。

```powershell
.\scripts\archive-validation-evidence.ps1 `
  -RegressionReportPath .\.artifacts\offline-regression\offline-regression.json `
  -ReplayPath "$env:APPDATA\HearthstoneDeckTracker\Replays" `
  -FixturePath "$env:APPDATA\HearthstoneDeckTracker\DiscardAdvisor\Fixtures" `
  -AnnotationPath ".\.artifacts\offline-regression\annotations" `
  -DiagnosticsPath "$env:APPDATA\HearthstoneDeckTracker\DiscardAdvisor\Diagnostics"
```

命令会输出证据目录。用该精确路径进行严格验证：

```powershell
.\scripts\verify-validation-evidence.ps1 `
  -EvidencePath <上述命令输出的证据目录> `
  -RequireVisiblePrerequisites
```

## 12. 启用小规模可见建议测试

仅在证据校验成功后执行：

```powershell
.\scripts\enable-visible-test.ps1 `
  -RegressionReportPath .\.artifacts\offline-regression\offline-regression.json
```

脚本会原子性地将 `settings.json` 改为 `experimental`。不要手动编辑该
文件。可见测试是独立的小 cohort，继续保留其诊断数据，并与 Shadow 证据
分开归档。

## 13. 常见问题

| 现象 | 处理方式 |
| --- | --- |
| PowerShell 提示禁止运行脚本 | 在当前窗口执行 `Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass`。 |
| 安装器找不到 HDT `v1.53.11` | 运行 `bootstrap-hdt-reference.ps1`，或传入包含 `Hearthstone Deck Tracker.exe` 的 `-HdtReferenceDir`。 |
| 没有 `Fixtures` 目录 | 确认插件已启用、卡组精确匹配且完成真实对局；随后查看诊断日志门禁状态。 |
| `DeckMismatch` | 重新导入本文的精确套牌代码，不要替换任何一张牌。 |
| `UnsupportedPatch` 或指纹不匹配 | 停止采集，更新到最新项目源码并重装插件；保留相关诊断日志以便排查。 |
| 进度检查退出码为 1 | 在未满足全部门槛前属正常现象；读取生成的 JSON 和 Markdown 报告。 |
| cohort 数大于 1 | 旧版诊断日志与新版混合。将旧 `DiscardAdvisor` 目录移出当前数据目录，重装后重新采集干净 cohort。 |
