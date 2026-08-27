# STS2 单回合高手模型：完整工程计划

## 0. 计划元信息与固定决策

- **计划范围**：只深化“当前战斗玩家回合最优解”。
- **不纳入本计划**：地图、路线、事件、商店、奖励选择、完整牌局策略、多人/盟友战斗。
- **游戏版本**：StS2 `v0.111.0`。
- **游戏 commit**：`41cef1ea`。
- **程序集 SHA-256**：`0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9`。
- **当前 CLI 协议**：`0.2.0`；训练轨迹使用独立 trace schema `1`，不改变旧动作语义。
- **训练栈**：Python 3.12、PyTorch、Parquet/Arrow、JSONL 原始轨迹。
- **部署路线**：先离线评测，再导出 ONNX 接入 C#。
- **主算法**：Expectimax 作为教师和运行时兜底，学习模型负责排序、价值估计和风险估计。
- **不使用**：RandomForeseer 运行时、端到端动作序列作为首版主决策器、截图/OCR 训练输入。
- **硬件**：9950X3D、32GB RAM、12GB GPU，暂不升级内存。

## 1. 当前仓库基线

### 已有能力

- [DeterministicSimulator.cs](D:/STS2BestChoice/STS2BestChoice/STS2BestChoice.Core/Simulation/DeterministicSimulator.cs)
  - 出牌、药水、抽牌、弃牌、消耗、格挡、伤害、状态、敌人回合投影和回合推进。
- [LiveCombatSnapshotAdapter.cs](D:/STS2BestChoice/STS2BestChoice/Mod/LiveCombatSnapshotAdapter.cs)
  - 从实机读取战斗状态、牌堆、药水、充能球、历史计数器和七条 RNG 流。
- `CombatSearchSession`
  - 当前回合限时前沿搜索。
- `BattleSearchSession`
  - 当前回合结果向未来回合递归投影。
- `ExpectimaxEngine`
  - Max/Chance 节点、概率归一化、缓存和预算控制。
- `CardTextSemanticCompiler`
  - 版本化卡牌语义和模拟器 handler。
- `RandomModel`
  - v0.111 兼容的 xoshiro256** 状态和七条战斗 RNG 流。
- CLI v0.111 启动、地图和基础战斗协议已经迁移。

### 当前真实覆盖

根据当前代码、catalog 和验证报告：

- v0.111 全局卡牌语义：`1108/1176` fully structured；
- 单人战斗范围：`1099/1099` fully structured、simulator-executable、runtime-handler-resolvable；
- 剩余 `68` 个主要为多人/盟友效果，不属于当前单人范围；
- Core/Mod/SemanticCoverage Release 回归：`706 passed`；
- 仍有一个 Mod 侧预先存在的 `CS0436` warning；
- CLI v0.111 combat-scope 门禁：`36/36 GREEN`，`public_leakage_count=0`。
- P0 契约、结构化采集、数据导出和真实引擎↔影子差分的验收证据见
  [P0_VERIFICATION.md](D:/STS2BestChoice/STS2SuperModel/data/P0_VERIFICATION.md)。

### P0 已完成与后续缺口

P0 的状态契约、Power/遗物结构化采集、public/teacher 双视图、稳定动作 ID、
训练 trace/manifest、版本门禁以及真实 CLI↔影子差分闭环已经实现；当前固定矩阵
包含 P0/P1 Power/Relic 差分报告，所有已提交报告均为 `Reliable` 且
`mismatch_count=0`。详细命令和证据以
`data/P0_VERIFICATION.md` 为准。

仍需在 P0 之后逐步扩展的内容（不影响契约使用）：

1. 对尚未行为探针验证的 59 个已声明 Power 和 163 个已知未支持战斗遗物逐批实现
   simulator handler，并在验证后才提升为 `Reliable`。
2. 将当前 100 状态 smoke（已生成 Estimated fallback 标签）扩展至计划中的 1k/10k/100k 状态，
   并接入真实 CombatSearchSession/Expectimax evaluator 后生成 Reliable/Estimated 标签。
