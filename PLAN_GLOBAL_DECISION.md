# STS2 v0.111 NOSL 全局决策实施计划

> 研究更新：2026-08-31；已纳入 STS1/STS2 卡组评估、条件化反事实价值、候选屏排序和模块化神经网络结论。当前 `.run`/高手视频辅助数据阶段标记为 `DEFERRED`。

## 一、目标与固定决策

建立独立的局外决策系统，处理：

- 地图路线；
- 战斗后奖励；
- 商店购买/移除；
- 篝火休息/锻造；
- 普通事件和先古之民；
- Boss/宝箱奖励；
- proceed/leave 和嵌套选择。

战斗仍由现有 `CombatPolicy + DeterministicSimulator + Expectimax` 负责，只向全局层提供版本化 `CombatSummary`，不扩大全局动作到逐卡牌层。

已锁定：

- 首期：v0.111、单人标准模式、Act 1；
- 路线算法：风险约束 Beam/DP；
- 教师 horizon：3–5 个局外节点；
- 模型：模块化多头；
- 目标：通关率优先，死亡风险硬约束；
- 数据规模：`1k → 10k → 100k`；
- `.run` 与高手视频辅助：**暂缓采集、解析、人工标注和训练接入**；已有文件只保留原始归档，不进入当前 active dataset；
- 部署：Python → ONNX → C#；
- Self-play 后置；
- 正式计划独立落盘：

```text
D:\STS2BestChoice\STS2SuperModel\PLAN_GLOBAL_DECISION.md
```

### 1.1 研究结论转化为固定设计原则

公开的 STS1/STS2 项目显示，卡组价值不是一张牌的永久 Tier，而是给定状态、候选集合和未来风险的**条件增量价值**。本计划固定采用：

```text
V_run(s | public_context)
Q_global(s, a) = E_hidden[ V_run(T(s, a, hidden)) ]
ΔV(s, a) = Q_global(s, a) - V_run(s)
```

其中 `hidden` 由符合当前公共观测的隐藏状态/概率分布构成；`run_seed` 只在离线重放、教师审计和切分中使用。线上策略不接收未来地图、未来牌堆或未发生 RNG。

卡牌获取价值拆成四个可诊断目标，不合并成一个不可解释的分数：

```text
ΔV_next_fight   下一场战斗生存、预期 HP 损失和药水成本
ΔV_act          当前 Act 的即时伤害/防御/成长/敌群覆盖
ΔV_run          到 Boss/Heart 的通关概率和楼层生存
ΔRisk           死亡概率、低 HP 尾部、方差/CVaR 和资源断裂
```

`Skip`、不购买、离开和不锻造都是显式候选。外部 Tier、Elo、pick rate、高手视频和 `.run` 选择只属于 `ExternalPrior` 或 `ObservedHuman`，用于冷启动、采样和辅助损失，不直接生成 `Reliable` 标签。

### 1.2 两脑分层与范围边界

本计划保持两个相互调用但独立验收的模型：

```text
CombatPolicy + DeterministicSimulator + Expectimax
        └── CombatSummary（战斗成本/风险）

GlobalPolicy（Route/Reward/Shop/Campfire/Event/Ancient）
        └── 以 CombatSummary 为输入，不展开逐牌战斗动作
```

战斗逐步规划使用搜索，局外卡组/路线判断使用候选条件化网络。首版不把所有动作压进一个共享离散动作头；共享编码器只有在各模块分别通过门禁后再评估。

### 1.3 当前数据源决策（2026-08-31）

当前全局模型先走**合成状态 + 反事实教师**路线。`.run` 解析扩展和高手视频人工标注保留接口与目录约定，但状态标记为 `DEFERRED`：

```text
active:
  CLI / deterministic shadow / Expectimax / synthetic scenario generator
deferred:
  .run observed behavior
  expert-video manual labels
```

在 `global_teacher` 达到首个可用基线、版本门禁稳定且需要行为校准之前，不启动这两类辅助数据的采集和训练。恢复时仍使用独立 `ObservedHuman` manifest，不改写已有 teacher 标签。

恢复条件：`global_teacher` 至少完成一个通过 strict diff 的 pilot、`GlobalModelManifest` 可重建、公共状态分布和主要决策类型已有覆盖；届时单独开启 G2，并重新审查 `.run` 的 SL/版本/来源质量。

