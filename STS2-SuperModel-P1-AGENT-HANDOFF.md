# STS2SuperModel 第二优先级 Agent 交接文档

更新时间：2026-08-27

> 历史交接文档（2026-08-27/28）：原用于 P1 语义覆盖扩展。M0-M2 NOSL 已在
> 2026-08-29 完成，本文中的旧测试数量、覆盖数量和“不要重复实现”说明不再是现役状态。
> 当前事实以 `AGENTS.md`、`PLAN_NOSL.md`、`PLAN.md` 和最新验证报告为准。

## 1. 交接目标

本交接只用于 **P1：语义覆盖和验证扩展**。P0 的 TeacherWorker、Expectimax
随机分支修正和 1,000 状态教师数据 Smoke 已完成；PyTorch 模型训练仍属于后续阶段。

P1 的目标是把已经结构化但尚未获得足够行为证据的 Power、遗物、CLI 战斗
协议和数据质量门禁逐批补齐。任何对象只有在真实引擎和影子模拟器零差异验证
后，才能提升为 `LiveObserved`、`SimulatorSupported` 或 `Reliable`。

## 2. 权威资料和版本锁

不要把旧交接文档中的数字当作当前事实。以以下文件为准：

- 计划：`D:\STS2BestChoice\STS2SuperModel\PLAN.md`
- P0 验证：`D:\STS2BestChoice\STS2SuperModel\data\P0_VERIFICATION.md`
- 当前状态：`D:\STS2BestChoice\STS2BestChoice\docs\CURRENT_STATE.md`
- 根规则：`D:\STS2BestChoice\AGENTS.md`
- 模组/Core 规则：`D:\STS2BestChoice\STS2BestChoice\AGENTS.md`
- CLI 规则：`D:\STS2BestChoice\STS2SuperModel\sts2-cli-v0111\CLAUDE.md`

固定版本：

```text
Game: v0.111.0
Commit: 41cef1ea
sts2.dll SHA-256: 0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9
CLI protocol: 0.2.0
Trace schema: 1
```

历史基线（仅供当时交接追溯，不代表当前状态）：

- Core Release：706 passed；
- Training tests：48 passed；
- CLI combat/consistency：21 passed；
- P0 固定矩阵：21 fixtures、41 个 C# ShadowDiff 报告，全部 `mismatch_count=0`；
- Power：283 个已捕获，9 个有真实行为探针，55 个 declared mapping 尚需行为验证；
- Relic：299 个已捕获，14 个 declared mapping 已验证，165 个已知战斗钩子和119 个未知遗物仍不得作为 Reliable。

## 3. 仓库和工作目录

GitHub 模型仓库：

```text
https://github.com/mtdxmtdx/STS2SuperModel.git
main commit: 6a2b6e24ba61465689670f2fdd61220d472a11b9
```

本地模型镜像：

```text
D:\STS2BestChoice\STS2SuperModel
```

上传用的模型仓库副本：

```text
D:\STS2BestChoice\work\github-STS2SuperModel
```

### 重要：Core 不在 GitHub 模型镜像中

为遵守“只上传模型相关内容，不上传旧模组工程”的要求，GitHub 仓库没有包含
整个 `STS2BestChoice` 模组目录。但 `ShadowDiff` 的项目引用仍需要 Core：

```text
D:\STS2BestChoice\STS2BestChoice\STS2BestChoice.Core
```

`training/ShadowDiff/STS2BestChoice.ShadowDiff.csproj` 使用相对引用：

```text
..\..\..\STS2BestChoice\STS2BestChoice.Core\STS2BestChoice.Core.csproj
```

所以新 Agent 不能只克隆 GitHub 仓库后直接构建。推荐每个 Agent 使用下列隔离
目录结构：

```text
D:\STS2BestChoice\work\p1-power\
  STS2SuperModel\       # 从 GitHub 克隆的模型仓库
  STS2BestChoice\       # 仅复制 Core 项目，满足 ShadowDiff 相对引用
    STS2BestChoice.Core\

D:\STS2BestChoice\work\p1-relic\
  STS2SuperModel\
  STS2BestChoice\
    STS2BestChoice.Core\
```

