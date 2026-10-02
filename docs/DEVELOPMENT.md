# 开发、协议与复现

本工程复用锁定版本的 sts2-sim 副本，实现 Silent A10 单场战斗适配、独立分支终局、公共知识和有限支持机制的 belief 采样。不是旧 Mod，不连接实机。当前已加入离线公共信息教师及数据/学生工程；尚未正式训练或晋升模型。

## 构建与测试

在仓库根目录执行。首次还原需要可访问 NuGet 或已有相同版本的包缓存。

```powershell
dotnet build .\Nosl.M012.sln -c Release -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false
dotnet test .\tests\Nosl.Tests\Nosl.Tests.csproj -c Release -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false
python -B .\tools\protocol_smoke.py
```

SDK 锁定 .NET 9.0.303。NuGetAudit=false 仅避免离线审计请求，不改依赖版本。所有规则委托 C# 引擎，Python 仅承担证据与协议测试。

## 持久 JSONL worker

```powershell
dotnet .\src\Nosl.Worker\bin\Release\net9.0\Nosl.Worker.dll
```

每行一条命令：

```json
{"op":"reset"}
{"op":"observe"}
{"op":"actions"}
{"op":"sample","samplerSeed":71}
```

`step` 提交当前 `actions` 中的完整 token，例如 `{"op":"step","action":{"revision":0,"kind":"play","slot":2,"target":0,"selection":null}}`。slot 随手牌变化，revision 防止复用旧 token。玩家自己为 target=-2，无目标为 -1，敌人使用公开槽位。子选择 token 包含公开候选的 `selection` 索引数组。`settle` 仅在战斗结束时读取 memoized 终局事实。

`reset.scenario` 可指定公开牌组（升级牌用 `+`）、药水、遗物、HP 和敌人，seed 只传给私有 worker，不进入策略 packet。非自然牌组或强制 HP 均是声明的构造场景，不作为自然训练分布。未知模型明确返回 unsupported_content，不删候选或偷偷采用简化效果。

## API 与隔离

- `CombatSession.CreateAsync / Observe / EnumerateActions / StepAsync / SettleAsync`。
- `ForkExact` 只接受稳定玩家边界，分支 room、hooks 和任务独立。
- 悬挂选择使用 `ReplayToChoiceAsync` 重建独立执行链，不复制协程栈。采样选择应从前一个稳定节点进入。
- `BeliefSampler.SampleWorld` 先规范化未知多重集合，再独立采样牌序和未来 RNG，保留已知位置和当前公开意图。不是仅 reseed 真实牌序。
- `EnumerateDrawPosterior` 对最多 8 张牌给出精确牌序后验；不冒称枚举全部未来随机流。
- `BranchDiagnostics` 只做 M2 信息隔离诊断，冻结公共续策并记录完整、截断、异常；不产生 M4 教师效用或正式标签。
- `Nosl.PublicPolicy` 仅引用 `Nosl.Contracts`，通过 stdin 接收公共 JSON；不引用 simulator/worker，不收到私有世界对象。这只是测试续策，不是训练后的学生。

目前单 worker 顺序执行，未验证同进程并发。终点包含 AfterCombatEnd、清理、AfterCombatVictory、奖励生成和 BeforeCombatRewardOffered，停在首次战后选择之前，奖励领取次数为 0。失败遵循原引擎，不补回血。

## 当前完整目录与能力边界

当前使用 iRyougi/sts2-sim5a9576b 原生注册表及遭遇工厂。`reset.scenario` 新增 `encounter`（原生遭遇名）、`enemies`（明确声明的自定义怪物组合）、`gold`；encounter 与 enemies 互斥。升级次数可用连续 `+` 表示，非法等级显式拒绝。

动作新增 `discard_potion`。手牌/生成/弃牌/消耗牌/无序抽牌堆选择与启动阶段选择都通过相同 choice token；多选保留选择顺序，超过100000显式候选时返回能力限制，绝不静默截断。隐藏牌堆候选不暴露实际顺序。

公共契约是 `nosl.public.v2`。卡牌临时费用、词条、附魔/affliction，状态时序、计数器、遗物公开状态、金币、充能球/宠物均有显式字段。新生成而尚未公开的抽牌堆身份以 `unidentifiedDrawCount` 表示，不进入牌名多重集。

`sample.maxAttempts` 控制通用原生 replay 后验提案预算，默认256。预算耗尽返回 `posterior_budget_exhausted`，不是胜/败。完整前缀必须从声明的初始场景记录，原始seed不参与条件提案。稳定有限机制仍可用独立未知牌序/未来流快速路径。

`ForkForContinuationAsync` 为需要具体RunState或额外收益结算的分支重建独立原生执行链。`ForkExact` 不能替代此路径，也不支持复制悬挂协程。已原位采样的中途选择不能悄悄回原seed重放。

五类强制事件的原生返回/奖励上下文、任意自然局的完整公开携入状态、极低接受率后验、穷尽组合和同进程并行仍有明确限制。见 [COVERAGE_BRIDGE.md](COVERAGE_BRIDGE.md)。

协议烟测只需 Python 标准库；测试 trace 写入忽略的 `artifacts/`。根目录测试调用独立公共策略进程，因此先构建整个 solution，再运行测试。


## M3–M6 worker operations

- `teacher`: evaluate every legal root candidate with `TeacherOptions`; defaults to development T0 and independent evaluation worlds
- `teacher_record`: same evaluation, exported as strict `public_input / targets / audit_only`; requires `sourceRun`, `sourceCombat`, `branchFamily`
- `continue`: execute one frozen public-rule source-policy choice, without inspecting hidden state
- `natural_sources`: collect native-run public roots with `NaturalSourceOptions`; this is explicitly raw/unlabeled source evidence, not fresh-scenario reconstruction

`TeacherOptions.Mode` is T0 or T1, with unique/disjoint ExplorationSeeds/EvaluationSeeds, MaxDecisions, TreeDepth and MaxPosteriorAttempts. FormalLabels remains forbidden by the uncalibrated objective. See [teacher evidence](M4_TEACHER.md), [M3–M6 checkpoint](M3_M6_STATUS.md), [data commands](../tests/data/README.md), and [student commands](../python/README.md).

Generation is Linux/POSIX cloud engineering (`fcntl` corpus locks), with isolated worker processes, explicit source recipes, durable rows and checked runtime versions. Raw earned-reward opportunity counts are kept without generated option identities; their prices are unresolved rather than silently zero.