3. 完成模型输入/ONNX manifest、离线 RL 环境和模型评测报告。

## 2. 运行时架构

```text
真实游戏/CLI
    │
    ├── Public Observation
    └── Privileged Teacher Snapshot
             │
             ├── DeterministicSimulator
             ├── Expectimax Teacher
             └── Transition/Chance Labels
                         │
                    Dataset Builder
                         │
                  PyTorch Policy/Value/Risk Model
                         │
                    Offline Evaluation
                         │
                       ONNX
                         │
                  C# Search Integration
```

### 双视图原则

**学生视图 `PublicStateView`**

模型实际允许使用：

- 玩家 HP、最大 HP、格挡、能量、最大能量；
- 玩家状态、可见 Power、遗物 ID 和可用数值；
- 手牌、动态费用、类型、升级、关键词、可玩性；
- 敌人 HP、格挡、状态、意图、攻击次数；
- 药水、充能球、回合和历史计数器；
- 抽牌堆/弃牌堆数量；
- 合法动作和动作候选。

**教师视图 `TeacherStateView`**

仅用于搜索和标注：

- 完整抽牌堆顺序；
- 完整弃牌堆和消耗牌堆；
- 七条 RNG 流的四个状态字、`Counter`、`IsKnown`；
- 影子状态内部计数器；
- 未公开的引擎状态和精确后继状态。

模型不得直接使用 `run_seed`、原始 RNG 状态字或未来抽牌身份，避免训练泄漏。

## 3. 核心数据类型

### 3.1 RelicState

为 `CombatSnapshot` 增加可选的遗物结构：

```text
RelicState
  id
  owner
  amount
  dynamic_vars
  counters
  active
  trigger_phases
  semantic_tags
  numeric_modifiers
  handler_id
  confidence
  evidence
  source_version
```

需要区分：

- 遗物存在但尚未镜像；
- 遗物已结构化但 handler 不完整；
- 遗物已可被模拟；
- 遗物只影响长期牌局、不影响当前回合。

### 3.2 PowerState

保留现有 `StatusState` 作为模拟内部状态，同时增加 Power 来源信息：

```text
PowerState
  id
  owner_id
  applier_player_id
  amount
  dynamic_vars
  internal_counters
  canonical_status_ids
  trigger_phases
  confidence
  evidence
```

已有 Power 语义继续映射到 `StatusState`；`PowerState` 用于：

- 训练特征；
- 风险解释；
- 版本差异追踪；
- 检查“同一状态来自卡牌还是遗物/Power”。

### 3.3 CombatSnapshot 扩展

在现有 record 末尾增加可选字段，避免破坏当前调用方：

```text
player_relics
player_powers
enemy_powers
snapshot_provenance
observation_view
```

同时更新：

- `MutableCombatState.Clone`
- `ExactKey`
- `CycleKeyWithoutProgress`
- fingerprint 生成；
- 分支复制；
- 序列化和反序列化。

影响后续结算的遗物、Power、计数器必须进入状态 key。

### 3.4 ActionCandidate

动作不使用易变的卡牌索引作为主 ID：

```text
ActionCandidate
  kind: PlayCard | UsePotion | EndTurn
  source_model_id
  source_instance_id
  target_id
  choice_id
  selected_card_instance_ids
  effective_energy_cost
  legal
  restriction
```

CLI 的 `card_index` 和 `target_index` 只作为执行层映射；训练标签使用稳定的 `instance_id/model_id`。

### 3.5 TrainingDecisionRecord

```text
record_id
schema_version
game_version
game_commit
assembly_sha256
cli_protocol_version
simulator_version
scorer_version
generator_config_hash

run_seed
seed_group
episode_id
character
ascension
act
floor
encounter_id
combat_id
round
state_hash_public
state_hash_teacher

public_state
teacher_state_reference
legal_actions
action_candidates

teacher_best_actions
teacher_top_k
action_values
balanced_value
damage_value
loss_value

chance_outcomes
probability_mass
sample_count
confidence_interval
death_probability

confidence
risk_events
search_budget
expanded_nodes
search_complete
provenance
```