复制 Core 时只复制 `STS2BestChoice.Core` 源码和项目文件，不复制 `bin/obj`。
Power/Relic Agent 之间不得共享同一份可写 Core；各自完成后由主 Agent 依次
审查并把确认过的 Core 改动合并回主工作区。

建议分支名：

```text
codex/p1-power
codex/p1-relic
codex/p1-cli
codex/p1-data-quality
```

Agent 不要直接 push 或修改远程 `main`，除非用户另行授权。

## 4. 可并行任务

### Lane A：Power 语义和真实差分

工作目录：`D:\STS2BestChoice\work\p1-power\STS2SuperModel`

首批目标：

```text
THORNS
ACCURACY
PLATING
POISON
PANACHE
```

允许修改：

- `STS2BestChoice\STS2BestChoice.Core\Simulation\DeterministicSimulator.cs`
- `STS2BestChoice\tests\PowerCatalogTests.cs`
- 与 Power 相关的 Core 测试
- `STS2SuperModel\training\ShadowDiff\Program.cs`
- 新建 `training\fixtures\p1-power-*-commands.jsonl`
- 新建 `training\run_p1_power_probes.py`
- Power coverage/report 测试和文档

必须完成：

1. 通过 v0.111 IL、运行时对象和固定 CLI 场景确认 Power 的 ID、owner/applier、
   amount、DynamicVars、内部计数器、触发阶段、触发顺序、持续时间和 RNG 消耗。
2. 为每个通过的目标建立可重复 fixture，至少覆盖触发前、触发后和必要的回合边界。
3. 用 ShadowDiff 比较 HP、格挡、能量、牌堆、Power、遗物、计数器、七条 RNG 流和终止状态。
4. 差异出现时修复通用语义，不得为单个 fixture 写特判、跳过字段或降低验证标准。
5. 只有报告同时满足 `confidence=Reliable`、`mismatch_count=0`、重复运行一致，
   才能更新 `runtimeProbedSet` 和 catalog 状态。

不得修改：ExpectimaxEngine、TeacherWorker、教师标签、Relic 证据集合、模型训练代码。

### Lane B：遗物语义和真实差分

工作目录：`D:\STS2BestChoice\work\p1-relic\STS2SuperModel`

首批目标：

```text
INCENSE_BURNER
SUNDIAL
TOUGH_BANDAGES
TUNGSTEN_ROD
UNCEASING_TOP
```

建议优先顺序：先完成已有部分 Core 逻辑的 `INCENSE_BURNER`、`SUNDIAL`，再处理
受伤/弃牌/回合结束钩子。

允许修改：

- `STS2BestChoice\STS2BestChoice.Core\Simulation\DeterministicSimulator.cs`
- `STS2BestChoice\tests\RelicCatalogTests.cs`
- `RelicSimulationTests.cs`、`RelicTrainingFeatureTests.cs`
- `STS2SuperModel\training\ShadowDiff\Program.cs`
- 新建 `training\fixtures\p1-relic-*-commands.jsonl`
- 新建 `training\run_p1_relic_probes.py`
- Relic coverage/report 测试和文档

必须完成：

1. 从 ModelDb、v0.111 IL 和 CLI 行为确认 relic ID、counter、DynamicVars、
   owner、触发阶段、只触发一次的条件、重置时机和 RNG 消耗。
2. Wiki 只能作为名称、描述和版本线索，不能作为运行时语义证据。
3. 每个通过目标都必须有 CLI fixture、ShadowDiff 报告和稳定重复结果。
4. 不得把 `UnsupportedKnownEffect` 或 `Unknown` 改成 `SimulatorSupported`，除非
   有真实差分证据。
5. 不得修改 Power 的 runtime-probed 集合或 Expectimax/TeacherWorker。

### Lane C：CLI 战斗范围门禁

工作目录：`D:\STS2BestChoice\work\p1-cli\STS2SuperModel`

只处理当前单人战斗回合：

