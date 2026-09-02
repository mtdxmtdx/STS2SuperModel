# STS2 NOSL 与全局模型改进构想

更新时间：2026-08-31  
状态：Idea / 方向池，不改变当前 `PLAN_NOSL.md` 与 `PLAN_GLOBAL_DECISION.md` 的既定优先级  
适用项目：`D:\STS2BestChoice\STS2SuperModel`  
关联文档：

- `PLAN_NOSL.md`
- `PLAN_GLOBAL_DECISION.md`
- `GLOBAL_DECISION_ARCHITECTURE_IDEA.md`
- `SELF_PLAY_POLICY_ITERATION_IDEA.md`
- `SHADOW_SIMULATOR_OPTIMIZATION_RECOMMENDATIONS.md`
- `docs/research/STS2_RL_AGENT_ANALYSIS.md`

## 1. 一句话结论

项目最终形态不应只是一个神经网络，而应是一个由真实 CLI、确定性影子模拟器、NOSL 隐藏状态分布、Expectimax 教师、候选条件神经网络、自适应搜索复核和全局 semi-MDP 决策器共同组成的分层混合系统。

建议长期架构：

```text
真实 CLI 事实源
    ↓
结构化 Public / Teacher 状态与稳定候选
    ↓
确定性影子模拟器 + CLI↔ShadowDiff
    ↓
NOSL Hidden-World / Belief
    ↓
Expectimax / Counterfactual Teacher
    ↓
Policy + Q + Risk + Uncertainty 模型
    ↓
自适应搜索复核与安全回退
    ↓
全局 semi-MDP Orchestrator
```

主原则保持不变：真实 CLI 是事实源；影子模拟器负责高速展开；模型负责排序、估值、风险和预算分配；未经当前版本真实差分的第三方实现不进入 `Reliable`。

## 2. 当前问题的本质

当前项目面临的主要困难不是“缺少一种更先进的神经网络”，而是四类问题同时存在：

1. 随机分支、行动排列和跨回合 continuation 造成搜索规模快速增长；
2. 教师标签存在 Exact、Estimated、Uncalculable 混合，数据成本和信息密度差异很大；
3. 单回合局部目标与整局 HP、药水、卡组成长和路线价值尚未完全接通；
4. 模拟器语义、版本、状态键和真实引擎差分必须长期维持严格门禁。

因此改进方向应围绕以下目标展开：

- 用更少的搜索节点得到相同或更好的教师结果；
- 用更少的真实 CLI 预算发现更多语义错误；
- 让模型输出可以被搜索验证，而不是让模型替代事实源；
- 让战斗模型输出可以被全局模块消费；
- 让所有标签、模型和报告都能追溯到版本与证据。

## 3. NOSL 搜索与教师改进

### 3.1 Chance 因子化与等价后状态早合并

不要先生成抽牌、随机目标、随机弃牌、生成牌和洗牌的完整笛卡尔积，再在末端合并。建议把随机过程拆成独立随机变量，并在每层立即按行为状态合并：

```text
随机变量 A
→ 按 SearchBehaviorKey + NoslBeliefKey 合并
→ 随机变量 B
→ 再次合并
→ 进入下一层 Max / Chance
```

可进一步研究：

- 多重集合 DP；
- 只传播后续真正使用的充分统计量；
- 对语义等价结果进行 Rao-Blackwell 化；
- 合并后保留 probability mass、ESS、CI、RNG consumption vector 和最差质量；
- 对未知权重、未知卡池和截断分支继续保守降级。

潜在收益：Chance 分支从“随机组合数”下降到“唯一语义状态数”，直接提高 Exact 展开范围和 Reliable 教师产率。

主要风险：错误的等价判定会静默合并真实不同状态。必须用小池暴力枚举、同 ID 不同行为卡、不同来源 Power、不同触发顺序等反例进行验证。

### 3.2 跨 Root 与跨 Continuation 转置缓存

不同根动作和不同 Chance path 可能到达同一行为状态。可以共享 continuation value，但各根动作的审计链、质量和概率信息必须分开保存。

缓存键至少绑定：

```text
SearchBehaviorKey
NoslBeliefKey
depth / horizon
objective / scorer version
semantic catalog version
probability mode
search configuration hash
```

缓存不得混合 Exact 与 Estimated，也不得跨游戏版本、语义版本或 scorer 权重复用。正式实现前先增加 cache-hit、collision 和字段遗漏遥测。