---

## 二、核心架构

```text
CLI / synthetic states / teacher worker
        ↓
GlobalRunState Public View
        ↓
GlobalDecisionOrchestrator
        ├── RoutePlanner / RouteValueModel
        ├── RewardPolicy / CandidateValueModel
        ├── ShopPolicy
        ├── CampfirePolicy
        └── Event/AncientPolicy
                 ↓
        CombatSummary Adapter
                 ↓
        现有 CombatPolicy + Expectimax
```

全局系统按 node/room 决策，属于风险约束 semi-MDP/POMDP。

### `DeckFeatureEncoder`（全局共享候选表示）

卡组使用集合、上下文和关系三路特征，不使用固定 STS1 one-hot 维度：

```text
Deck tokens:
  stable semantic_id / count / upgrade / enchantment / quest / temporary flags
Relic/Power/Potion tokens:
  stable id / counters / owner / charges / active state
Context:
  character / act / floor / ascension / HP / maxHP / gold / route / public encounters
Derived deck health:
  frontload single/AoE / block-mitigation / scaling / draw-energy
  first/subsequent cycle / cost curve / dead-draw/status burden
  upgrade density / dilution / encounter coverage / synergy and anti-synergy graph
Candidate token:
  complete card/relic/option semantics + cost + legality + opportunity cost
```

`DeckFeatureEncoder` 先以 DeepSets/attention 或候选条件化 MLP 实现；候选评分函数接收同一公共状态下的整个 offer set，并把 `Skip` 作为普通 token。模型不负责推断合法性，合法动作由 CLI/引擎枚举并提供 mask。

### `DeckHealth` 可解释派生指标（仅 baseline/诊断）

以下指标用于冷启动、采样配额和误差分析，最终数值由 teacher 校准，不设为硬规则：

```text
cycle_time_first       = expected cards needed for first engine cycle
cycle_time_later       = expected cards needed after exhaust/Power effects
frontload_dmg / aoe    = first 1–2 turns, single/multi-target output
block_density          = mitigation cards / playable cards
upgrade_density        = upgraded instances / deck size
dead_draw_rate         = expected unplayable/status/curse draw share
draw_energy_rate       = draw, filtering, energy/star generation per cycle
encounter_coverage     = weighted coverage of public next-fight profiles
synergy_graph          = enabler → payoff → finisher and anti-synergy edges
```

`floor/ceiling`、卡组大小、费用曲线和原型 commitment 仅作为软特征；同一卡牌在不同候选集合、敌群和 HP 状态下允许得到相反的 `ΔV`。

### `CombatSummary`

```text
source_state_hash
model_version
scenario_hash
expected_win_probability
expected_hp_loss
death_probability
expected_turns
potion_consumption
confidence
quality
source_provenance
hidden_state_policy
teacher_horizon
uncertainty_interval
```

---

## 三、数据契约

### 1. `GlobalRunKey`

```text
schema_version
feature_schema_version
game_version
game_branch
game_commit
assembly_sha256
cli_protocol_version
simulator_version
semantic_catalog_version
scorer_version
model_version
character
player_count
ascension
game_mode
modifiers
unlock_profile_hash
run_seed
run_context_hash
manifest_hash
```

seed 只能用于重放、数据切分和审计，不能进入模型特征。

### 2. `GlobalRunStatePublic`

```text
run_context_hash
episode_id
branch_family_id
act / floor
current_node
visible_map_graph
reachable_nodes
current_room_type
visible_options
visible_offers
legal_actions
hp / max_hp / gold
deck_public
relic_public
potion_public
public_history
combat_summary
field_completeness
public_state_hash
provenance
```

`deck_public`、`relic_public` 和 `potion_public` 必须保留可见的结构化状态，而不是只保存名称：

```text
deck_public.card_instances[]:
  card_instance_id          # 稳定跨进程 ID
  semantic_id
  upgrade_level
  enchantment_ids[]
  temporary / generated / quest / exhaust_state
relic_public[]:
  relic_instance_id / semantic_id / counters / charges / active_state
potion_public[]:
  potion_instance_id / semantic_id / charges / slot
```

