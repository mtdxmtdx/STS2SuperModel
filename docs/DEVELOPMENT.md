# 开发、协议与复现

本工程复用锁定版本的 sts2-sim 副本，实现 Silent A10 单场战斗适配、独立分支终局、公共知识和有限支持机制的 belief 采样。不是旧 Mod，不连接实机，不含正式教师、训练数据或模型权重。

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

## 支持边界

技术范围：11 类卡牌及其合法升级、5 类药水、3 类遗物、TwigSlimeS/LeafSlimeS/Nibbit 单敌人场景。详见覆盖清单，不等于 Silent 全内容已完成。Nibbit 固定单独遭遇，公开招式决定循环；LeafSlimeS 的已公开招式决定不能重复约束；TwigSlimeS 无潜在分支记忆。其他怪物未证明后验，明确不支持。

意图 damage 字段采用原模拟器 AttackIntent 的公开基础伤害预览；力量/虚弱等状态另列 powers。它不是客户端悬停预览对拍证明。

全部内容升级、复杂遗物、潜在怪物记忆、排队额外奖励、bundle/抽牌堆选择、附魔和 affliction、通用中途选择采样仍是后续范围；READY_FOR_TRAINING=false。客户端版本和原版保真尚未核对。M3–M8 本轮未执行。


协议烟测只需 Python 标准库；测试 trace 写入忽略的 `artifacts/`。根目录测试调用独立公共策略进程，因此先构建整个 solution，再运行测试。