### 3.3 模型只做候选排序，不先做剪枝

模型最早、风险最低的搜索接入方式是：

```text
全部合法候选
→ 模型排序
→ Expectimax 按排序展开
→ 充足预算下仍访问完整候选集合
```

这样模型错误只影响“多快找到优质动作”，不改变充足预算下的答案。限时停止仍按当前 incomplete/Estimated 规则传播。

验收应比较：

- 相同节点预算下最优动作发现时间；
- expanded_nodes；
- Top-K 覆盖；
- 足够预算时与无模型排序结果字节一致；
- 任意合法候选仍有机会被展开。

### 3.4 自适应模型—Expectimax 推理阶梯

长期运行时可以采用分级算力：

```text
1. 模型对完整合法集合打分
2. OOD、未知语义或低覆盖 → 直接扩大 Expectimax
3. 普通状态先验证模型 Top-K
4. 若最优下界高于其余候选上界且概率覆盖达标 → 提前停止
5. Top1/Top2 接近、低 HP、随机性强 → 扩大 K、深度和样本
6. 预算耗尽 → 保留 Estimated/Uncalculable 与明确回退原因
```

模型置信度只负责分配预算，不单独构成“最优证明”。提前停止必须依赖搜索的概率覆盖、置信区间或可证明上下界。

### 3.5 证明式 Partial-Order Reduction 与 Dominance

可以研究两类保守剪枝：

1. 已证明可交换且不影响触发顺序的动作，只保留一种规范顺序；
2. 在完整后续资源和状态上全维不优的候选，用严格 dominance 证明淘汰。

STS2 中受伤、弃牌、消耗、触发次数和卡牌播放顺序可能把“表面更差”变成长期更优，因此普通启发式 dominance 只用于排序。正式 Reliable 剪枝必须有白名单语义证明、反例 fuzz 和与暴力枚举一致性证据。

### 3.6 学习型跨回合 Continuation Value

当前单回合叶节点启发式可能低估 Setup、Power、保留牌和长期资源。后续可以学习：

```text
V(public belief at next player turn)
P(combat win)
remaining HP distribution
future potion / relic counter value
```

该 Value 可用于搜索叶节点、模型风险头或全局 CombatSummary。初期仅作为 `EstimatedExternalPrior` 或对照基线；待高保真全战斗差分覆盖稳定后，再评估是否进入教师 scorer。

## 4. 学生模型与数据效率

### 4.1 Candidate-conditioned Policy + Q + Risk + Uncertainty

模型继续使用 `f(state, candidate, complete_offer_set)`，而不是固定动作槽位。共享编码后输出：

- listwise policy / rank；
- 每动作 Q；
- 多目标 value；
- `P(death)`、HP loss、Uncalculable 风险；
- epistemic uncertainty / OOD；
- 搜索预算建议。

训练规则建议：

- Reliable 进入主策略与价值损失；
- Estimated 可进入低权重排序、辅助价值或蒸馏损失；
- Uncalculable 用于风险/OOD 目标，不作为正策略标签；
- teacher-only quality、reason、hidden RNG 不进入学生特征；
- 合法性继续由真实引擎/CLI 提供。

### 4.2 多目标价值向量与上下文条件 Scorer

“当前回合最优”依赖整局环境。建议教师和学生保留价值向量，而不是过早压成一个标量：

```text
敌方 HP / 击杀变化
玩家 HP 损失
死亡概率
剩余格挡
能量与手牌质量
Potion 消耗
Power / Relic 进度
下一回合状态价值
```

全局层可根据 Boss 距离、当前 HP、路线风险、药水稀缺性等提供风险偏好或 scorer 参数。这样同一个战斗模型可以服务不同整局情境，并减少 scorer 权重变化引起的全量重训。

### 4.3 主动数据采集

不再只按数量扩张数据。优先采集：

- 模型与教师 Top1 分歧；
- Top1/Top2 action gap 很小；
- ensemble disagreement 高；
- 当前 CLI holdout 缺失；
- 低 HP、多敌人、复杂选择；
- 随机目标、随机生成、洗牌、回合边界；
- Power 与 Relic 组合；
- Unknown、Estimated 或高 regret 状态。

建议保留混合分布，例如：

```text
50% 自然战斗分布
30% 语义覆盖与挑战场景
20% 模型分歧、低 margin 和 OOD 状态
```