牌堆中未公开的顺序、未来抽牌和隐藏计数器不进入 Public View；缺失字段必须写入 `field_completeness`，不能静默补默认值。

### 2.1 `GlobalRunStateTeacher`（仅标签与审计）

```text
public_state_hash
hidden_state_id
hidden_state_distribution
rng_snapshot / rng_counter
ordered_pile (optional)
teacher_only_powers / relic_counters
teacher_version
oracle_flag (NOSL=false; SEED_ORACLE=true)
```

该视图只由教师 worker 和审计 sidecar 使用，永不序列化到部署模型输入。若使用 seed 预知未来，只能生成 `SEED_ORACLE` 审计上界，不能作为 NOSL 策略样本。

### 2.2 `GlobalOfferSnapshot`

每个奖励、商店、事件、篝火和 Boss 选项都保存完整候选集合：

```text
offer_snapshot_hash
decision_type
candidates[]                 # 包含 Skip/Leave/Don'tBuy 等动作
candidate_order
visible_context_hash
legal_actions_complete
source (cli | run | manual | teacher)
```

候选集合采用长表存储：一行对应一个候选动作，同时保留同屏其它候选，以支持 pairwise/list-wise 训练和候选屏级切分。

Public View 使用允许列表，禁止出现：

```text
raw RNG
未来节点内容
未访问遭遇
未来商店/奖励
teacher snapshot
ordered future pile
actual hidden outcome
```

允许进入 Public View 的未来信息仅限当时游戏界面真实展示的 `visible_map_graph`、路线可达性和已公开选项；seed 生成的完整未来地图/奖励必须放入 teacher/audit sidecar。

### 3. `GlobalActionCandidate`

```text
action_id
action_type
semantic_id
transport_action
transport_args
target_id
legal
restriction_reason
candidate_index
offer_snapshot_hash
parent_decision_id
continuation_id
candidate_role (offer | skip | leave | rest | smith | route | option)
candidate_semantic_features_hash
opportunity_cost
source_confidence
```

索引只能作为 CLI 传输参数，不能作为跨进程主键。

动作命名空间：

```text
route:map:{act}:{row}:{col}
reward:offer:{snapshot_hash}:{offer_id}
reward:skip
shop:card:{snapshot_hash}:{offer_id}
shop:relic:{snapshot_hash}:{offer_id}
shop:potion:{snapshot_hash}:{offer_id}
shop:remove
shop:leave
campfire:rest
campfire:smith:{card_instance_id}
event:{event_id}:option:{option_id}
ancient:{ancient_id}:option:{option_id}
boss_reward:{offer_id}
proceed
leave
```

### 4. 独立记录类型

- `GlobalTransitionRecord`
- `GlobalDecisionRecord`
- `GlobalTeacherRecord`
- `GlobalDeckEvaluationRecord`
- `GlobalPriorRecord`
- `GlobalDatasetManifest`
- `AuditTeacherSidecar`

不要复用战斗专用 `TrainingDecisionRecord`。

`GlobalDeckEvaluationRecord` 至少包含：

```text
decision_id / offer_snapshot_hash
state_public_hash
candidate_action_id
delta_v_next_fight
delta_v_act
delta_v_run
delta_risk
expected_hp_loss_delta
teacher_mean / variance / ci95
label_source
quality
```

`GlobalPriorRecord` 保存外部统计或人工判断的来源 URL、抓取时间、查询参数、版本、样本量和偏差说明；它与 `GlobalTeacherRecord` 分表、分 manifest，禁止混作 Reliable 标签。统计 prior 只能在训练 split 内拟合，并记录：

```text
prior_fit_split
prior_fit_group (character / act / ascension / floor)
prior_shrinkage
prior_sample_count
```

验证、测试和 challenge split 不得反向更新 prior。

---

## 四、实施阶段

### G0：版本、Schema 和 NOSL 边界

交付：

- 新建 `PLAN_GLOBAL_DECISION.md`；
- 冻结 RunKey 和三视图；
- 冻结 `DeckFeatureSchema`、候选屏长表和 `label_source` 枚举；
- 冻结 action/quality/provenance 枚举；
- 固定 `game_branch/catalog/scorer/feature_schema` 的版本绑定；
- 版本不匹配自动拒绝；
- seed 从模型特征中排除。

出口：

