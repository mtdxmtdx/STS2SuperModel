# 影子模拟器修改与优化建议（NOSL Expectimax）

审查日期：2026-08-30

## 结论

当前影子模拟器适合继续做语义探针和 CLI 差分回放，但不应把全部搜索结果当作精确 NOSL 教师标签。优先顺序必须是：公共信念和概率正确性 → 策略树与时序 → 状态键和稳定 ID → 真实引擎门禁 → COW/指纹/并行性能。否则加速的只是错误标签。

版本锁：StS2 v0.111.0 / commit 41cef1ea；sts2.dll SHA-256 0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9；CLI protocol 0.2.0；trace schema 1。

## 当前证据

| 检查 | 最近结果 | 解释 |
|---|---:|---|
| Core Release 测试 | 752 passed / 0 failed / 0 skipped（本轮定向回归 19/19） | 语义回归通过，不等于 NOSL 概率模型完整 |
| CLI v0.111 一致性测试 | 6 passed | 协议/版本 smoke 通过 |
| 战斗遗物 closeout | 98 simulator-supported；2 PartiallySupported；20 未实现 hook；97 OutOfScope；24 strict eligible | 126 份 relic reports 均零 mismatch，但随机/teacher-conditioned 质量已分级 |
| 卡牌统计 | 1176 variants；1099 单人战斗范围；590 signatures；1 签名全变体严格验证 | 结构化覆盖，不是全部行为级 Reliable |
| RNG 捕获 | 当前主快照 7 条 combat 流 | v0.111 RunRngSet 有 12 条流，另 5 条只进 sidecar |
| 模型阶段 | PLAN_NOSL 的 M0-M2 基础结构/桥接存在，M3 correctness closeout 尚未通过 | 暂不启动大规模 Reliable 标签 |

已复现的两个高风险现象：

1. NoslExpectimaxTeacher.Solve 只读取普通 ActionLine；CombatSearchSession.BuildPolicies 另存 PolicyLine。临时场景中随机 100 伤害根动作期望值 160，而固定 60 伤害动作是 60，facade 仍可能返回固定动作。
2. MaskHiddenState 把 nosl_hidden_order_masked 写入 GlobalRestrictions；MutableCombatState.FromSnapshot 将其转换为 risk，合法的信息掩码会人为把结果降级为 Estimated/Uncalculable。

## P0-1：策略树必须成为 NOSL 标签来源

当前搜索是路径枚举加终局候选，不是完整 action-level contingent expectimax。Chance 后必须回到 Max；未来尚未观察的结果不得条件化已经作出的动作。

改造：
1. 引入不可变 PolicyNode。Max 节点保存 action_id 到 child，Chance 节点保存 outcome_id、probability 到 child。
2. 每个 chance boundary 只按当时可观察结果建立下一层 Max。
3. 每个 root action 计算 V(a)=Σ p(o|public belief,a)V(o)，分别排序 Balanced、HighestDamage、MinimumLoss。
4. 保存所有合法动作 value、mask、tie group、soft policy；PV 只做调试。
5. TeacherEvaluator.BuildObjective 统一消费 PolicyLine 聚合值；无 chance 动作也生成 probability=1 的 deterministic policy。

验收：随机根动作期望高于固定动作时 Top-1 必须是随机动作；Chance→Max 反例中各分支分别选择后续动作；相同 public belief 的 policy tree/hash 完全一致。

## P0-2：修正 NOSL 信念和 public/teacher 边界

NoslBeliefState.FromPublicSnapshot（CombatModel.cs 约 218-239 行）把 Hand、Draw、Discard、Exhaust 全部相加，Exhaust 牌会错误地再次进入可抽集合。MaskHiddenState 还可能把隐藏实例/顺序带入普通 ordered-pile 逻辑。