这样既避免训练集只剩困难异常状态，也能把昂贵教师预算集中在信息密度最高的位置。

### 4.4 NOSL 不变性与对比式增强

对同一公共信念构造不同隐藏实现：

- 改变 raw RNG words；
- 改变未公开未来牌序；
- 改变无意义 runtime instance ID；
- 改变等价容器序列化顺序；
- 对白名单敌人置换，同时重映射 target action ID；
- 对已证明等价的卡实例置换，同时重映射 stable action。

要求 teacher label、public hash 和模型输出保持一致。并非所有置换都是真正对称，必须使用白名单 transformation，避免忽略左右位置、来源和内部计数器等真实差异。

### 4.5 Ensemble、校准与 OOD

训练多个不同 seed/bootstrap 模型，按语义分层评估：

- 确定性动作；
- known Chance；
- random target；
- multi-enemy；
- choice；
- EndTurn；
- Power/Relic；
- 新版本或新语义。

输出 ensemble 方差、top-gap、校准概率和 OOD 距离。运行时据此决定模型直排、小预算验证或完整搜索。校准门禁需要独立 CLI holdout/challenge，不以同分布 validation 的 softmax 作为置信度。

### 4.6 Search-to-Student DAgger

模型进入 shadow mode 后记录：

- 模型与教师 Top1/Top-K 分歧；
- 低置信和 OOD；
- 真实执行后的状态；
- 失败与回退原因；
- 当前 model/scorer/catalog/generator 版本。

再对这些状态重新运行教师并加入下一轮训练。每轮继续混入固定自然集、历史 replay 和 challenge，避免模型诱导的数据分布越来越窄。

### 4.7 自监督状态转移预训练

大量合法 trace 即使没有高质量教师标签，也可训练编码器预测：

- 一步后 HP/block/energy；
- 牌堆数量和公开卡牌变化；
- Power/Relic delta；
- action legality；
- next-state embedding；
- 状态是否属于同一公共 belief 等价类。

该模型既可为 Policy/Q/Risk 提供预训练表示，也可作为异常检测器：当神经转移模型、影子模拟器和真实 CLI 长期出现系统性分歧时，进入语义审查队列。它不作为事实源。

## 5. 全局决策改进

### 5.1 真正的 Semi-MDP Macro Transition

全局动作持续时间并不相同：路线选择跨一个房间，商店购买后仍停留在商店，事件选项可能进入第二页、选牌或战斗。因此应记录宏动作：

```text
GlobalOptionTransition:
  pre_public_state
  candidate_action_id
  continuation_chain[]
  elapsed_rooms / elapsed_floors / combat_turns
  cumulative_hp / gold / potion / deck / relic_delta
  terminal_reason
  post_public_state
```

折扣按房间、楼层或风险暴露定义，而不是按 UI 点击次数。Nested choice 使用独立 continuation ID，但仍归属于原始 global option。

### 5.2 整场战斗 CombatSummary

全局模型需要整场结果分布，而不是当前回合分数。建议新增 `FightRolloutProvider`：每回合调用 CombatPolicy/Expectimax，直到战斗结束，输出：

```text
P(win) / P(death)
HP loss mean / p50 / p90 / CVaR
turns distribution
potion use distribution
end-of-fight relic / power counters
confidence / semantic coverage / mismatch status
```

按 `deck + relic + potion + HP + encounter distribution + version` 缓存。Route、Reward、Shop 和 Event 只消费 CombatSummary，不进入逐牌搜索。

### 5.3 NOSL Hidden-World Factory / World Bank

从公共观测构造兼容隐藏世界集合，而不是恢复真实 seed 的唯一未来：

```text
public observation
→ compatible hidden worlds
→ exact / sampled / unknown strata
→ weighted world bank
```

所有候选共享同一批 hidden worlds 和 common random numbers，以降低配对 `ΔV` 方差。记录 probability mass、ESS、CI 和 world-bank config hash。完整 seed 世界只进入 `SEED_ORACLE` 审计 manifest。

### 5.4 统一 Counterfactual Global Teacher

对同一完整合法候选集配对展开：

```text
candidate
→ apply macro action
→ 展开 3–5 个 global nodes
→ 必要时调用 CombatSummary
→ leaf V_run bootstrap
→ 输出 ΔV_next_fight / act / run / risk
```

候选共享 pre-state、world bank、rollout budget、机会成本和版本。首版可以使用 Beam/DP 与短 horizon，不要求直接整局穷举。