- Schema 解析率 100%；
- public leakage=0；
- `ExternalPrior`/`ObservedHuman` 与 teacher manifest 隔离；
- unknown semantic ID 进入 OOV/Uncalculable，不静默映射；
- 不修改 CombatPolicy。

### G1：CLI 全局协议与稳定动作

补齐：

- `get_run_context`；
- `event_id/page_id/option_id`；
- shop `offer_id/model_id`；
- reward/bundle/Boss reward ID；
- Ancient ID；
- leave/proceed；
- nested card-select；
- index→stable ID 映射。
- 每个 screen 输出完整 legal set、`Skip/Leave/Proceed/Don'tBuy` 和未知候选；
- 若采用 top-K 屏筛选，只能作为推理加速，必须记录 `screen_version`、`omitted_ids` 并在执行前重新检查完整 legal set；
- 未知或未能计算的候选进入 safety set，不能因未入 top-K 而静默丢弃。

地图：

- `get_map` 只表示当前 Act 玩家可见拓扑；
- 不预填未访问节点的具体内容；
- node ID 固定为 `map:{act}:{row}:{col}`。

出口：

- 每种决策均有完整合法动作集；
- 重复 reward/shop 项仍有唯一 ID；
- action transport mapping 可重放。
- 候选集合顺序、遗漏项和重新校验结果可重建；

### G2：GUI、`.run` 和人工行为数据（暂缓辅助接入）

当前状态：`DEFERRED`。GUI/解析器可以保留接口和未来回归样例，但本阶段不启动 `.run` 行为导入、高手视频人工标注或相应训练作业；G2 不作为 `global_teacher` 首个基线的前置门禁。

GUI（保留接口，不启动辅助采集）：

- 自动渲染当前 event/shop/reward/rest/Ancient 选项；
- 保存 pre/post state hash；
- 支持 checkpoint、branch、partial episode；
- 增加 unresolved/mismatch 审查页；
- 增加 teacher/provider 操作页；
- seed 地图/未来内容预览仅放在 `privileged/audit` 视图，禁止写入 Public View；
- 对每个候选记录 `candidate_order`、Skip/Leave、人工备注和 `label_source`。

`.run`（恢复辅助数据阶段后）：

- 仅作为已发生的局外行为和结果；
- 默认 `sl_status=unknown/sl_possible`；
- 不自动标为高手或最优；
- 缺 legal set 时 `legal_actions_complete=false`；
- 修复 `ancient_chosen` generator 序列化问题；
- 保留 raw `.run` 与 SHA-256；
- 解析 `card_choices`、shop、event、ancient、campfire、route 的完整候选集合和 `was_picked`；
- `.run` 行为只进入 `ObservedHuman`/辅助 manifest，不产生 `CounterfactualTeacher`。

出口：

- 一局 Act 1 可重建为 `global_behavior.jsonl`；
- partial/SL 数据只进辅助集；
- 每条行为记录都携带 `source_provenance`、`sl_status`、`legal_actions_complete` 和版本 hash。

### G3：路线对齐与精确 checkpoint

地图对齐分级：

```text
exact
constrained
ambiguous
unavailable
```

修复：

- `rest_site` 与 `RestSite` canonicalization；
- 各 Act 独立地图缓存；
- 同一 row 多节点不得误判 exact；
- ambiguous 记录候选 node IDs。

Route candidate 的可见特征至少包括：

```text
node_type_counts / elite_count / campfire_count / shop_count
path_length / reachable_branch_count
public_encounter_distribution
expected_hp_cost / death_risk / recovery_opportunity
map_uncertainty_or_entropy
```

`public_encounter_distribution` 和 `map_uncertainty_or_entropy` 只能由当前可见拓扑、历史先验和已公开遭遇估计；完整 seed 地图不得参与这两个 Public 特征。完整 seed 地图只能用于离线审计或构造与公共观测一致的隐藏世界集合，不能把真实未来 seed 当作单一已知世界喂给 NOSL teacher。路径熵、精英数量等是候选特征，不是固定“精英越多越好”的规则。

Checkpoint：

- 当前非 MapRoom save 会回滚至入房前，不能直接用于房间内反事实；
- route 使用 pre-node checkpoint；
- event/shop/rest/reward 使用 exact-room serialization 或独立进程 fork；
- checkpoint 绑定完整版本、RNG counter 和 hash；
- restore 后必须得到相同 decision/options/hash。