### 3.6 DatasetManifest

```text
dataset_id
schema_version
game_version
assembly_sha256
simulator_commit
teacher_config
feature_config
seed_ranges
split_policy
row_count
state_count
action_count
reliable_count
estimated_count
uncalculable_count
source_hashes
created_at
```

## 4. 遗物和 Power 结构化计划

### Tier 0：全部采集

对所有遗物和 Power 记录：

- 稳定 ID；
- 当前 amount/stack；
- DynamicVars；
- charges/counters；
- owner/applier；
- 是否 active；
- 是否影响当前战斗；
- 是否已模拟；
- 证据来源；
- 可信度。

CLI 当前只输出部分名称、描述和 vars；必须补充稳定 ID，不依赖本地化文本。

### Tier 1：当前回合关键语义

优先实现影响以下行为的遗物/Power：

- 能量和能量上限；
- 抽牌和生成牌；
- 卡牌费用；
- 伤害、格挡、力量、敏捷、Focus；
- 出牌前/出牌后触发；
- 受伤、格挡、击杀触发；
- 随机目标、随机生成和自动出牌；
- 弃牌、消耗、保留和结果牌堆位置。

### Tier 2：完整战斗语义

补齐：

- 回合开始/回合结束；
- 多回合计数器；
- 召唤物和伴生体；
- 牌堆跨回合变化；
- 卡牌永久成长；
- 由遗物/Power 产生的隐式动作。

### 证据分级

```text
ILConfirmed
LiveObserved
OfficialPatchNote
VersionedCardText
HeuristicInferred
Unknown
```

只有 `ILConfirmed`、`LiveObserved` 和已验证的版本数据才能进入 `Reliable` 主标签。未知效果保留原始对象并触发局部风险，不得当成无效果。

## 5. CLI 轨迹协议

### 兼容原则

- 现有 `0.2.0` 动作协议保持兼容；
- 新增独立 `trace_schema=1`；
- 只有在 trace 测试通过后，才将训练采集标记为可用；
- 版本门禁继续检查游戏版本、commit 和程序集哈希。

### 必须支持的能力

1. `start_run` 返回兼容性和 trace metadata；
2. 每个决策点包含：
   - `trace_id`
   - `step`
   - `pre_state_hash`
   - `decision`
   - `public_observation`
3. 每个 action 返回：
   - 动作请求；
   - 动作规范化 ID；
   - `post_state_hash`；
   - 实际结果；
   - 新决策点；
   - 是否产生随机分支。
4. 增加 `get_combat_snapshot`：
   - `view=public`
   - `view=teacher`
5. 支持将完整轨迹写入 JSONL；
6. 轨迹中断时写入失败原因，不丢弃此前有效步骤；
7. `quit` 前保证 trace flush。

### CLI 训练采集测试

- 相同 seed、相同动作序列，两个进程的 public trace hash 一致；
- teacher snapshot 能恢复到同一影子状态；
- action 前后 RNG counter 差异正确；
- 卡牌索引移动后稳定 action ID 仍可回放；
- `end_turn` 后状态推进和下一回合手牌一致；
- 旧 `0.2.0` 客户端仍能执行普通命令。

## 6. 数据生成管线

### 四类 worker

1. **EngineWorker**
   - 启动真实 CLI；
   - 生成固定 seed 对局；
   - 保存真实状态和动作结果。

2. **CounterfactualWorker**
   - 从真实快照克隆状态；
   - 使用 `DeterministicSimulator` 生成未实际执行的动作结果；
   - 保存模拟来源标记。

3. **TeacherWorker**
   - 对决策状态运行高预算 Expectimax；
   - 生成多目标标签、机会分支和策略树。

4. **VerifierWorker**
   - 重新执行固定 seed；
   - 比较引擎和影子结果；
   - 生成差分报告。

### 行为策略覆盖

不能只采集专家动作，需要同时采集：

- 当前启发式策略；
- 随机合法动作；
- 低温度 Expectimax；
- 高预算教师动作；
- 接近最优但最终遗憾较大的动作；
- 低血量、低能量、多敌人、复杂选择、随机效果状态；
- 专门构造的稀有遗物/Power/卡牌组合。