### 5.5 Engine-valid Synthetic Curriculum

合成状态应从真实引擎可达状态开始，再做合法反事实扰动，而不是随机拼字段。覆盖：

- 五角色、各 Act、A0/A5/A10+；
- HP、金币、药水槽和路线位置；
- deck 加/删/升级/附魔；
- 单敌、多敌与公开 encounter profile；
- 商店价格、库存、不可买候选；
- Event/Ancient 普通、Proceed、Leave、Cancel、Nested choice；
- Boss relic、bundle、reroll、sacrifice、药水/遗物奖励。

训练循环采用均匀覆盖、初版模型、分歧/OOD 挖掘和定向提高教师预算的主动课程。

### 5.6 模块化候选评分器

共享输入协议，不急于共享全部权重：

```text
Deck / Relic / Power / Potion Set Encoder
Context Encoder
Candidate / Edit Encoder
Offer-set Attention
    ├── Route head
    ├── Reward head
    ├── Shop head
    ├── Campfire head
    ├── Event head
    └── Ancient head
```

各模块先独立通过数据和评估门禁，再进行 shared trunk ablation。当前 hash-bag reward scorer保留为 E0 baseline。

### 5.7 Route：风险约束搜索 + Learned Leaf Value

RoutePlanner 继续用 Beam/DP 搜索公开 MapGraph，网络主要预测叶节点价值与风险。维护 Pareto frontier：

```text
expected run value
expected HP cost
P(death)
CVaR low HP
recovery / shop opportunity
```

先施加死亡风险约束，再按 Run Value 排序。每次战斗、奖励、金币、药水或卡组变化后重新规划。输出真实下一合法节点 ID；完整规划路径另存 `plan_id`。

### 5.8 Reward 与 Campfire：Deck Edit Delta Model

抓牌、Skip、升级、移除、变形和附魔统一表示为：

```text
before_deck + edit_operator → after_deck
```

模型预测：

- `ΔCombatSummary`；
- `ΔV_next_fight`；
- `ΔV_act`；
- `ΔV_run`；
- 风险与不确定性。

Skip、Leave 和不锻造与普通候选一起比较，不使用固定 tier 覆盖教师价值。

### 5.9 Shop：序列化背包 / Beam Search

一次商店可能连续购买多件，购买顺序会改变金币、药水槽、移除价格和后续候选。教师应展开：

```text
buy / remove / discard potion
→ 更新 inventory / gold / state
→ 再次选择
→ leave
```

用受约束 Beam/Knapsack 搜索购买序列，模型学习当前第一个动作 Q。加入保留金币的未来机会价值、折扣、售罄、Potion slot 和移除价格递增。

### 5.10 Event / Ancient：Typed Effect DSL

不依赖描述文本猜测效果。将已验证事件结果表示为结构化算子：

```text
HP / MaxHP / Gold delta
add / remove / upgrade / transform / enchant card
add relic / potion / curse
start combat
next page / nested choice
random outcome mixture
```

已知公开效果直接模拟；隐藏随机结果进入 NOSL mixture；语义证据不足保留 Uncalculable；事件战斗调用 CombatSummary。

## 6. 验证、版本与工程改进

### 6.1 Coverage-guided Differential Fuzzing

自动生成合法小场景，执行：

```text
真实 CLI v0.111
vs 当前 C# Shadow
vs 外部 Python simulator（仅 ExternalReference）
```

唯一 Reliable 晋级条件继续是 `真实 CLI ↔ 当前 Shadow`。第三方结果只帮助分诊。对 mismatch 自动缩减卡牌、Power、Relic、敌人和动作序列，形成最小反例。

### 6.2 ExternalReference 语义索引

把当前 mismatch/Unknown/Uncalculable ID 映射到外部参考项目的实现文件和测试名称：

```text
model_id
reference_project / commit
reference_source_path
reference_test_paths
current_v0111_status
verification_required=true
```

只做定位和场景发现，不复制许可证不明确的实现，也不改变质量等级。

### 6.3 版本语义漂移与影响依赖图

记录 Card、Power、Relic、Event、RNG、Map 和 Scorer 的依赖关系。游戏版本变化时输出：

- added / removed / changed / unknown；
- 受影响 ShadowDiff fixtures；
- 受影响 teacher rows / shards；
- 需要重新生成的模型和 manifest；
- 可以保留的无关数据。