### G4：全局语义 Catalog

建立：

- `MapNodeCatalog`
- `EventCatalog`
- `AncientCatalog`
- `ShopOfferCatalog`
- `RewardOfferCatalog`
- `CampfireCatalog`
- `BossRewardCatalog`
- `DeckSemanticCatalog`
- `ExternalPriorCatalog`（与真实语义和 teacher 分离）

语义只能来自：

```text
真实 CLI action
→ pre/post diff
→ semantic handler
→ 版本化证据
```

本地化文字仅用于显示，不从描述文本推断数值效果。`DeckSemanticCatalog` 必须保留：

```text
semantic_id / stable_id
game_branch / game_version / assembly_sha256
cost / type / target / base_vars / upgraded_vars
keywords: draw/discard/exhaust/ethereal/retain/void/generated/quest
damage/block/power/status/resource effects
enchantment interactions / relic-power dependencies
evidence_level / source_url / source_sha256 / extracted_at
```

卡牌、遗物、Power、Enchantment、Quest 的语义变更会递增 `semantic_catalog_version`，并触发受影响 teacher 标签重建。未知 ID 使用 OOV token 并标记 `Uncalculable`，不跨版本复用 embedding。

`ExternalPriorCatalog` 只收录 Tier、Elo/WAR、社区 pick rate、专家 reason tags 等统计/经验信息，必须带抓取时间、查询参数、样本量和偏差说明；它不改变真实语义，也不覆盖 teacher 值。

数据来源优先级固定为：

```text
真实 CLI pre/post diff + runtime probe
    > 影子模拟器已验证语义
    > 版本化游戏文件/反编译 catalog（交叉核验）
    > `.run` 已发生记录
    > 社区 Tier/Elo/WAR/专家文字
```

低层来源可以补充覆盖，不能覆盖高层来源的冲突结论；每个冲突写入 provenance 和 unresolved queue。

### G5：NOSL 反事实教师

教师模式：

```text
NOSL_MARGINALIZED
Q(a|o) = Σ P(h|o) Q(a,o,h)
```

训练主标签使用该模式。

对每个全局候选执行同一公共 pre-state 的配对反事实：

```text
for candidate in complete_legal_set (including Skip/Leave):
    fork(public_state, hidden_state_sample)
    apply(candidate, opportunity_cost)
    advance 3–5 global nodes and required CombatSummary calls
    record ΔV_next_fight / ΔV_act / ΔV_run / ΔRisk
```

候选之间共享相同的 `state_public_hash`、版本、候选集合和隐藏状态抽样配置；不使用跨 run 的绝对胜率替代配对差值。教师同时保存均值、方差、95% CI、失败步骤、恢复路径和 `quality`。

```text
SEED_ORACLE
```

仅作为审计上界和 future-information leakage challenge，不进入 Public View、主训练集或线上推理。

随机分支：

- 确定性：`p=1`；
- 小分支：完整枚举；
- 大分支：确定性分层采样；
- 记录 probability mass、ESS、CI、sampling seed、config hash；
- 未知概率绝不假设均匀。

隐藏状态策略：

```text
public observation
→ compatible hidden-state belief / stratified worlds
→ simulator + CombatSummary
→ marginalized Q and uncertainty
```

当观测不足以确定候选效果时，输出 `Uncalculable` 或带 CI 的 `SampledCI`，不强行生成确定性分数。候选屏 top-K 只可减少计算量；teacher 仍需在完整合法集合上复核。

Reliable 条件：

```text
legal action set complete
public leakage=0
checkpoint restore hash一致
CLI↔shadow mismatch_count=0
重复运行 SHA-256 一致
版本/context 一致
candidate set / opportunity cost 一致
teacher hidden-state policy 一致
teacher CI 达到最低有效样本数
```

### G6：全局数据集

分别导出：

```text
global_behavior
global_observed_human       # DEFERRED until auxiliary-data phase
global_external_prior       # DEFERRED until auxiliary-data phase
global_teacher
global_deck_evaluation
global_transition
global_challenge
global_audit_sidecar
```

切分使用：