新增 PublicStateNormalizer，至少包含：
- KnownTopPrefix：公开可知的顶牌；
- DrawEligibleCounts：排除 Exhaust/removed 的可抽多重集合；
- DiscardCounts、ExhaustCounts；
- Constraints：excluded、position、generated-card、replacement。
- 手牌顺序仅在选择/用户可见时保留；隐藏 draw/discard 顺序不进 NOSL key。
- 带完整隐藏牌序的 public snapshot 必须清空并记 sidecar。
- nosl_hidden_order_masked 只写 provenance/diagnostics，不写 semantic risk。
- 用显式 RngAvailability（Known/Unknown/Masked）替代 RngState=0/1。
- 从 belief multiset 生成 synthetic unordered pool，不能因 CLI public 不带 ordered piles 就把抽牌当空堆。

验收：任意隐藏牌序、RNG words、runtime instance ID 变化时，belief key、标签和 hash 不变；Exhaust 牌永不回到 DrawEligibleCounts。

## P0-3：统一随机算子和概率质量

ShadowSimulationTransitions 中存在 EnumeratePermutations(pool).Take(max)、seed % n、首个字典序结果、固定 Orb 池等路径；重复卡被当作不同排列，截断后仍可能赋 1/N。随机 listener、随机状态/伤害、稀有牌选择、回合开始生成也可能绕过 PlayCardOutcomes。

引入版本化 RandomOperatorRegistry，字段包括 operator_id、eligible outcomes、权重、是否有放回、顺序、条件、证据版本、public observation delta。每个 ChanceBranch 必须显式带：
ProbabilityKnown、OutcomeKind（Exact/Sampled/Unknown）、covered_mass、proposal_mass、ESS、CI、RngConsumptionVector（只记计数，不记 raw words）。

规则：
- 不放回抽牌/洗牌按语义牌型 multiset prefix DP，下一张概率为 count/remaining；合并相同 post-state。
- 随机目标/状态/伤害/球/生成牌/药水/稀有牌按真实 eligibility 和权重；未知池/权重不得默认均匀。
- 随机弃牌/消耗只有证明效果可交换时才用无序组合，否则保留有序选择。
- 回合开始/结束、Power、Relic、自动出牌全部进入 event/chance queue。
- Unknown stream 的 NextInt 不得返回 0 并继续；应返回 UnknownOutcome/不可计算。
- 分支预算贯穿组合算子；先合并再截断/采样，保留未覆盖质量。
- 内部 outcome key 用稳定敌人/实例 ID；展示 label 与审计 hidden label 分离，完整 permutation 不进入 NOSL 标签。

验收：A,A,B 抽 1 张得到 A=2/3、B=1/3；合并 post-state 总质量在 1±1e-12；未知权重/采样质量不足自动降级。

## P0-4：稳定 ID、完整状态键、文化无关序列化

DeterministicSimulator.cs 约 1815-1853 行的 Fuel/Giant Rock 转换使用 Guid.NewGuid，导致重跑、缓存和 action ID 不稳定。生成牌按牌堆数量计数还可能复用旧 ID。MutableCombatState.ExactKey/CycleKey 漏 Gold、一次性标志、部分 History、PotionCostSpent、动态状态字段、Power SourceId 等，且直接 ToString 会受区域设置影响。SyncPowerState 约 4689-4731 行只按 owner+canonical id 合并，可能折叠不同来源的同名 Power。

改造：
1. StableInstanceIdFactory：generated:transform:<source_instance_id>:<kind>:<ordinal>；状态级 GeneratedInstanceCounter 单调。
2. 单一 UTF-8 typed canonical serializer；数值使用 InvariantCulture/固定长度前缀；字典/集合按稳定 ID 排序。
3. 分离 PublicObservationKey、NoslBeliefKey、SearchBehaviorKey、AuditExactKey、CycleKey。
4. Power/Relic 保存 runtime ID、owner、applier、source、DynamicVars、counter、trigger phase、version、evidence；索引包括 source/instance key。
5. unknown condition/dynamic amount/history counter 用 tri-state；不可默认 false/0 后标 Reliable。