目标是从“每次更新全量重做”升级为“按语义依赖定向重验与重标”。

### 6.4 严格双向 ShadowDiff 与连续 Replay

比较器应检查双方字段全集，而不是只遍历 expected。Power owner/applier、DynamicVars、Relic counter、RNG、牌堆、Intent 和 stable ID 任一边多出或缺失都应产生 mismatch。

增加连续 `state → action → state` 回放，定位第一次偏差，覆盖计数器累积、回合边界、nested choice 和多步事件链。

### 6.5 Request Correlation 与过期响应防护

每个决策事务绑定：

```text
trace_id
decision_seq
request_id
pre_state_hash
action_id
post_state_hash
```

并行采集、超时恢复和重连时拒绝旧响应，记录恢复步骤，避免动作落到错误状态。

### 6.6 候选集合完整性矩阵

专门验证：

- reward Skip；
- shop 买不起、售罄或槽位不足商品；
- campfire disabled option；
- event/Ancient locked、Proceed、Leave、Cancel；
- Boss relic、bundle、reroll、sacrifice；
- nested choice 和 continuation。

候选保持完整集合，合法性由输入 legal mask 给出，stable ID 不由列表位置伪造。

### 6.7 性能剖面优先于结构重写

固定 100/1k 根状态测量：

- Clone/Fork；
- Exact/Search/Belief Key；
- Chance expansion；
- event dispatch；
- transposition hit；
- 每节点分配与 GC；
- 每类语义的节点耗时；
- 单线程和并行一致性。

报告绑定游戏、模拟器、scorer 和 config hash。根据数据再选择结构共享、对象池、增量 hash 或批量推理。

### 6.8 COW / Arena Fork 与增量 Key

Correctness 冻结后可以在实验分支研究：

- copy-on-write card/power/relic containers；
- arena 生命周期；
- 增量行为 hash；
- 批量 transition；
- 批量神经网络候选评分。

门禁必须覆盖状态所有权、事件队列、内部计数器、所有 RNG 流和并行确定性。差分全绿前不进入主线。

### 6.9 统一 DecisionExplanation 与 Regret Certificate

战斗和全局候选统一输出：

```text
action_id
rank / score / Q
multi-objective value
risk / uncertainty
quality / reliable
probability mass / ESS / CI
restriction reason
teacher budget / expanded nodes
selected / fallback reason
version / manifest hash
```

如搜索已形成上下界，可额外输出当前选择相对次优候选的 regret bound。该记录可直接服务 GUI、线上 shadow 审查和模型版本比较。

## 7. 隔离研究方向

### 7.1 POMCP / 粒子 Belief Search

当未来扩展到长时域全局隐藏状态时，可以把 POMCP 作为 Expectimax 的实验对照。当前单回合已知概率结构仍优先使用可审计 Expectimax。

### 7.2 MaskablePPO Baseline

使用本项目 Public Schema、稳定候选、真实 legal mask 和当前模拟器训练小型 MaskablePPO，仅作为挑战基线或 hard-state generator。评估与监督模型在同一 challenge set 比较 regret、死亡风险、mask violation 和延迟，不复用第三方 131/151 维位置编码。

### 7.3 Privileged World Model / SEED_ORACLE

完整 seed 地图和未来世界可用于审计上界、future-leakage challenge 与 Hidden-World 采样校验。输出独立 manifest，禁止混入 NOSL 主训练数据和 Public View。

## 8. 分阶段实施建议

### 8.1 现在可独立推进，且与当前 NOSL Core/P1 低冲突

1. Candidate-conditioned Policy/Q/Risk 模型 Prototype；
2. 数据分层与主动采样设计；
3. NOSL 不变性/置换增强与 dataset-level 测试；
4. ensemble/OOD/校准指标 schema；
5. 模型—教师 disagreement record；
6. ExternalReference 语义索引；
7. 候选集合完整性矩阵；
8. 连续 Replay 和差分 fuzz 方案；
9. 搜索性能剖面；
10. semi-MDP 与 CombatSummary 接口设计。

### 8.2 由当前 M3 Correctness 主线完成

1. Chance 因子化与等价状态早合并；
2. Key 完整审计；
3. 跨 root/continuation 转置缓存；
4. 模型候选排序 Hook；
5. 概率质量、CI、RNG consumption vector 传播。

为避免多人同时修改 `Search/Simulation`，这些内容应由同一主线批次实现。