```text
run_context_hash
episode_id
run_seed
branch_family_id
game_branch / game_version / catalog_version
```

不能用单独 `branch_id` 拆分，否则同一局的分支可能跨数据集。`run_seed` 用于分组和重放，不作为模型输入。

每个候选屏都生成一组行，而不是只保留被选动作：

```text
decision_id / offer_snapshot_hash
candidate_action_id / candidate_order / is_skip
state_public_hash / public_features
observed_action (optional)
teacher_delta_v / teacher_risk / teacher_ci
prior_score / prior_source (optional)
label_source / quality / sl_status
```

`global_observed_human`、`global_external_prior` 和 `global_teacher` 使用不同 manifest；辅助数据可用于 stop-gradient prior、pairwise/list-wise 预训练或采样配额，不能改写 NOSL 主标签。

质量枚举：

```text
ExactPublic
ObservedPartial
ExactKnownChance
SampledCI
BudgetBound
EstimatedByHeuristic
CounterfactualTeacher
ObservedHuman
ExternalPrior
Uncalculable
```

晋级规则：

```text
ObservedHuman / ExternalPrior / EstimatedByHeuristic
    → 仅辅助集
CounterfactualTeacher + strict CLI↔shadow + repeated-run pass
    → Reliable 主集
```

流程：

```text
JSONL
→ Schema
→ leakage gate
→ group split
→ Parquet/Arrow
→ Manifest
→ quality report
```

规模：

```text
1k Smoke
→ 10k Pilot
→ 100k Production Candidate
```

每一级通过门禁后再扩展。配额按 character、Act/floor、Ascension、单/多敌、HP 区间、资源、卡牌机制（消耗/虚无/指定弃牌/生成/随机目标）和决策类型分层，避免 Smoke 数据分布被单一角色或 A0 单敌人主导。

### G7：模块化 PyTorch 模型

独立训练：

```text
RoutePolicy / RouteValueModel
RewardPolicy
ShopPolicy
CampfirePolicy
EventPolicy
AncientPolicy
```

各模块共享接口但默认不共享参数：

```text
GlobalRunStatePublic
  → DeckFeatureEncoder (card/relic/power/potion set)
  → ContextEncoder (act/floor/HP/gold/map/visible encounters)
  → CandidateEncoder (完整语义 + opportunity cost)
  → candidate-conditioned scorer
  → module-specific heads
```

输出头固定为：

```text
policy logits / legal mask (由引擎提供)
V_next_fight / V_act / V_run
expected_hp_loss / death_risk / low_hp_tail
uncertainty (variance / CI proxy)
confidence / evidence_quality
```

卡牌奖励、商店、篝火和事件都使用同一公共状态下的候选相对评分；`Skip/Leave/Don'tBuy` 与普通候选相同。网络只负责排序和估值，合法动作、目标和资源约束由 CLI/引擎重新校验。

每个模块输出：

```text
legal mask
policy logits
value
death risk
confidence
```

训练顺序：

1. 规则/启发式 baseline + 合成状态覆盖；
2. 直接使用 `CounterfactualTeacher` 做候选 ranking/value warm start；
3. 候选屏 pairwise/list-wise ranking（保留 Skip 和完整 offer set）；
4. `CounterfactualTeacher` 的多目标 value 蒸馏；
5. RouteValue 与 CombatSummary 联合校准；
6. death-risk、低 HP 尾部和 uncertainty calibration；
7. 数据量和跨模块门禁足够后再评估共享 encoder；
8. 最后才评估 IQL/AWR 或 self-play 微调。

先验权重使用温度/系数逐步退火，并监测 prior 与 teacher 的 KL/regret；先验造成系统性偏差时自动降权。先验不锁定 archetype，也不改变 `Reliable` 主标签。

`ExternalPrior`/`ObservedHuman` 的 stop-gradient warm start 作为后续可选步骤，待辅助数据阶段恢复后再加入，不阻塞当前训练。

### G8：动态路线规划与编排

RoutePlanner：

```text
MapGraph
→ constrained Beam/DP
→ CombatSummary
→ RouteValue leaf
→ death-risk hard constraint
```

每条路径的叶节点价值由以下分量组成：

```text
route_value = ΔV_run
           + expected_deck_job_coverage
           - expected_hp_loss
           - death_risk_penalty
           - gold/rest opportunity cost
```