测试：Gold、PotionCostSpent、DemonTongueUsedThisTurn、Power.SourceId、Status.GeneratedCard 任一变化必须改变 SearchBehaviorKey；隐藏牌序/RNG 变化不能改变 NoslBeliefKey；zh-CN/en-US key 相同；同名不同来源 Power 不合并。

## P0-5：保持事件时序和终点

PlayCardOutcomes/UsePotionOutcomes 把 Draw 先剥离再补抽，draw→伤害/选择/监听器会改变结果。ProjectToNextPlayerTurn 可能使用回合开始时的 enemy 快照；AddTerminalCandidate 在死亡/全灭后仍推进下一回合。非攻击 intent 的 Move/目标规则也不完整。

采用生命周期队列：
Validate → PayCost → PrePlay listeners → Effect AST → Draw/Choice/Generation → PostPlay listeners → Damage/Death → CombatEnd → TurnEnd → NextTurn

每阶段检查 pending choice、deferred generation、death window；不可 fork 时明确失败，不重排。每次敌人命中前按 enemy ID 从当前 state 读取；TryFinalizeCombat 在每个可能致死阶段调用并短路。增加多敌 Doom、回合结束毒杀、敌人弱化后多段攻击、draw interleave、autoplay 触发 Power/Relic、死后不可出牌 fixtures。CopyStateInto 改为集中 clone/replace 或字段清单测试。

## P0-6：敌人行动和 RNG 合约

未来 intent provider 主要复制当前 intent 并降级 Estimated；horizon>1 不能宣称完整 NOSL 最优。v0.111 RunRngSet 有 12 条流：7 条 combat，加 UpFront、UnknownMapPoint、MonsterAi、Niche、TreasureRoomRelics。主 NOSL 视图屏蔽 words/counters；sidecar 可记录计数。

若标签包含 EndTurn 后敌方行为，必须使用公共历史加规则化 MonsterAi belief（move-state、条件概率、cooldown/repeat），而不是把当前 intent 当未来确定值。加入 RngContract 版本、程序集 hash、stream 列表；整数/浮点 RNG 以锁定 DLL golden vectors 验证。若首版只做单回合，明确 horizon=one_player_turn，EndTurn 后标签统一 Estimated。

## P0-7：覆盖和 Reliable 门禁

- TriggerPhase 不要只靠 ID 文本启发式；未知 phase 保留 Unknown，待 runtime/IL evidence 后晋级。
- Target 集合遵守 alive/hittable/primary/minion/protection；解析失败的非攻击 intent 不能当无效果。
- Reliable 同时要求：语义已验证；所有 chance exact 且质量完整；无时序 fallback；CLI↔shadow mismatch_count=0；版本 metadata 完整。
- 未建模 effect 若可能改变 HP、牌堆、Power、Relic、目标或计数器，整条 action 设 Uncalculable。
- Checkpoint 需分层记录 public behavior、Power/Relic/counter、RNG consumption；仅 HP/Block 不足。

## P1：P0 通过后做结构和性能

### 借鉴 CombatSolver 的边界

可借鉴：
- CombatRootSnapshot 的主线程原子捕获和 quiescence/forkable 检查；
- PredictionForkContext、PredictionStateStore、SimCardPile 的 COW/remap；
- typed effect registry、coverage catalog、fail-fast；
- StateFingerprint 的 64/128-bit 增量指纹，完整 canonical bytes 做碰撞复核；
- BranchMonsterAi 的公共信息规则分支思想；
- headless harness、逐字段 strict diff、增量回放对拍。

不可移植到 NOSL：
- CombatPredictionRngSet 的真实 RNG words/未来顺序；
- known-seed MonsterAi 或 realized random outcome；
- RandomForeseer 代码、程序集、依赖；
- Beam/Pareto retention 作为离线精确教师；
- AutoSlayer 固定 setup/随机选牌作为自然分布。