CLI 现有 `set_player`、`set_draw_order`、`enter_room` 能用于建立可控测试夹具。

### 数据规模

- Smoke：1,000 个决策状态；
- Pilot：100,000 个决策状态；
- 第一版扩展：1,000,000 个决策状态；
- 训练样本按动作候选数扩展，不复制同一状态的完全重复标签。

### 分层配额

Pilot 默认：

- 铁甲战士 50%；
- 静默猎手 50%；
- A0 40%、A5 30%、A10+ 30%；
- 普通战斗、精英、Boss 分层；
- 单敌人、多敌人、随机分支、自动出牌、卡牌选择分别设最低配额；
- 可靠、估计、不可计算样本分开统计。

### 存储

```text
training/
  schemas/
  collectors/
  raw/cli-v0.111/
  raw/snapshots/
  labels/
  datasets/train/
  datasets/validation/
  datasets/test/
  challenge/
  manifests/
  reports/
  checkpoints/
  exported/
```

- 原始轨迹：压缩 JSONL；
- 训练数据：Parquet/Arrow；
- 每个 shard 10,000–50,000 行；
- 不将全部 replay 一次性读入内存；
- 每个 shard 保存 SHA-256 和生成配置。

## 7. Expectimax 教师标签

### 主教师

当前回合标签以 `CombatSearchSession` 为主，因为它包含：

- 合法动作生成；
- 动态选择；
- 随机分支；
- 当前回合终点投影；
- 现有 `CombatScorer`；
- 风险和可信度。

`ExpectimaxEngine` 继续作为纯算法回归测试和简化教师验证。`BattleSearchSession` 只用于可选的未来回合审计，不作为首期主要标签来源。

### 三个目标

同时保存：

1. `Balanced`
2. `HighestDamage`
3. `MinimumLoss`

`Balanced` 为默认运行目标；模型保留三个价值头，运行时按目标选择。

原始标签同时保存：

- 有效伤害；
- 玩家 HP 损失；
- 击杀和威胁移除；
- 长期状态/充能球价值；
- 手牌和资源价值；
- 药水机会成本；
- `BalancedScore`；
- 预计未来回合数；
- scorer 版本和权重。

不能只保存一个最终分数，否则以后无法重新调整目标。

### 随机分支

- 分支数不超过 32：精确枚举；
- 超过 32：确定性分层采样；
- 记录抽样 seed、样本数、概率质量、均值、方差和 95% 区间；
- 不允许用 `Take(32)` 直接截断；
- 机会分支后重新求 Max；
- 概率质量不足时标签为 `Estimated`；
- 未知 RNG 或未知语义不伪造均匀分布。

### 标签质量等级

```text
ExactComplete
ExactWithKnownChance
SampledWithConfidenceInterval
BudgetBound
EstimatedByHeuristic
Uncalculable
```

训练权重：

- `ExactComplete`：1.0；
- `ExactWithKnownChance`：1.0；
- `SampledWithConfidenceInterval`：0.5–0.8；
- `BudgetBound`：0.25；
- `EstimatedByHeuristic`：只用于辅助价值/风险；
- `Uncalculable`：不用于最优动作主损失。

### 隐藏状态聚合

同一 `public_state_hash` 可能对应多个 teacher state：

- 保存所有隐藏状态；
- 计算动作价值均值和方差；
- 生成 soft action distribution；
- 记录最优动作是否随隐藏状态改变；
- 不把多种真实可能性压成一个错误的硬标签。

## 8. 学生模型

### 输入编码

- 玩家数值特征：标准化、裁剪和缺失 mask；
- 手牌 token；
- 敌人 token；
- 遗物 token；
- Power/status token；
- 药水 token；
- 充能球 token；
- 历史计数器；
- 当前回合和动作阶段；
- 合法动作 mask；
- 可信度和未知效果 mask。

不输入：

- 原始 seed；
- 原始 RNG 四个状态字；
- 未来抽牌身份；
- 本地化说明文字；
- 图片像素。