`deck_job` 使用软特征表示当前最紧急的前置伤害、AoE、格挡/减伤、成长、抽牌/能量、坏抽恢复和精英/Boss 覆盖；它只是候选排序先验，最终由 teacher value 和风险约束决定。

首版 horizon：

```text
3–5 个局外节点
```

重规划触发：

- 战斗结束；
- 奖励选择后；
- HP/金币显著变化；
- 获得关键卡牌/遗物；
- 药水消耗；
- 进入岔路；
- Boss 前。

当新状态使 `HP ratio`、金币、药水、卡组关键组件、遗物协同或下一战风险跨过阈值时，必须重新枚举完整路径候选，而不是沿用原路线。

`GlobalDecisionOrchestrator` 根据 `decision_type` 调用对应模块，只输出下一合法全局动作。

### G9：离线 RL 与 Self-play

IQL/AWR 仅在以下门禁后启动：

- P1 语义完成；
- NOSL M3a–M3e 通过；
- Reliable teacher 存在；
- strict diff 通过；
- split 无 seed/episode 泄漏。

离线 RL 数据策略：

```text
Reliable CounterfactualTeacher   主 Q/V 约束
ObservedHuman / ExternalPrior    DEFERRED（恢复后低权重先验）
EstimatedByHeuristic              预训练和覆盖补齐
```

采用 conservative policy constraint，禁止用 reward shaping 把“多拿牌、走更多精英或保留药水”等代理目标优化成最终目标。每次更新同时报告相对 teacher 的 regret、死亡风险和固定 seed challenge；模型若只提高行为模仿而降低生存率则拒绝。

Self-play 最后启动，并保留：

```text
专家行为
NOSL teacher
Expectimax
旧 champion
探索数据
```

自生成数据不能替代专家或教师主集。

### G10：部署与扩展

部署：

```text
PyTorch
→ ONNX
→ ONNX/PyTorch parity
→ C# Orchestrator shadow mode
→ CLI challenge
→ live integration
```

每个导出模型都生成 `GlobalModelManifest`：

```text
model_version / feature_schema_version
game_branch / game_version / assembly_sha256
semantic_catalog_version / simulator_version / scorer_version
training_manifest_hash / prior_fit_split
supported_characters / acts / ascensions
```

加载时任何版本或 schema 不匹配都拒绝模型，OOV/未知语义触发低置信度回退。

回退：

```text
高置信度模型排序
→ Shadow/Expectimax 验证
→ Beam/DP baseline
→ 完整 Expectimax
→ 人工确认
```

回退前固定执行：完整合法动作重新枚举、候选屏遗漏项复核、版本/hash 校验和 unknown-effect 检查。

扩展顺序：

```text
Act 1
→ 全三幕
→ Ironclad/Silent
→ 全角色
→ A0–A10
→ 更高 Ascension
→ Self-play/联赛
```

---

## 五、并行关系与启动门禁

现在即可并行：

- Schema；
- RunContext；
- stable action catalog；
- GUI；
- `.run` 导入（DEFERRED，仅保留接口）；
- MapGraph；
- human behavior/video 标注（DEFERRED）；
- partial episode；
- JSONL/Manifest；
- 启发式模型；
- `DeckFeatureEncoder` 和 `GlobalPriorRecord`（只做辅助 baseline）；
- 候选屏 pairwise/list-wise 数据整理。

Reliable 全局教师必须等待：

- P1 Power/Relic/Card 语义；
- NOSL M3a–M3e；
- CombatSummary；
- exact-room checkpoint；
- shadow chance provider；
- CLI↔shadow strict diff；
- teacher candidate set 完整性和 `ΔV` 配对复现。

---

## 六、验收测试

### 契约与数据

- RunKey/version/hash 不完整时拒绝；
- duplicate reward/shop 仍有唯一 action ID；
- Ancient/Boss reward/leave/proceed/nested choice 全覆盖；
- public state 递归扫描无 RNG/future/teacher/seed feature；
- same branch family 不跨 split；
- `.run` Ancient 不出现 generator repr；
- `rest_site == RestSite`；
- 多 Act 分别对齐；
- 同 row 多候选标 ambiguous；
- `GlobalRunStatePublic` 与 teacher/audit sidecar 字段边界递归扫描通过；
- 每个 offer snapshot 包含完整 legal set、Skip/Leave/Proceed 和遗漏项复核；
- card instance 的 stable ID、upgrade/enchantment/quest 状态可重建；
- `ExternalPrior` 的 `prior_fit_split` 不读取 validation/test/challenge。