### 8.3 M3 完成后

1. 自适应模型—Expectimax 推理阶梯；
2. 证明式 Partial-Order/Dominance；
3. COW/Arena/增量 Key 实验；
4. 批量候选和 Chance 后状态评估。

### 8.4 P1 与整场战斗闭环稳定后

1. FightRolloutProvider；
2. 版本化 CombatSummary；
3. NOSL Hidden-World Factory；
4. Counterfactual Global Teacher；
5. Engine-valid global dataset；
6. Deck Edit、Route、Shop、Event/Ancient 模块训练。

### 8.5 M5/M7/M8 后

1. ensemble 正式校准；
2. Search-to-Student DAgger；
3. learned continuation value；
4. PPO/POMCP 对照实验；
5. Champion/Challenger 与线上 shadow 晋级。

## 9. 优先级矩阵

| 方向 | 潜在价值 | 风险 | 当前依赖 | 建议阶段 |
|---|---:|---:|---|---|
| Chance 因子化与早合并 | 极高 | 中 | M3 belief/key | 当前 M3 |
| 模型只做搜索排序 | 极高 | 低 | 初版模型 | M3 后接入 |
| 主动采样 | 极高 | 低 | 初版教师/模型 | 现在设计 |
| NOSL 不变性增强 | 高 | 低至中 | Public normalizer | 现在 |
| 转置缓存 | 极高 | 中高 | Key 完整审计 | M3 |
| 自适应 Expectimax | 极高 | 中 | CI/上界/模型校准 | M3/M5 后 |
| Policy/Q/Risk 网络 | 极高 | 中 | 稳定 schema/data | 现在 Prototype |
| 整场 CombatSummary | 极高 | 中高 | P1/全战斗 rollout | P1 后 |
| Hidden-World Global Teacher | 极高 | 高 | CombatSummary/checkpoint | 全局 G5 |
| Deck Edit Delta | 高 | 中 | Global Teacher | 全局首个模型 |
| Route 风险搜索 | 高 | 中 | CombatSummary | Deck Edit 后 |
| Shop 序列搜索 | 高 | 中 | 完整 inventory contract | Route 后或并行 |
| Event Typed DSL | 高 | 中高 | 真实事件差分 | 后续 |
| 差分 fuzz/minimization | 高 | 低至中 | CLI fixture | 现在 |
| 版本影响依赖图 | 高 | 低 | catalog/manifest | 现在设计 |
| COW/Arena | 高 | 高 | correctness freeze/profile | 延后 |
| PPO/POMCP | 实验性 | 高 | 完整环境与评估 | 最后 |

## 10. 最推荐的组合

如果只选择当前最有潜力的五项：

1. **Chance 因子化与等价状态早合并**：提高 Exact/可靠教师产率；
2. **模型只排序、Expectimax 验证**：最低风险获得搜索加速；
3. **主动采样 + NOSL 不变性增强**：用更少标签训练更稳的学生；
4. **整场 CombatSummary**：接通战斗与全局决策；
5. **NOSL Hidden-World + 配对反事实 Global Teacher**：建立无未来泄漏的全局教师。

建议总体顺序：

```text
当前 M3 correctness
→ Chance DP / Key / Cache
→ Candidate Policy/Q/Risk Prototype
→ 主动采样与不变性增强
→ 模型排序 + 自适应 Expectimax
→ FightRollout / CombatSummary
→ Hidden-World Global Teacher
→ Deck Edit
→ Route
→ Shop
→ Event / Ancient
→ DAgger / PPO / POMCP 对照
```

## 11. 明确保持的边界

- 不把所有战斗与全局动作压进一个固定离散动作头；
- 不把模型输出当作合法性来源；
- 不把真实 seed 的唯一未来当作 NOSL 教师；
- 不把未经当前 v0.111 CLI 差分的第三方语义晋级 Reliable；
- 不把 Estimated 与 Reliable 数据混成同一来源；
- 不因模型置信度高而跳过未知语义门禁；
- 不在 correctness 冻结前进行大规模 COW/状态共享重构；
- 不用盲目扩充相似数据替代覆盖与主动采样；
- 不让全局模块阻塞当前单回合战斗模型的 M3/P1 主线。

该文件作为方向池保存。任何条目转为实施任务前，应重新核对当前代码、数据、版本门禁、并行 Agent 工作范围和对应计划状态，再决定是否写入正式 PLAN。