- 多卡选择的稳定 `choice_id` 和 `selected_card_instance_ids` 回放；
- 两个 CLI 进程的 public hash 和稳定 ActionCandidate 一致；
- 失败步骤、flush 和恢复记录；
- `0.2.0` 普通动作兼容；
- public/teacher 泄漏检测；
- combat-scope gate。

不得处理地图、奖励、商店、事件、save/load、完整牌局、Power/Relic handler、
Expectimax 或模型训练。

### Lane D：数据质量门禁

工作目录：`D:\STS2BestChoice\work\p1-data-quality\STS2SuperModel`

只处理 training 下的 schema、validator、split、manifest 和 quality report：

- mixed version/hash 拒绝；
- episode/seed 不跨 split；
- public/teacher 不泄漏；
- stable action ID 完整；
- Reliable/Estimated/Uncalculable 统计一致；
- 空 teacher label 明确分类；
- 损坏 JSONL/Parquet 定位清楚；
- 生成 `data/dataset-quality-gate.json`。

不得修改 Core、CLI 游戏行为、Expectimax、TeacherWorker 或 Power/Relic 证据。

## 5. 通用执行规范

每个 Agent 开始时：

1. 阅读本文件、`AGENTS.md`、`PLAN.md`、`P0_VERIFICATION.md`。
2. 确认版本锁和工作目录；记录起始 commit。
3. 先建立最小 fixture 和失败/通过基线，再改代码。
4. 保持现有 P0 21 fixtures 和 41 reports 全部通过。
5. 不把反射方法名、Wiki 文本或白名单当作行为验证。
6. 不将原始 seed、RNG 四状态字或未来抽牌身份写入 public 模型特征。
7. 不产生或提交 `sts2.dll`、第三方运行库、`.python-deps`、`bin/obj` 和缓存。

允许的本地验证命令：

```powershell
dotnet build .\training\ShadowDiff\STS2BestChoice.ShadowDiff.csproj -c Release --no-restore
dotnet test D:\STS2BestChoice\work\p1-<lane>\STS2BestChoice\tests\STS2BestChoice.Tests.csproj -c Release --no-restore
python .\training\run_p1_<lane>_probes.py
```

Training 测试使用已安装的 Python 3.12、PyArrow、jsonschema 和 pytest；这些本地
依赖不上传。完整训练测试必须排除 `test-output`、`.pytest_cache`、`__pycache__`。

## 6. P1 Agent 交付清单

每个 Agent 完成后必须提供：

- 修改文件列表；
- 新增 fixture 和 runner 路径；
- 每个目标的真实行为结论；
- IL/CLI/ShadowDiff 证据路径；
- `confidence`、`mismatch_count` 和重复运行结果；
- catalog/coverage 统计变化；
- Core、CLI、training 的实际测试输出；
- 未解决差异、原因和下一步；
- 不得声称全仓或完整 CLI 已通过，除非确实运行并记录。

## 7. 合并顺序

推荐顺序：

```text
Lane C（CLI）
→ Lane D（数据质量）
→ Lane A（Power）
→ Lane B（Relic）
→ 主 Agent 统一回归和文档同步
```

Power 和 Relic 都会触及 Core/ShadowDiff，不能直接同时写入同一份源码。合并每个
Lane 后必须重新运行 P0 全矩阵，再接受下一个 Lane。

## 8. 明确不在本交接范围

- 第一优先级 TeacherWorker 和教师数据生成；
- Expectimax 超过 32 分支的采样改造；
- PyTorch 训练、离线 RL、ONNX 和 C# 学习接口；
- 删除旧交接文档、清理验证副本或清理工作区；
- 修改远程 `main`、发布版本或上传游戏程序集。

## Suggested skills

- `tdd`：每个 Power/Relic 先写固定场景、再实现和回归。
- `diagnosing-bugs`：ShadowDiff 出现字段差异时按 mismatch 报告定位根因。
- `research`：需要确认 v0.111 IL 或官方版本事实时使用，并保留证据引用。
- `neat-freak`：所有 P1 Lane 合并且验证完成后，再同步 PLAN、CURRENT_STATE 和 P0 文档。