### 重放与教师

- fixed seed `get_map` 双跑字节一致；
- route checkpoint restore hash 一致；
- exact-room checkpoint 恢复相同 options/inventory；
- 每个合法 action 都有 value 或 `null + reason`；
- Reliable CLI↔shadow `mismatch_count=0`；
- teacher probability mass/ESS/CI 可重建；
- 未知概率不生成伪 Reliable；
- 同一 pre-state、候选集合和机会成本的 counterfactual `ΔV` 可配对重放；
- `SEED_ORACLE` 与 `NOSL_MARGINALIZED` 结果分离，seed 不进入 public features。

### 模型与部署

- 合法动作率 100%；
- Top-1/Top-K、regret、NDCG；
- pairwise/list-wise candidate accuracy；
- value/risk calibration（Brier/ECE/低 HP 尾部）；
- death rate、survival、win rate；
- `ΔV_next_fight/act/run/risk` 四头均有分层报告；
- 按 character/act/floor/ascension/decision_type/patch 分层；
- 固定 seed challenge 不劣于 Beam/DP/Expectimax baseline；
- ONNX/PyTorch/C# 输出一致；
- 低置信度、版本错误、未知效果自动回退。

---

## 七、硬件

现有 `9950X3D + 32GB RAM + 12GB GPU` 足够：

- GUI；
- Schema/数据采集；
- ≤10k 生成；
- 5–30M 参数模块化 FP16 模型。

100k–1M 数据生成建议：

```text
16–32 vCPU x86_64
64–128GB RAM
200GB–1TB NVMe
GPU 非必需
```

说明：模拟器/CLI 并行和 JSONL→Parquet 主要受 CPU、进程数、磁盘和 RAM 限制；GPU 主要用于 PyTorch 训练。当前 32GB RAM 可完成 1k/10k smoke/pilot 和小型模块模型，进入 100k 以上或全局多分支 teacher 前再升级/租用 64–128GB RAM。模块化 5–30M 参数模型继续使用 12GB GPU；只有共享 Transformer、较大 batch 或多模型并行时再租 24GB+ GPU。

---

## 八、研究依据与外部参考

详细调研记录：

```text
D:\STS2BestChoice\STS2SuperModel\docs\research\STS1_STS2_DECK_EVALUATION_RESEARCH_REPORT.md
```

本计划采用的关键外部结论：

- [Slay-I](https://github.com/alexdriedger/SlayTheSpireFightPredictor)：用战斗结果模型比较加牌/升级/移除前后的条件差值；
- [LearnTheSpire](https://github.com/kaijchang/LearnTheSpire)：卡牌选择模仿网络的最小 baseline，以及同集评估过拟合教训；
- [Jialeiv/sts-rl-agent](https://github.com/Jialeiv/sts-rl-agent)：局外小网络与战斗搜索分层；
- [Spire Pilot](https://www.spirepilot.com/)：Combat/OOC 两脑、风险多头和教师蒸馏/死亡案例门禁（项目自述）；
- [Liam-Loebl/sts2-stats](https://github.com/Liam-Loebl/sts2-stats)：`.run` 候选长表、floor-conditioned WAR、候选屏 Elo 和补丁过滤；
- [Spire Codex scoring](https://spire-codex.com/leaderboards/scoring)：Bayesian shrinkage 与 Bradley–Terry/Elo 只作为外部统计先验；
- [Zamiell STS2 emulator](https://github.com/Zamiell/slay-the-spire-2-emulator)：C# 确定性模拟器与 Python Gym 分层接口参考；
- [Mega Crit Early Access](https://www.megacrit.com/news/2026-03-05-early-access-launch/)：STS2 内容和平衡持续变化，版本绑定是硬条件。

外部项目的卡牌数值、胜率、Tier、权重和实现代码均不直接写入本项目的 Reliable 语义或教师标签；接入前必须完成版本、许可证、来源和 CLI↔影子差分审查。