### 动作打分结构

采用 `f(state, action)`，对当前合法候选逐个评分：

```text
shared_state_encoder
        +
action_candidate_encoder
        ↓
action_score
```

这样不依赖固定卡牌索引，也能处理：

- 手牌索引移动；
- 目标数量变化；
- 多卡选择；
- 药水槽位变化；
- 动态生成牌；
- `END_TURN`。

### 模型规模

先训练两个基线：

1. 规则/当前 `CombatScorer`；
2. 5–10M 参数候选动作 MLP。

主模型：

- entity/action encoder；
- 2–4 层 attention；
- hidden size 256；
- 15–30M 参数；
- FP16；
- 最大 token 数由实际状态分布压测确定；
- 不在 12GB GPU 上训练大语言模型或长序列模型。

### 输出头

- `PolicyHead`
- `BalancedValueHead`
- `DamageValueHead`
- `LossValueHead`
- `DeathRiskHead`
- `ConfidenceHead`
- `Abstain/FallbackHead`

## 9. 训练阶段

### M0：版本和基线门禁

交付：

- CLI 完整回归结果；
- v0.111 版本门禁；
- 单人语义覆盖报告；
- 固定 seed 回放报告；
- 当前 teacher/shadow 差分基线。

出口：

- CLI 全量测试完成；
- `start_run → combat → end_turn` 完整回放通过；
- 关键 RNG 流对照通过。

### M1：状态契约和语义目录

交付：

- `CombatSnapshot` 扩展；
- `RelicState`、`PowerState`；
- public/teacher 双视图；
- relic/power catalog；
- canonical hash；
- schema validator。

出口：

- 所有现有核心测试保持通过；
- 遗物和 Power 不再只以文本或 risk 存在；
- 未知对象可被完整记录。

### M2：CLI Trace Exporter

交付：

- trace schema；
- full snapshot endpoint；
- action 前后状态；
- RNG before/after；
- replay 工具；
- CLI consistency tests。

出口：

- 固定 seed trace 可逐步重放；
- trace 能转换为 `TrainingDecisionRecord`。

### M3：1,000 状态 Smoke

当前进度：已完成 100 状态 smoke 闭环；100/100 条记录具有非空
`teacher_best_actions`，当前标签来自明确标记为 `Estimated` 的 fallback，尚未作为
Reliable 教师数据使用。

交付：

- 原始 JSONL；
- 规范化 Parquet；
- schema 统计；
- 卡牌/遗物/Power/敌人覆盖报告；
- teacher label 质量报告。
- `training/collectors/teacher_worker.py` 与 `training/labels/teacher_label_schema_v1.json`；
- `data/teacher-smoke-100.jsonl`、Parquet、manifest、隐藏状态聚合和 quality gate。

出口：

- 100% 可解析；
- 100% 合法动作可回放；
- 无版本混杂；
- 无 seed 泄漏。

### M4：100,000 状态 Pilot

交付：

- train/validation/test/challenge 四套数据；
- 三种目标标签；
- chance 分支统计；
- 真实引擎与影子差分；
- 数据分布可视化和错误样本清单。

出口：

- 可靠样本比例达到预设目标；
- unknown/uncalculable 样本被单独统计；
- 近最优 hard negative 足够；
- 可重复重建数据集。

### M5：监督训练

损失：

```text
L = policy_rank
  + value_balanced
  + value_damage
  + value_loss
  + death_risk
  + confidence
```

训练顺序：

1. 合法动作 mask/动作编码；
2. 教师 soft policy；
3. 动作排序；
4. 三种价值；
5. 风险和弃权；
6. 温度校准。

数据切分按 `run_seed/episode`，不能按单行随机切分。

### M6：离线 RL

环境只覆盖当前回合：

```text
reset(snapshot)
legal_actions()
step(action)
chance_outcome()
end_turn()
```

终止条件：

- `END_TURN`；
- 玩家死亡；
- 敌人全部死亡；
- 动作上限；
- 状态不可计算。

奖励采用现有 `ScoreBreakdown` 的潜势差：