参考：[CombatSolver README](https://github.com/Torch1230/CombatSolver/blob/main/README.md)、[CombatRootSnapshot.cs](https://github.com/Torch1230/CombatSolver/blob/main/src/Runtime/CombatRootSnapshot.cs)、[PredictionForking.cs](https://github.com/Torch1230/CombatSolver/blob/main/src/Engine/Common/PredictionForking.cs)、[StateFingerprint.cs](https://github.com/Torch1230/CombatSolver/blob/main/src/Search/StateFingerprint.cs)、[BranchMonsterAi.cs](https://github.com/Torch1230/CombatSolver/blob/main/src/Prediction/BranchMonsterAi.cs)。

CombatSolver README 明确其模拟核心含 Random Foreseer 来源关系和已知 RNG/路线用途，因此只作为结构/差分 oracle 参考；PLAN_NOSL 的“完全排除 CombatSolver/RandomForeseer”决策保持不变。

### 性能顺序

1. 先记录节点、分支、frontier、分配/GC、cache 命中、covered mass。
2. COW/持久化牌堆替代每节点全量 Clone；完整状态保留给 verifier。
3. compact planner state，叶子再物化 full state。
4. 热路径 128-bit fingerprint，完整 canonical bytes 做碰撞审计。
5. transition/turn-start/action legality cache；机会后按 behavior key+belief context 合并，不把 ChancePath label 放入 visited key。
6. 每个合法 root ActionCandidate 至少展开一次后，才做重复卡/对称敌人 POR；POR 必须有读写集合证明。
7. 32GB 先 CPU/root-shard 并行；进程级内存到 80-85% 或出现 swap 才升级 RAM。GPU 不适合不规则树搜索热路径。

### 建议模块边界

SemanticEffectRegistry
  - CardEffectHandlers
  - PowerRelicHandlers
  - PotionHandlers
  - EnemyAi/TurnHandlers
  - RandomOperatorRegistry
DeterministicSimulator（事务编排）
ShadowSimulationTransitions（chance、聚合、质量）
StateKey/StateFingerprint（序列化）
StrictDiff/TraceAdapter（CLI 验证）

先用 partial 文件和测试边界渐进迁移，每个 effect 只有一个权威结算入口。

## 推荐实施顺序

M3a 契约冻结：
- PublicStateNormalizer、NoslBeliefKey、SearchBehaviorKey、AuditExactKey；
- StableInstanceIdFactory、ChanceBranchFactory、RngContract；
- 修 GUID、文化相关 key、Power 来源合并；
- 门禁：hidden-state invariance、key mutation、stable-ID 双跑。

M3b 机会引擎：
- RandomOperatorRegistry；
- multiset/prefix DP 和 post-state aggregator；
- 随机 listener、turn hook、药水、目标/生成/稀有选择纳入 queue；
- unknown/sampled metadata 和质量门禁；
- 门禁：小池暴力枚举对拍、质量 1、未知不伪确定。

M3c 时序和敌人：
- event queue、death finalizer、动态 enemy state；
- MonsterAi public-probability provider；
- 明确单回合/多回合 horizon；
- 门禁：多敌 Doom、draw interleave、弱化后多段攻击、终点短路。

M3d 策略标签：
- root action aggregation；
- recursive contingent policy tree；
- TeacherEvaluator 只读策略树；
- 门禁：chance→Max、Top-K/soft policy/tie 稳定、截断质量降级。

M3e 真实闭环：
- CLI 执行 root action；
- ShadowDiff 比较 HP/Block/Energy/piles/Power/Relic/counters/stream consumption；
- 固定版本、双跑 SHA-256；
- mismatch=0 才晋级 Reliable。

M3f 性能和并行：
- COW、fingerprint、cache、root-shard；
- 1k → 10k → 100k；
- DatasetManifest、challenge set、峰值内存/节点/s；
- 单线程/多线程字节一致。

## 最小测试矩阵

belief_hidden_order_invariance
belief_exhaust_exclusion
belief_known_top_prefix_update
unknown_rng_never_becomes_zero
weighted_pool_mass_and_replacement
multiset_draw_AAB_probability
equivalent_post_state_aggregation
chance_then_max_policy
policy_tree_no_clairvoyance
stable_generated_instance_id
culture_invariant_key
power_source_separation
dynamic_enemy_state_per_hit
combat_terminal_short_circuit
all_random_listener_paths
truncation_confidence_gate
single_vs_parallel_byte_identity

每个 fixture 保存 public input、belief、action_id、policy tree、概率质量、confidence、sidecar（版本/计数/差分）；public 特征不得含 RNG raw words、未来牌序或 teacher-only 字段。

## 现在直接做什么

1. 暂停大规模教师数据和 GPU 训练。
2. 按 M3a → M3b → M3c → M3d 实施；卡牌/遗物覆盖 agent 可并行，但新语义必须通过同一 registry、key、strict-diff 门禁。
3. 第一批编码做 M3a 四件事：稳定生成 ID、belief normalizer、ChanceBranch 工厂、root policy aggregation；每件配最小反例。
4. P0 全绿后才开 1k NOSL smoke；结构化可执行不能直接写成 Reliable。
5. 9950X3D 先承担 CPU 搜索，32GB 先不升级；实测内存压力后再决定 64GB+。

## 参考资料

- 计划：D:\STS2BestChoice\STS2SuperModel\PLAN_NOSL.md
- headless/RNG 研究：D:\STS2BestChoice\STS2SuperModel\docs\research\headless-simulator-research-agent.md
- 参照架构：D:\STS2BestChoice\reference\CombatSolver\docs\ARCHITECTURE.md
- CLI：D:\STS2BestChoice\STS2SuperModel\sts2-cli-v0111
- StS2 多 PRNG/xoshiro 说明：[Steam 公告](https://steamcommunity.com/app/2868840/allnews/)
- 真实游戏 headless JSON CLI：[sts2-cli](https://github.com/wuhao21/sts2-cli)

本审查只新增建议文档和验证工件，没有修改影子模拟器源代码、标签或覆盖目录。


## P0-8：终点、自动播放和覆盖报告的具体修正项

最新遗物/卡牌返工结论：当前有 100 个遗物探针报告（98 handler-supported、2 PartiallySupported），另有 20 个未实现战斗 hook、25 个 UnverifiableByCli、56 个 Uncalculable，97 个非战斗遗物已标 OutOfScope。212 份 ShadowDiff 报告全部零 mismatch，但质量分级为 Reliable=96、Estimated=67、Uncalculable=49；1099 个单人卡牌变体聚合为 590 个无碰撞语义签名，仅 1 个签名完成全变体严格行为验证。该结果解决了“对象盘点”问题，但没有绕过 NOSL 的 M3 正确性门禁。`PARRYING_SHIELD` 仍缺多敌随机目标消歧，`UNCEASING_TOP` 仍缺空手触发证据。

以下是本次代码审查中可直接转为 fixture 的高风险点：

- Search Expand 和 DeterministicSimulator.PlayCard/UsePotion 入口都应先检查玩家死亡、敌方全灭；终点不能继续出牌或推进下一回合。
- ResolveEnemyDoom 不能在第一个满足条件的敌人后 break；多敌同时 Doom 要逐个结算并执行统一 CombatEnd。
- 回合结束毒、敌方反伤、Power/Relic 触发造成的最后一次击杀，也必须经过同一个 TryFinalizeCombat。
- CopyStateInto 当前容易漏复制 Powers、Relics、Gold、History、pending flags 和回合计数；优先改为集中 Clone/Replace，再用反射字段清单测试。
- Parrying Shield 的多敌场景必须重新探针：当前影子实现疑似对所有敌人造成伤害，而运行时语义是从可命中敌人中随机选一个。单敌 fixture 不能证明它已 Reliable。
- Unceasing Top 必须用“初始手牌恰好一张、打空后继续观察抽牌/触发”的场景验证；连续打三张并不能覆盖手牌清空钩子。
- 任何 TransformCards、RandomEnemyStatus、RandomEnemyDamage、随机 listener 未注册的 EffectKind 都应触发显式 coverage failure，不得静默 no-op。
- Dynamic amount、History counter、Condition 的未知值应返回 Unknown 三态，不可默认 0/false。
- 评分同时保存 gross damage、gross HP loss、net HP delta、death/kill；用净生命差直接表示风险会掩盖“受伤后回血”的真实成本。

## P0-9：训练导出和 CLI 入口

- TeacherEvaluator 必须从 public_state 重建 NOSL belief，拒绝 RNG streams、raw counters、ordered hidden piles 和 teacher_snapshot 进入主训练记录；teacher snapshot 只能 sidecar reference。
- 每个合法 root ActionCandidate 都要输出 value、mask、quality、限制原因；Top-K 不能替代全动作价值。
- 风险分布使用概率加权 variance、ESS、CVaR；异常或缺质量时 value=null、label_quality=Uncalculable，不能把 death_probability 填成 1。
- CLI 的输出流需要逐行 flush，增加 Popen streaming smoke；兼容性 metadata 同时记录 original DLL hash、runtime patched hash、patch profile。
- Public hash、belief signature、schema、legal action set、版本必须共同作为数据切分键，防止同一公共状态跨版本/跨信念混标签。

## 10：对 `reference/slay-the-spire-2-emulator` 的复用审查

该仓库 commit `04cfe6df800156e5339d565e03236302363c829c`（2026-06-03）声明 MIT License，包含约 40 个 C# 文件、卡牌效果、敌人 AI、遗物效果、回合引擎、run engine、NativeAOT interop、Gymnasium wrapper 和 198 个 C# 测试。README 明确说明它是 subset emulator，广泛 relic/run parity 仍是 future work。

### 可以复用

| 内容 | 用法 |
|---|---|
| `Core/Effects/CardEffects.cs` 的显式卡牌 case | 作为语义候选，转换为本项目 `EffectSpec`/handler，再用 v0.111 CLI ShadowDiff 验证 |
| `CombatEngine.cs` 的生命周期结构 | 参考 event queue、抽牌/弃牌/消耗和终点顺序，保留本项目 NOSL 状态契约 |
| `EnemyAI.cs` 的 move/state-machine 组织 | 参考 `EnemyAiBelief` provider，不把已知 seed 分支写入 NOSL 标签 |
| `RelicEffects.cs`、`BuffSystem.cs` 的拆分方式 | 参考 handler 组织和读写集合声明 |
| 测试、trace 和 Python/C# 边界 | 参考测试结构，不复用其 observation/action schema |

### 不直接复用

- `Core/Rng/DotNetRandom.cs`、`GameRng.cs`、`RunRngSet.cs`：与当前项目锁定的 v0.111 xoshiro256** 合约不同。
- 固定整数 observation、phase-dependent action index、NativeAOT ABI：与 `ActionCandidate`、stable ID 和训练 trace schema 不兼容。
- run/map/reward/shop 逻辑：subset 实现，尚未有本项目的 v0.111 CLI/ShadowDiff 证据。
- `decompiled/`：来自游戏程序集的反编译内容；仓库根 MIT 文件不足以证明其中派生内容可直接再发布，默认只作语义阅读和证据定位。

### 导入流程

1. 建立 `third_party/slay-the-spire-2-emulator/` 隔离副本，固定源 commit，并保留 MIT notice。
2. 每次只导入一个小模块或一张卡牌的候选逻辑，记录源文件、源行号和目标 handler。
3. 替换 RNG、状态模型、ID、观察 schema 和分支接口，不保留其 `DotNetRandom` 或固定 seed 假设。
4. 为同一 public root 添加 CLI↔shadow fixture；通过 `mismatch_count=0`、双跑一致、NOSL leakage=0 后再更新 catalog。
5. 未通过验证的导入代码标记 `Candidate/Estimated`，不进入 Reliable 主标签。

结论：可以复用 MIT 项目中有明确来源记录的仓库自有 C#/测试结构；优先采用“语义参考 + 独立重写 + CLI 对拍”，不整库复制，也不直接移植其 RNG、状态、ABI 或反编译文件。