```text
reward = Potential(next_state) - Potential(state)
```

附加终局项：

- 存活；
- 击杀；
- 死亡；
- 预计 HP；
- 三种目标独立统计。

默认使用 IQL/AWR 风格的保守离线 RL。只有模拟器差分和监督模型门禁通过后，才考虑 masked PPO 的受约束在线微调。

### M7：离线影子评测

模型不影响搜索，只记录：

- 模型 Top-K；
- Expectimax Top-K；
- 排名差异；
- 价值差异；
- 置信度；
- fallback 原因；
- 预测和真实结果。

### M8：ONNX/C# 接入

增加可选接口：

```text
IActionPrior
ILeafValueEstimator
IRiskEstimator
```

运行规则：

1. 先由核心枚举真实合法动作；
2. 模型只给候选排序；
3. 保留 `END_TURN`、可靠击杀、防御和未知效果动作；
4. Top-K 剪枝前检查模型置信度；
5. 低置信度时关闭学习剪枝；
6. 可靠模拟值优先于模型叶值；
7. 随机边界后重新捕获状态；
8. 不自动结束回合；
9. 模型不可改变 `PredictionConfidence`。

## 10. 模型 Manifest 和版本门禁

每个 ONNX 模型必须附带：

```text
model_id
model_sha256
schema_version
game_version
game_commit
assembly_sha256
simulator_version
scorer_version
feature_view
normalization_hash
training_manifest_hash
calibration_version
supported_characters
supported_ascensions
```

加载失败、版本不匹配、schema 不匹配或 normalization 不匹配时：

```text
模型禁用 → 现有 Expectimax → 记录 fallback 原因
```

模型文件、数据集、标签生成器和模拟器必须分别可回滚。

## 11. 并行模拟与硬件

### 当前配置

- 9950X3D：负责真实 CLI、影子模拟、Expectimax、预处理；
- 32GB RAM：先不升级；
- 12GB GPU：负责 PyTorch 训练和批量推理。

### 初始并行参数

- 真实 CLI：4 个持久 worker；
- 影子模拟：根据内存动态增加；
- 总内存使用超过 80% 时停止增加 worker；
- 每个 worker 独立 seed shard；
- 不共享可变 RNG；
- 训练 DataLoader 使用小规模 worker 和流式读取；
- GPU 使用 FP16，物理 batch 128–256，梯度累积至有效 batch 512–1024。

### 升级触发条件

只有出现以下任一情况才升级内存：

- 长时间超过 80–85% 内存占用；
- swap/pagefile 持续增长；
- CLI worker 因内存失败；
- Parquet 预处理无法流式完成。

升级顺序：

```text
32GB 保持不变
→ 64GB
→ 测量吞吐
→ 再决定是否需要更大 GPU/内存
```

## 12. 测试计划

### 版本和 CLI

- ready 版本、commit、程序集 hash；
- 固定 seed 启动一致；
- 地图、战斗和 `end_turn`；
- 旧协议兼容；
- full trace replay；
- CLI 进程异常和中断恢复。

### 状态和 schema

- public/teacher 视图字段隔离；
- canonical serialization 稳定；
- decimal 数值无意外舍入；
- 状态 hash 对顺序敏感字段正确；
- 缺失字段和未知对象有明确 mask；
- action round-trip 正确。

### 模拟器

- 每种 ActionCandidate 的合法性；
- 卡牌索引移动；
- 目标和多卡选择；
- 药水；
- 生成牌；
- 自动出牌；
- 结果牌堆优先级；
- RNG counter；
- Chance probability mass；
- exact/sample 分支；
- 死亡、胜利和回合结束。

### 遗物和 Power

每个结构化对象至少有：

- 捕获测试；
- amount/vars 测试；
- trigger phase 测试；
- simulator transition 测试；
- 未知对象风险测试；
- 版本差异测试；
- teacher/public view 泄漏测试。

### 模型

- 非法动作永不输出；
- 空合法动作集正确处理；
- permutation/index shift；
- unknown effect 自动弃权；
- ONNX 与 PyTorch 输出误差；
- normalization 一致；
- batch 和单样本结果一致；
- 模型超时回退。

## 13. 验收指标

### 数据门禁

- schema 解析率：100%；
- 版本 metadata 完整率：100%；
- 合法动作重放率：100%；
- seed/episode 泄漏：0；
- 支持语义的 CLI/影子差分：目标 ≥99.5%；
- 未知效果不得被标记为 Reliable。

### 模型离线门禁

- 合法动作率：100%；
- Expectimax 最优动作进入 Top-16：≥99%；
- 可靠状态 Top-1 一致性：≥85%；
- 相对完整教师的期望遗憾：≤2%；
- 死亡概率 ECE：≤0.05；
- chance 置信区间覆盖接近名义 95%。

### 真实引擎门禁

固定 seed challenge set 对比：

1. 当前完整 Expectimax；
2. 模型排序；
3. 模型排序 + 叶节点价值；
4. 端到端实验模型。

发布条件：

- 混合模型胜率不低于完整 Expectimax 超过 1 个百分点；
- 死亡率不显著增加；
- 无非法动作；
- 未知遗物/Power 正确回退；
- 模型失败不影响现有搜索。

## 14. 数据和模型质量监控

每次训练记录：

- 状态数量和动作数量；
- 角色、难度、敌人和遗物覆盖；
- Reliable/Estimated/Uncalculable 比例；
- chance 分支数量和概率质量；
- teacher 搜索完成率；
- Top-K 召回；
- regret；
- calibration；
- fallback 率；
- ONNX/PyTorch 差异；
- 真实引擎差分失败样本。

每个模型发布前生成：

```text
dataset_report.json
teacher_report.json
model_metrics.json
challenge_report.json
compatibility_manifest.json
```

## 15. 主要风险与处理

| 风险 | 处理 |
|---|---|
| 教师评分函数偏差 | 保存原始分量和 scorer 版本，不只保存总分 |
| 隐藏牌堆泄漏 | public/teacher 双视图和 schema 测试 |
| 遗物未镜像 | 保留原始遗物、局部 risk、模型弃权 |
| Power 来源丢失 | 增加 PowerState 和来源证据 |
| RNG 调用顺序变化 | 每版本锁定流状态和消费计数 |
| 影子模拟器漂移 | CLI 差分回放和挑战集 |
| 数据只包含高手动作 | 加入随机、近最优和反例动作 |
| 类别极不平衡 | 分层采样和 hard-negative |
| 模型奖励投机 | 向量化目标、真实引擎验收 |
| 12GB GPU OOM | 小模型、FP16、梯度累积、流式数据 |
| CLI worker 崩溃 | 独立 shard、断点清单、自动重试 |
| 新版本加载旧模型 | manifest 硬门禁和自动回退 |
| ONNX 输出不一致 | PyTorch/ONNX 对照测试 |

## 16. 计划完成定义

单回合模型达到以下状态后，才算首版完成：

1. v0.111 CLI trace 可重复采集；
2. public/teacher 状态契约冻结；
3. 遗物和 Power 可结构化记录；
4. 100,000 状态 pilot 可重建；
5. Expectimax 教师标签可追溯；
6. 监督模型通过离线门禁；
7. IQL/AWR 实验不会降低安全指标；
8. ONNX 与 PyTorch 输出一致；
9. C# 影子模式可记录模型和教师差异；
10. 真实固定 seed challenge set 不劣于完整 Expectimax；
11. 版本不匹配、未知效果和模型异常都能自动回退。

## 17. 明确不做的事情

- 不把 seed 直接作为策略特征；
- 不把 RandomForeseer 编译进运行时；
- 不用自然语言描述替代结构化卡牌、Power 和遗物语义；
- 不让端到端模型首版直接控制完整动作序列；
- 不在当前计划中加入地图和完整牌局决策；
- 不把未验证的遗物/Power 伪装成可靠效果；
- 不因为模型预测失败而破坏现有 Expectimax。

本轮只在回复中提供这份完整计划，未修改 `D:\download\PLAN.md`。
