- 本轮追加：CLI 支持 `reset_run` 批量隔离运行并正确关闭 trace writer；ShadowDiff 对 choice/error-only trace 输出确定性 `Uncalculable` 报告并以 exit 0 结束，不再触发 CLR 弹窗；direct-matrix 非 combat_play 行在模拟前直接降级，607 条批量报告现可无超时生成。卡牌 direct matrix 已采集 607 个真实 CLI play 行，证据仍按 match/timeout/degraded 分级，未把 timeout 晋级 Reliable。Expectimax 等价 post-state 合并现在保留 probability coverage、ESS、置信区间与 RNG consumption vector，并按最差 outcome quality 传播；ExpectimaxEngineTests 定向 8/8 通过。
# STS2 NOSL Expectimax 教师模型计划（PLAN.md 更新版）

## 0. 当前实现状态（2026-08-31，影子模拟器复核后）

- **M0 契约修复已完成，仍需扩展验收**：`ExhaustPile` 已从 NOSL 可抽多重集合排除，`MaskHiddenState` 不再写入风险，`CombatSnapshot` 默认 view 为 `Public`，真实适配器显式标记 Teacher view，并引入显式 `RngAvailability`；TeacherEvaluator 现在只接受 `Public + MaskedUnknown`，并拒绝 Teacher provenance、raw RNG state、ordered future draw pile 与 Missing 输入。TeacherWorker 已移除 teacher_snapshot 硬依赖，public-only evaluator 请求回归 `9 passed`，并完成 public-only `NOSL_EXACT_OFFLINE` smoke（10 条与 1 条，均 exit=0）。完整 M0 回归仍需补齐跨模块不变性门禁。
- **M1 精确概率能力已通过当前 exact fixture 审计，完整算子覆盖仍开放**：不放回抽牌、随机目标、随机组合和加权池均使用多重集合 DP 与语义合并；已生成 `m1-runtime-probability-audit.json`，33 个 Chance 组的概率总和全部为 1，未发现采样分支伪装为 Exact/Reliable。仍需补齐未在当前战斗路径引用的 `CombatPotionGeneration` 及其余随机算子的独立运行时证据，才能关闭全量 M1。
- 本轮已将未知洗牌与 Reboot 精确分支统一改为按卡牌语义生成唯一多重集合排列，并加入 A,A,B→1/3 分支回归；随机生成卡池及随机弃牌的单张/多张无放回选择现在按重复计数加权（A,A,B→2/3、1/3），Expectimax 在评估前合并等价 post-state 并累加概率；随机卡池/弃牌/洗牌/Reboot exact 分支已填充 RNG 消耗向量；新增 `RandomOperatorRegistry` 覆盖 7 条 v0.111 combat RNG 流，并扩展 `ChanceBranch`/采样统计元数据。`UniquePermutationCountAtMost` 已改为带上限的多重集合 DP，不再通过枚举排列判断是否超预算；`CardSemanticKey` 现在包含行为字段和 Effect 参数，DP 与排列枚举使用同一判别键，避免同 ModelId 动态卡被错误合并；新增同 ModelId 不同伤害变体的分支回归（2/2 通过）；已知随机充能球分支补齐 RNG source、coverage、ESS 与 consumption vector；等价分支合并保留 coverage/ESS/CI/RNG vector 并按最差质量传播；重复 Reboot 无法展开的随机分支现在显式标记 `ProbabilityKnown=false/OutcomeQuality=Uncalculable`；Expectimax 对进入预算内的 `Sampled/Estimated` transition 也会强制降级为 `Estimated + incomplete`，不再因概率和为 1 而误标 Reliable，并保持 `Uncalculable` 质量单调不被 unknown-probability 分支覆盖；新增 `audit_random_operator_registry.py` 与 `data/random-operator-audit.json`，当前 v0.111 combat RandomSource 引用 4 条、未注册 0 条。未知概率来源审计和大池降级分支仍需完成后才能关闭 M1；本轮已修正 sampled ChanceBranch 的 covered mass/ESS 元数据，避免把全量质量误标为采样覆盖；并补齐嵌套随机球/生成/目标/消耗分支的概率质量、随机流向量与置信元数据传播；随机目标/球/消耗的 exact 与 sampled 分支也已显式记录 RNG 元数据，并通过嵌套 Chance 组合测试确认 metadata 不丢失。
- 多结果随机生成现在明确输出 `OutcomeKind.Stochastic`，避免把多个 exact chance 分支误标为确定结果。
- **M2 协议桥接已落地，但生产教师正确性未验收**：TeacherEvaluator 现已在存在 `PolicyLine` 时优先使用其期望值，并输出 `Max → Chance → Max` 结构化策略树和所有合法动作的 value/quality/reason 记录；`PolicyBranch.Continuations` 已按多层 ChancePath 构建，序列化器现在递归输出 `Chance → Max → Chance` 子树，且每个策略动作带 CLI 兼容的 canonical `action_id`；`trace_to_training` 现在传播 `legal_actions_complete`，fallback/截断集合不会被当成完整合法动作集；新增随机动作期望值高于固定动作的反例回归。仍需验证复杂多层分支和全部动作覆盖率。因此 M2 是“可调用”，不是“可生成 Reliable 主标签”。
- TeacherEvaluator 现额外输出 `restricted_reasons`，将未探索动作关联到具体 simulator restriction code，便于定位 public-only M4 中的 `teacher_label_missing`；generic `not_explored` 仅作为无可用 restriction 时的最后原因。
- 初步定位 `teacher_label_missing`：在 public-only 快照中，回合结束会把当前手牌移入弃牌堆并触发未知 `Shuffle`，当前 `ProjectToNextPlayerTurn` 在该路径记录 `uncalculable_shuffle_order`，导致所有终态被过滤。该原因已通过 `restricted_reasons` 暴露，下一步应专门修正“手牌回合末弃牌后未知洗牌”的 chance 分支，而不是放宽 Reliable 门禁。
- **当前主线切换为 M3 correctness closeout**：先完成 belief/key/稳定 ID、精确 chance 聚合、事件时序、条件策略树和真实 CLI 差分门禁，再启动新的 1k Reliable smoke。
- M3a 已新增 `PublicObservationKey`、`NoslBeliefKey`、`SearchBehaviorKey`、`AuditExactKey` 与 `CycleKey` 入口；NOSL key 回归确认隐藏 RNG 和未来牌序变化不影响 key，序列化使用 InvariantCulture。`SearchBehaviorKey/AuditExactKey` 现将卡牌行为字段、Effect 参数、生成池和回合末效果纳入 canonical serialization，并有同 ID 不同伤害变体、fr-FR 小数格式及同 ModelId 变体换序回归通过；完整字段审计与稳定 ID 全量门禁仍待完成。

当前语义覆盖和报告以仓库可执行 runner/catalog 为准：

- Power：`20/283` 为 `simulator_supported`，`53` 为 `simulator_declared`；P1 Power 注册 11 fixtures / 26 reports，连同 P0 为 32 fixtures / 57 reports。报告质量必须以 ShadowDiff 元数据判定。
- Relic：checked-in catalog 为 99 SimulatorSupported、1 PartiallySupported（PARRYING_SHIELD）、20 UnsupportedKnownEffect、97 OutOfScope、25 UnverifiableByCli、56 Uncalculable、1 NoCombatEffect；Unknown=0，strict Reliable eligible=25。当前 runner 注册 65 fixtures / 132 reports；随机/teacher-conditioned 报告已降级，不能把 handler 数量当作 Reliable 语义对象。
- 重复运行清单：`data/p1-repeat-verification.json` 保留为新增专项前的 `212 reports` 历史基线；新增两个遗物 fixture 后，当前遗物矩阵为 132 reports，combined repeat manifest 需重新生成后才能作为总门禁。
- Card：单人战斗范围 `1099/1099`，聚合为 `590` 个结构签名；`build_card_signature_report.py` 已接入 `data/card-direct-witness-manifest.json`。607 条 direct matrix 行中 `64` 条为 repeat-verified direct_reliable、`10` 条 direct_estimated、`65` 条 mismatch、`467` 条 Uncalculable；签名层当前 `46` 个 verified_all_variants（含 `62` 条显式 numeric-upgrade equivalence proof）、`9` 个 partial_fixture、`441` 个 fixture_degraded、`543` 个仍有行为缺口。607/607 报告独立双跑字节一致，未通过的语义仍不进入 Reliable NOSL 主训练标签。
- 已有 `teacher-realsmoke-1000` 为诊断数据：`1000` rows、`874` unique states、`6006` actions、`Reliable=0`、`Estimated=1000`、duplicate warning `126`。它不构成 M4 Reliable smoke 完成证据。
- 已运行 public-only M4 管线 pilot：清洁输入 `data/m4-nosl-smoke-100-input-clean.jsonl` → 最新输出 `data/m4-nosl-smoke-100-clean3.jsonl` 共 `100` rows、`624` legal actions、`23` combats；修复默认 `ImmutableArray` 空效果及 stochastic terminal 诊断索引后，结果为 `Estimated=93`、`Uncalculable=7`、`Reliable=0`，`teacher_label_missing=7`，无 `teacher_snapshot_rebuild_failed`。不可计算动作现在带具体 `uncalculable_shuffle_order/unknown_shuffle_rng_state` 原因。`build_dataset_report.py` 生成版本锁一致报告；该 pilot 证明批量标签生成与质量统计链路可用，但尚未满足 M4 Reliable 出口。
- 对 clean3 进行了可重建去重：`data/m4-nosl-smoke-100-dedup.jsonl` 保留 `94` rows、`584` actions、`23` combats，删除 `6` 条完全相同的 state/action 记录；`validate_dataset.py` 返回 `passed`，`public_leakage_count=0`、`stable_id_missing=0`、duplicate warning/error 均为 `0`，标签为 `Estimated=87/Uncalculable=7/Reliable=0`。
- 已生成清洁的 1k NOSL 诊断集：`m4-nosl-smoke-1000.jsonl` 共有 1000 rows、6006 actions、241 combats，Expectimax 标签全部 `Estimated`（当前 500ms bounded 配置，不宣称 Reliable）；版本锁、稳定 ID、public leakage 均通过。按 `(state_hash_public, legal_actions, label)` 去重后得到 `m4-nosl-smoke-1000-dedup.jsonl`：874 rows、5294 actions、231 combats，`validate_dataset.py=passed`，duplicate warning/error=0、public_leakage=0、stable_id_missing=0。该数据仍是诊断/预训练候选，不是 M4 Reliable 出口。
- 已以 2000ms/1,000,000 节点重新生成扩展 1k：`m4-nosl-smoke-1000-extended.jsonl`；标签仍全部 Estimated（1000 rows、6006 actions），说明当前状态包含未公开 RNG/边界，增加预算不会伪造 Reliable。去重版 `m4-nosl-smoke-1000-extended-dedup.jsonl` 为 874 rows、5294 actions，验证通过且 duplicate warning/error=0；该结果可作为 M5 前的高预算诊断基线。
- 进一步确认：public `draw_pile_count` 不能直接写入有序 `CombatSnapshot.DrawPile`，否则会违反 evaluator 的未来牌序拒绝规则。当前适配器继续使用无序剩余牌多重集合并保守降级；要获得 Reliable，M3b 仍需新增“无序抽牌池 + 不放回概率抽样”的状态表示，不能用排序后的隐藏牌序替代。
- 已落地无序抽牌池标记：当 public `discard_pile_count=0` 且剩余牌数量等于 `draw_pile_count` 时，适配器写入 `nosl_unordered_draw_pool` 标记；Core 将其从 snapshot restrictions 排除，并在 turn-start 抽牌处按多重集合展开。3 个定向测试通过；该路径仍按未知顺序标记 Estimated，不泄露未来牌序。
- 无序抽牌池完成真实批量验证：重新生成 `m4-nosl-smoke-1000-final.jsonl` 后，1000 rows 中 `Reliable=380`、`Estimated=580`、`Uncalculable=40`；去重后 `m4-nosl-smoke-1000-final-dedup.jsonl` 为 874 rows、5294 actions、Reliable=362、Estimated=480、Uncalculable=32。`m4-nosl-smoke-1000-final-manifest.json` 与 `validate_dataset.py` 交叉校验通过，duplicate warning/error=0、public leakage=0、stable_id_missing=0。
- 无序抽牌池现已覆盖回合结束抽牌和卡牌/药水 Draw 效果：`PlayCardOutcomes`、`UsePotionOutcomes`、`OrderedDrawShuffleOutcomes` 均优先消费独立无序池，exact 分支不增加 Shuffle RNG 消耗，超过预算才采样降级；新增卡牌 Draw 回归通过。
- 已用上述完整 Draw 语义重新生成最终 1k 批次 `m4-nosl-smoke-1000-final3.jsonl`：原始 1000 rows 的 Reliable/Estimated/Uncalculable 为 381/579/40；去重后 `m4-nosl-smoke-1000-final3-dedup.jsonl` 为 874 rows、Reliable=363、Estimated=479、Uncalculable=32。manifest、split 和统一 quality gate 均通过，final3 是当前 M4 数据基线；final2 保留为历史批次。
- `run_quality_gate.py` 已对 final3 dedup 数据、manifest 和 split 目录执行统一门禁：`PASS`，0 failures、0 leaks、0 missing stable ids、0 malformed lines，874 rows/231 groups/5294 actions；质量分布 Reliable=363、Estimated=479、Uncalculable=32。final2 仅保留为历史批次。
- 在无序抽牌池实现后重新执行验证：final3 去重数据统一 quality gate 为 `PASS`（874 rows、Reliable=363、Estimated=479、Uncalculable=32、duplicate/leakage/stable-ID 均为 0）；M3e DEFEND trace 仍为 `Reliable/mismatch_count=0/match=true`。
- 新增 `data/m3e-reliable-holdout-coverage.json`：将当前 7 个 M3e smoke 报告与去重 1k 数据的 canonical action ID 对齐，3/7 报告能在数据 action 集中找到，7/7 mismatch_count=0。该覆盖率为 partial，不能代替全量 CLI holdout，剩余动作仍需采集对应 root-state 证据。
- 已按 episode/trace 分组生成 M4 split：权威目录为 `data/m4-nosl-smoke-1000-final3-splits/`，train=430、validation=162、test=217、challenge=65，231 个 group 无跨 split 泄漏；每个 split 都绑定相同版本锁、generator_config_hash 和 final3 source SHA。
- 新增 `data/m3a-stable-id-audit.json` 对 874-row 扩展去重集做轻量稳定 ID 审计：5294 actions、130 个跨样本 canonical action IDs、`stable_id_missing=0`、行内冲突=0、`verdict=pass`。这证明当前数据层 ID 完整，但不替代跨进程 stable-ID 生成器的全量门禁。
- 新增 `data/M5_READINESS.json`：记录 M4 final3 已满足质量门禁、M3e 7-report smoke 已全绿，可开始 M5 Pilot 的来源准备和 Reliable-only 训练实验；生产训练前仍必须完成每个 Reliable 分层的 CLI↔Shadow holdout，以及随机目标、多敌人、EndTurn 超出 smoke 的覆盖。
- 为降低重建造成的伪缺口，`snapshot_adapter.py` 已补齐 v0.111 public preview 中已验证的 `afterimagepower/strengthpower/vulnerablepower/weakpower` → `ApplyStatus` 映射；当前 pilot 仍保持保守降级，未将任何新样本晋级 Reliable。
- 同一适配器已补齐 Shrinker Beetle `DebuffStrong` → 三回合 `SHRINK` 的已验证 Intent 映射；未验证的其他敌人 AI 仍保持降级，不进入 Reliable。
- 另补齐 `BARRICADE` Power 卡 ApplyStatus 与 public `orb_slots → orb_capacity`，100-row pilot 的 BARRICADE 缺口已清零；质量仍按 evaluator 结果保守标记。
- M3e deterministic CLI smoke 已用当前 ShadowDiff 重跑：`m3e-live` DEFEND 动作 `Reliable/Exact/mismatch_count=0`，严格比较 HP、Block、Energy、手牌/牌堆、Power、Relic、RNG、Intent；重复报告 SHA-256 相同（`AC306533EEA326810B77469CFA9AD1654E2AA113161EF7225E467B7C97E3E07E`）。该证据仍为单动作，不代表全量 M3e 关闭。
- M3e potion smoke 也已重跑：FIRE_POTION 动作 `Reliable/Exact/mismatch_count=0`，与重复报告 SHA-256 相同（`17233DA01B153028C2D26B60749110B324215E7F97979EA69BCE3A13436ECD5F`）；仍需扩展多敌随机目标与 EndTurn 的完整场景。
- M3e multi-enemy deterministic smoke 已重跑：`p0-multi-enemy-targets-trace` `Reliable/mismatch_count=0/match=true`，重复 SHA-256 一致（`E04F4DF8FAEC4712492DC894753AC5359FC2000E50FE9EE09B36BC7118B24E3C`）。该 fixture 覆盖多敌指定目标动作，不等于随机目标概率闭环。
- M3e 随机闭环 smoke 已重跑：`SWORD_BOOMERANG` 随机目标与 `CHAOS` 随机球均 `Estimated/mismatch_count=0/match=true`，各自重复报告字节一致（随机目标 SHA `BAE5BD7E9C34972387EE16DCFACD94A1F6BA80325652D4EACBFB40ADB5BEAA16`；随机球 SHA `2C5F1A773730595A6B685ED61482299A0E770D8DE86C4A6521072D69C02A0DE6`）。由于随机源未公开，保持 Estimated，不进入 Reliable。
- M3e EndTurn 未知洗牌 smoke 已重跑：`Estimated/mismatch_count=0/match=true`，重复 SHA-256 一致（`8859FA498C8D97EEAF1C118AEE700F512DF56B6218A2F4EC7320DBBEEAB12D79`）。当前 7 个 M3e live smoke 报告均可重复且零差异，但 Reliable 仍仅限已验证确定性动作。
- M5 来源准备（2026-08-31）已完成：修复 `capture_teacher_matrix.py` 在 fixture 未含 `quit` 时的生命周期超时，重新采集全部 28 个 P0 fixture；`data/m5-source-pilot-all28.jsonl` 共 114 个真实 CLI public 决策状态、28 个 episode、672 个合法动作，版本锁一致。经 `TeacherEvaluator` 的固定 `maximum_expanded_nodes=10000` bounded NOSL 配置生成 `data/m5-source-pilot-all28-labeled.jsonl`，标签为 `Reliable=44/Estimated=66/Uncalculable=4`；4 条 Uncalculable 均保留 `teacher_label_missing` 与 `uncalculable_shuffle_order` 限制。`m5-source-pilot-all28-quality-gate.json` 为 PASS（0 duplicate、0 leakage、0 stable-ID missing、0 split violation），并已生成 episode split。固定节点预算重跑的标签与 aggregate SHA-256 字节一致；500ms wall-clock 版本曾出现 `expanded_nodes` 漂移，已废弃，不作为正式 source。该批次是 M5 pilot source/管线验收，不是 10k 生产训练集；仍需扩展语义分层和 CLI↔Shadow holdout。
- M5 source 重建审计（2026-08-31）：对 `data/m5-source-*-trace.jsonl` 的 28 个真实 CLI trace 逐一重新执行 `trace_to_training.py`，得到 114 行，与权威 `data/m5-source-pilot-all28.jsonl` SHA-256 `E0DFA0D08A05BBCF876538AB46E9214173596EE99C51BFA6F201904211D3B31F` 完全一致；28/28 文件 JSONL 可解析、`stable_instance_id` 完整。该审计只确认来源可重建，不提升标签质量，也不改变 M3e holdout partial 状态；明细见 `data/m5-source-reconversion-audit.json`。
- M5 覆盖扩展（2026-08-31）：复用版本锁一致的 `data/p1-card-direct-matrix-trace.jsonl`，规范化得到 596 个 public 决策点，与 114 条 P0 source 按 state/action 组合去重后形成 `data/m5-source-expanded.jsonl`（416 状态、180 episode、2,331 动作）。固定节点预算标注结果为 `Reliable=45/Estimated=243/Uncalculable=128`，质量门禁 PASS、无重复/泄漏/stable-ID/split 违规；全量重复标注 SHA-256 字节一致。由于 direct-matrix 覆盖了大量尚无完整卡牌 handler 的语义，128 条保留 Uncalculable，teacher pair rate 仅 27.4%；该文件仅作为语义覆盖候选和分层诊断，不进入 Reliable 主训练集。
- 扩展候选的分层统计见 `data/m5-source-expanded-strata.json`：302 条来自 direct-matrix 语义探针、114 条来自 P0 战斗 fixture；409/416 状态为单敌人、仅 5 条为三敌人、2 条无敌人。该分布明显偏向单敌人和卡牌探针，不能直接视为自然战斗分布；10k Pilot 必须补充低血量、多敌人、随机目标、复杂选择和跨回合状态后再切分训练集。
- 已从 M4 final3 Reliable（363）与扩展候选 Reliable（45）合并出 `data/m5-reliable-candidate.jsonl`：408 个独立 Reliable 状态、188 episode、2,635 动作；quality gate PASS、无重复/泄漏/stable-ID/split 违规。该集合仅用于 Reliable-only 试验，分布严重偏向 Ironclad（394/408）且 M3e holdout 仍不完整；分层统计见 `data/m5-reliable-candidate-strata.json`，不得视为生产 10k 数据。
- 通过 `p0-combat`、`p0-multi-enemy-targets`、`p0-neutralize-weak` 各 5 个种子变体新增 64 个状态，形成 `data/m5-source-expanded-v2-labeled.jsonl`：480 状态、195 episode、2,842 动作，Reliable=65、Estimated=287、Uncalculable=128；三敌人状态由 5 增至 29，Silent 状态由 138 增至 168。固定节点预算组件重跑字节一致，quality gate PASS。v2 仍是覆盖候选，不是 10k 生产集；单敌人仍占 449/480，需继续补充自然分布。
- 从 v2 中抽取并合并 M4 Reliable 后生成 `data/m5-reliable-candidate-v2.jsonl`：428 个 Reliable 状态、198 episode、2,815 动作，quality gate PASS；该集合仅供 Reliable-only 试验。它仍高度偏向 Ironclad（414/428），且 M3e CLI↔Shadow holdout 仍为 partial，生产训练前需继续补充 Silent 和自然战斗状态。
- 新增 `data/m3e-reliable-holdout-coverage-v2.json`：扫描 165 个零 mismatch 的 Reliable ShadowDiff 报告，按 canonical action ID 与 Reliable 候选对齐后覆盖 20/86 个候选 action ID（23.26%），mismatch_count=0，verdict=partial。该报告仅证明动作 ID 层已有证据，不替代每个根状态/语义分层的 CLI↔Shadow holdout。
- 对 43 个 M5 source/seed trace 的 75 个真实动作执行 ShadowDiff；初次发现的 9 份 mismatch（13 个字段差异）已全部修复。修复包括 Weak 后公开敌方 Intent 重算、Seapunk Buff/Defend（+1 Strength/+7 Block）、Terror Eel Buff（+6 Vigor）和敌方 Vigor 攻击后消费。最终为 74 份 match、0 mismatch、1 份 choice/error-only，75 份报告重复字节一致，详见 `data/m3e-m5-holdout-run.json` 与 `data/m3e-m5-mismatch-diagnosis.json`。
- ShadowDiff 置信度门禁已收口：`mismatch_count>0` 时强制 `confidence=Estimated`。语义修复前批次为 65 份零差异、9 份 mismatch、1 份 choice/error-only；mismatch 报告不再保留 Reliable 标记。代码变更见 `training/ShadowDiff/Program.cs`，仅运行 ShadowDiff 定向构建和 M5 holdout 批次。
- M5 holdout 最终报告已在上述语义修复后更新为 74 份零差异、0 mismatch、1 份 choice/error-only；置信度门禁继续保留。按 root-state/action 精确键，目前覆盖 Reliable 候选 23/1580（1.46%），所以 `data/m3e-reliable-holdout-coverage-v4.json` 仍为 partial，尚未达到 M3e 全量出口。
- 为落实 Reliable 资格门禁，按 `teacher_best_actions` 与零差异 CLI↔Shadow root evidence 交集筛出严格子集 `data/m5-reliable-holdout-backed.jsonl`：15 状态、7 episode、130 动作，全部 Reliable，quality gate PASS。其余 Reliable 候选继续保留为实验数据，直到获得对应 root-state holdout；严格子集不代表生产规模。
- 新增 Silent 基础战斗 fixture `training/fixtures/m5-silent-basic-commands.jsonl` 并使用 10 个种子变体采集 60 个状态；由于公开无序洗牌分支保持显式 Estimated/Uncalculable，未新增 Reliable。合并形成 `data/m5-source-expanded-v3-labeled.jsonl`（540 状态、205 episode、3,222 动作，Reliable=65、Estimated=307、Uncalculable=168），quality gate PASS；该批次用于分布覆盖诊断，不改变 Reliable-only 主候选。
- M1 轻量出口复核：修正 `audit_random_operator_registry.py` 后报告 registry=7、语义引用=6、未注册引用=0，唯一未引用流为当前战斗路径未使用的 `CombatPotionGeneration`；`NoslExpectimaxTeacherTests + ExpectimaxEngineTests` 定向 25/25，`test_shadow_diff_reports.py` 15/15。随机算子注册静态门禁通过，但 Exact 全量运行时概率审计仍保持开放。
- M2 输出审计：`data/m2-policy-tree-audit.json`（schema v2）确认 15 条严格 Reliable 状态的所有合法动作都有 `action_evaluations`，未探索动作均带 `null+reason`；严格集含 245 个 Chance 节点、41 个根级确定性后续动作且概率质量无异常。540 条候选的 Chance→Max 节点存在，但后继 Max 均无继续动作，完整多层 contingent policy 仍未验证，M2 保持 partial。`ConditionalPolicyReportsDeathProbabilityAcrossDrawOutcomes` 已增加每个 Chance 分支必须包含后续 Max 动作的回归断言（相关策略测试 5/5）。
- 新增 `m2-contingent-draw-commands.jsonl` 真实 CLI 探针；公共 `ACROBATICS` 预览现映射为 `Draw(3)+DiscardCards(1)`。由于弃牌仍是未解析的玩家选择，该探针保持 `Uncalculable`，不进入 Reliable；它用于后续真实 Chance→Max 夹具扩展。
- 同一探针已切换至 `BATTLE_TRANCE`（真实预览为 Draw 3 且禁止本回合额外抽牌）；适配器记录 Draw 并显式标注 `card_semantics_partial`，当前未知洗牌边界仍使结果保持 `Uncalculable`，未进入训练主集。
- 扩充探针牌组到 13 张并固定 seed 后，真实 CLI root 已让 `BATTLE_TRANCE` 成为教师最优根动作；使用 `NOSL_EXACT_OFFLINE` 后，`data/m2-contingent-policy-verification.json` 验证 56 个 Chance 节点概率和为 1，56/56 个 Chance 子节点均重新进入带 4 个后续动作的 Max，教师标签为 `Reliable/ExactWithKnownChance`。递归结构与精确概率门禁已通过。
- 该夹具已接入 ShadowDiff：`m2-contingent-draw-shadowdiff-report.json` 对 `play_card:BATTLE_TRANCE:001:none` 返回 `Reliable / match=true / mismatch_count=0`。为使验证器重建真实抽牌身份，ShadowDiff 仅在 verifier 侧使用 settled public hand delta，绝不写入 NOSL 学生特征；教师标签本身仍保持 Estimated。
- ShadowDiff 已重复运行 2 次并生成 `data/m3e-contingent-shadowdiff-manifest.json`：2/2 报告 `match=true`、`mismatch_count=0`，SHA-256 字节完全一致；该证据仅覆盖 BATTLE_TRANCE 单一 root action，不代表全量 M3e holdout 已关闭。
- 将精确 BATTLE_TRANCE 根状态并入严格候选后生成 `data/m5-reliable-holdout-backed-v2.jsonl`：16 状态、8 episode、136 actions、Reliable=16；manifest/split/quality gate 全部 PASS，source SHA `8FF91815EF9BAA9DEF6F3179021FACABC1670CCDE7D286A8A3C106264EC93A89`。该集合仍是严格实验候选，不等于生产规模 M5。
- `m2-policy-tree-audit.json` 已新增 `holdout_v2` 小节：16 行、16 个完整 action-evaluation 集合、101 个带 reason 的 null action、301 个 Chance 节点，概率质量异常为 0；历史 15 行审计保留以便追溯。
- 新增确定性 BASH CLI↔Shadow holdout 后生成 `data/m5-reliable-holdout-backed-v3.jsonl`：17 状态、9 episode、142 actions、Reliable=17；quality gate PASS，加入了 BASH 的真实根动作证据。
- BASH ShadowDiff 已双跑并生成 `data/m3e-bash-reliable-shadowdiff-manifest.json`：2/2 `match=true`、`mismatch_count=0`、字节一致，进一步补齐确定性攻击语义的 M3e 证据。
- 新增第二个真实随机递归夹具 `m2-contingent-random-commands.jsonl`：TeacherEvaluator 在 NOSL exact 下输出 27 个 Chance 节点、概率和 1.0、27/27 后继 Max（每个至少 3 个动作），并保留两个并列最优根动作（Defend/Sword Boomerang）。ShadowDiff 对 Sword Boomerang 根动作 `match=true/mismatch_count=0`，重复报告字节一致（`m3e-contingent-random-shadowdiff-manifest.json=pass`）；因真实随机目标是 realized hidden outcome，验证置信度保持 Estimated；详见 `data/m2-contingent-random-verification.json`。
- 新增确定性 `INFLAME + DEFEND` CLI↔Shadow holdout：在包含 Power 卡的真实 root state 上执行 `DEFEND_IRONCLAD`，ShadowDiff `Reliable/match=true/mismatch_count=0`，重复运行 SHA-256 `8FAB98B2467F2D0C00116D34814699D447F2A3A1764F9CB93092920D4B9EAABB` 完全一致。该 root 已并入 `data/m5-reliable-holdout-backed-v4.jsonl`，严格 Reliable 状态由 17 增至 18，manifest/split/quality gate 均 PASS；这只是新增一个语义夹具，不代表 M3e 全量 holdout 已关闭。
- 按 v4 严格候选重新计算 root/action 覆盖：`data/m3e-reliable-holdout-coverage-v5.json` 记录 11 个严格候选 root-best-action 对，其中 10 个已有零差异报告（90.91%），`mismatch_count=0`；唯一未对齐项是 BATTLE_TRANCE 历史行缺少 `teacher_state_reference`，因此整体仍保持 `partial`，不放宽 Reliable 门禁。
- 已重建 BATTLE_TRANCE 真实 teacher 配对并补充并列最优 `DEFEND_IRONCLAD` 动作的 CLI↔Shadow 报告：`m5-reliable-holdout-backed-v5.jsonl` 保持 18 个 Reliable 状态、148 个动作，`m3e-reliable-holdout-coverage-v6.json` 的严格候选 root-best-action 覆盖达到 12/12、`mismatch_count=0`、重复报告字节一致。该 complete 仅针对 18 行严格实验候选；全量语义分层 M3e 仍为 partial。
- M1 运行时概率审计新增 `data/m1-runtime-probability-audit.json`：扫描 4 个 NOSL exact 标签文件、6 个递归策略根、33 个 Chance 组/134 个 Chance 节点；所有 exact 概率质量和均为 1、等价后继 Max 有动作的节点 110 个、采样/Estimated 分支混入数为 0，审计 `verdict=pass`。该审计覆盖当前已生成 exact fixture，不等价于所有随机算子（`CombatPotionGeneration` 仍未被战斗路径引用）的全量运行时证明。
- FLAME_BARRIER 已完成独立真实 CLI↔Shadow 证据（`Reliable/mismatch_count=0`、重复字节一致），但其教师 root 同时将 `EndTurn` 标为并列最优；由于 EndTurn 的未知洗牌报告只能是 Estimated，该 root 未晋级严格 Reliable 候选，保留在证据层，避免 tie-group 不完整造成伪 Reliable。严格候选权威版本为 `m5-reliable-holdout-backed-v7.jsonl`（18 行、12/12 root-best-action 覆盖）。
- 刷新一份过期卡牌 direct-matrix 报告：`BATTLE_TRANCE` 当前 ShadowDiff `Reliable/match=true/mismatch_count=0`，重复运行 canonical SHA-256 一致；`m3e-card-direct-holdout-coverage-v2.json` 重新统计为 140/596 源状态对齐、92 个 Reliable、47 个 Estimated、1 个 Uncalculable，已对齐 mismatch 从 123 降至 121。该报告仍为卡牌语义证据层，不能把 direct-matrix 样本直接晋级为 NOSL Reliable 教师标签。
- 修正 `PIERCING_WAIL` 的真实语义差分：ShadowDiff `BuildCard` 现在生成全敌人临时 Strength 下降与来源 Power，Core 同步负 Strength Power 并刷新公开敌方攻击意图。定向构建与 `CardTextSemanticCompilerTests`（299/299）通过；direct-matrix 报告重复运行 `Reliable/match=true/mismatch_count=0`，对齐 mismatch 从 121 降至 118。统计见 `data/m3e-card-direct-holdout-coverage-v4.json`；其余未验证语义仍不进入 Reliable。
- 修正 `UPPERCUT` 的共享 `power` 预览语义：ShadowDiff 现在将同一数值分别映射为目标敌人的 Weak 与 Vulnerable。定向 ShadowDiff 重跑为 `Reliable/match=true/mismatch_count=0`，重复字节一致；卡牌 direct-matrix 对齐 mismatch 从 118 降至 115，统计见 `data/m3e-card-direct-holdout-coverage-v5.json`。未覆盖的复杂 Power 仍保持降级。
- 修正两类消耗触发 Power：`DARK_EMBRACE` 映射为 `TRIGGER_CARD_EXHAUSTED_DRAW`，`FEEL_NO_PAIN` 映射为 `TRIGGER_CARD_EXHAUSTED_BLOCK`，并补齐对应 Power ID/触发阶段。两份 direct-matrix 报告均重复验证为 `Reliable/match=true/mismatch_count=0`；卡牌对齐 mismatch 从 115 降至 111，统计见 `data/m3e-card-direct-holdout-coverage-v6.json`。
- 修正 `THRASH`：描述中的“双击伤害”映射为 `Repeat=2`，并对随机攻击牌消耗接入 `CombatCardSelection`。真实 direct-matrix 重跑为 `Estimated/match=true/mismatch_count=0`（随机消耗结果保持 Estimated），重复字节一致；卡牌对齐 mismatch 从 111 降至 107，统计见 `data/m3e-card-direct-holdout-coverage-v7.json`。
- 修正 `FIGHT_ME`：多段攻击使用描述中的 repeat，玩家获得 Strength、敌人获得 Strength，并设置正确的目标方向。真实 direct-matrix 报告重复验证为 `Reliable/match=true/mismatch_count=0`；卡牌对齐 mismatch 从 107 降至 102，统计见 `data/m3e-card-direct-holdout-coverage-v8.json`。
- 修正 `SETUP_STRIKE`：本回合 Strength 施加到玩家，并同步 `SETUP_STRIKE_POWER`。真实 direct-matrix 报告重复验证为 `Reliable/match=true/mismatch_count=0`；卡牌对齐 mismatch 从 102 降至 98，统计见 `data/m3e-card-direct-holdout-coverage-v9.json`。
- 修正 `EXPOSE`：清除敌方 Block/Artifact 后施加目标 Vulnerable。真实 direct-matrix 报告重复验证为 `Reliable/match=true/mismatch_count=0`；卡牌对齐 mismatch 从 98 降至 96，统计见 `data/m3e-card-direct-holdout-coverage-v10.json`。
- 修正 `OUTBREAK`：将 Poison 触发卡映射到 `EffectKind.Outbreak`，按引擎顺序施加 Poison 并立即结算触发伤害及 Power 同步。真实 direct-matrix 报告重复验证为 `Reliable/match=true/mismatch_count=0`；卡牌对齐 mismatch 从 96 降至 93，统计见 `data/m3e-card-direct-holdout-coverage-v11.json`。
- 修正 `DODGE_AND_ROLL`：识别 “Next turn, gain Block” 并写入 `ScheduleCurrentBlock`，同步 `BLOCK_NEXT_TURN_POWER`。真实 direct-matrix 报告重复验证为 `Reliable/match=true/mismatch_count=0`；卡牌对齐 mismatch 从 93 降至 91，统计见 `data/m3e-card-direct-holdout-coverage-v12.json`。
- 修正 `BLUR`：识别 `blur` 预览并保留格挡至下一回合，写入 `BLUR` Power 及 Block/TurnStart 触发阶段。真实 direct-matrix 报告重复验证为 `Reliable/match=true/mismatch_count=0`；卡牌对齐 mismatch 从 91 降至 89，统计见 `data/m3e-card-direct-holdout-coverage-v13.json`。
- 修正 `AGGRESSION`：无数值 stats 的卡牌现在仍写入持久 `AGGRESSION` TurnStart Power。真实 direct-matrix 报告重复验证为 `Reliable/match=true/mismatch_count=0`；卡牌对齐 mismatch 从 89 降至 87，统计见 `data/m3e-card-direct-holdout-coverage-v14.json`。
- 已生成 `data/m3e-live-smoke-manifest-current.json`：7 个报告、4 Reliable、3 Estimated、0 mismatch、全部 match，版本锁一致；该 manifest 是当前 smoke 证据，不代表全量 M3e 出口。
- `build_m3e_smoke_manifest.py` 已重新校验原始 7-report manifest：`verdict=pass`、`report_count=7`、`repeats=7`、`failures=0`，输出 `data/m3e-live-smoke-manifest-verified.json`。
- 为处理回合末未知洗牌，`ShadowSimulationTransitions` 增加了仅由公开手牌构成的语义唯一排列 chance 分支；分支显式标记 `Estimated/estimated_hand_shuffle`，不晋级 Reliable。`CombatSearchSession` 同步修正随机 EndTurn 的 policy prefix，使 CLI `end_turn` 能映射到策略树根动作。相关定向回归 14/14 通过，TeacherEvaluator 构建 0 警告/0 错误；后续应从未加工的 public-state 源重新生成 pilot，避免把生成输出再次作为输入。

本轮可复现验证：

- Core：`748 passed, 0 failed, 0 skipped`；构建仍有 `2 warnings`。
- M0/M1 定向回归：`NoslExpectimaxTeacherTests + P0TrainingContractTests` 17/17；`RandomModelTests + ExpectimaxEngineTests` 14/14。
- Python training：`52 passed, 1 skipped`（命令排除 `training/test_replay_action.py`）。
- CLI v0.111 consistency smoke：`6 passed`；这只验证版本/协议 smoke，不等于全部语义闭环。

遗物与卡牌缺口计划见 `RELIC_CARD_GAP_COMPLETION_PLAN.md`。返工后 catalog 将 handler 支持、语义 hold、未实现 hook、OutOfScope 与 strict evidence 分开统计；未知 RNG、placeholder/count-only 和 teacher-conditioned 报告不得进入 Reliable NOSL 主训练集。当前统计和证据以 `relic-coverage.json`、`card-semantic-signature-report.json`、`p1-repeat-verification.json` 为准。

无头和 RNG 研究已经完成，见 `docs/research/headless-simulator-research-agent.md`。结论是：`sts2-cli` 用作真实引擎 oracle；影子模拟器承担高吞吐 NOSL 分支；AutoSlayer 不作为教师；当前 v0.111 `RunRngSet` 有 12 条流，而主快照只捕获 7 条 combat 流。

## 1. 已锁定的核心决策

- **训练目标**：严格 NOSL。教师不能使用本局具体 RNG 状态、未来牌堆顺序或实际随机结果来决定标签。
- **随机先验**：使用 v0.111.0 规则目录、公共可见牌组构成和公共历史建立信念分布。
- **教师算法**：新增离线 `NoslExpectimaxTeacher`；训练集允许高成本完整穷举。
- **运行时算法**：模型排序 + 限时 `CombatSearchSession`/Expectimax 兜底，不在运行时执行完整穷举。
- **目标函数**：并行输出 `Balanced`、`HighestDamage`、`MinimumLoss`；Balanced 作为默认排序目标，死亡概率和尾部风险作为独立标签。
- **未知语义**：保留并标记 `Estimated`/`Uncalculable`，不进入 Reliable policy 主损失。
- **外部求解器**：`reference/CombatSolver`、RandomForeseer 和其他 SL 求解器不得进入代码、教师标签、模型特征或运行时依赖。只允许隔离地参考 root snapshot、COW/fork、coverage catalog、state fingerprint 和 strict-diff 等架构思想，必须独立实现；其真实 RNG、已知未来牌序和 realized branch 只能作为审计 oracle，不能进入 NOSL 标签。
- **数据规模**：`1k → 10k → 100k`，每阶段通过重建和分层质量门禁后再扩展。
- **Top-K**：默认 `K=5`，同时保存所有合法动作的价值和 soft policy。
- **并行方式**：按 root state/shard 并行；不共享可变搜索树和 RNG。

## 2. 必须修正的当前计划描述

当前代码和文档需要明确区分以下事实：

1. `ExpectimaxEngine` 仍是通用 Max/Chance 算法和回归组件；生产教师入口由 `NoslExpectimaxTeacher` facade 负责。
2. `CombatSearchSession` 是运行时主要搜索入口，也承载当前 NOSL 离线路径枚举；它尚未形成经过验收的递归条件策略树，不能把当前 `BuildPolicies` 直接视为完整 Expectimax policy。
3. `TeacherWorker` 已接通 `TeacherEvaluator` 协议；请求不再把完整 `teacher_snapshot` 传给 evaluator。当前已移除对输入 `teacher_snapshot` 的硬依赖，无 teacher payload 时仍可通过 `public_state + nosl_belief_state + legal_actions` 调用 evaluator；`state_hash_teacher` 仅作为可选审计 sidecar reference。缺少 evaluator 且未启用 heuristic fallback 时仍显式失败，不会生成伪 Reliable 标签。
4. `LiveCombatSnapshotAdapter` 会捕获完整 RNG 状态和真实牌堆顺序，这些数据只能进入审计 sidecar，不能直接喂给 NOSL evaluator。
5. `TeacherStateView` 不再等同于“教师可直接使用的搜索输入”。应拆分为：
   - `AuditTeacherSnapshot`：保存完整实机快照，仅供差分、复现和错误分析；
   - `NoslBeliefState`：去除具体 RNG/未来牌序后的教师输入；
   - `PublicStateView`：学生模型唯一可见输入。
6. `CombatSearchSession.GenerateActions` 当前仍自行从手牌、药水和本地 choice 构造动作，并未完全以 CLI `ActionCandidate` 集合为唯一真源。`trace_to_training.action_candidates` 的 fallback 现已与 CLI stable ID 对齐，并为 AnyEnemy 卡展开每个目标；仍必须补齐 `legal_action_set + stable ID + CLI index mapping` 后再满足本计划的动作契约。
7. `ChanceDistributionProvider` 和 `BeliefStateKeyProvider` 虽已声明，但当前 facade 未真正把它们作为必需依赖注入。M3 必须消除隐式均匀概率和隐式字符串 key。

## 3. NOSL 信念状态

新增 `NoslBeliefState`，至少包含：

```text
public_observation
known_deck_composition
known_hand
publicly_observable_history
draw_eligible_counts
discard_counts
exhaust_counts
removed_counts
known_top_prefix
conditioning_constraints
random_effect_distributions
visible_powers
visible_relics
history_counters
round_context
belief_signature
```

明确禁止：

```text
rng_state_words
rng_stream_state0..state3
future_draw_order
hidden_discard_order
realized_hidden_outcome
teacher_only_state
```

### RNG 掩码规则

不得再使用 `RngState = 0/1` 或“全零状态字”作为 Known/Unknown 的隐式哨兵。新增显式契约：

```text
RngAvailability = Known | MaskedUnknown | Missing
```

- NOSL evaluator 输入必须为 `MaskedUnknown`，且不得调用会返回具体随机数的 API；
- `Unknown` stream 的 `NextInt`/`NextUnsignedLong` 不得返回 `0` 继续执行，必须生成 chance operator 或返回 `Uncalculable`；
- `Counter` 只允许作为审计诊断，不参与动作价值、NOSL 缓存 key 或学生特征；
- raw state words、counter 和实际 outcome 只写 sidecar。

锁定的 v0.111 `RunRngSet` 有 12 条流：

```text
UpFront, Shuffle, UnknownMapPoint,
CombatCardGeneration, CombatPotionGeneration, CombatCardSelection,
CombatEnergyCosts, CombatTargets, MonsterAi, Niche,
CombatOrbGeneration, TreasureRoomRelics
```

当前 adapter/CLI 主战斗快照只捕获 7 条 combat 流。其余 5 条应补到 audit sidecar 和版本门禁；它们仍不得进入 NOSL public view。`RandomModel.cs` 的版本注释也必须从 v0.110 更新并绑定锁定程序集测试。

### 牌堆表示

不保存未来具体顺序，使用“已知有序前缀 + 可抽多重集合 + 分桶计数 + 条件约束”：

```text
STRIKE: 3
DEFEND: 5
BASH: 1
```

每次抽牌后按公共历史更新剩余计数，采用不放回抽样概率。`ExhaustPile` 和 removed cards 不得进入可抽池；升级、附魔、动态费用、临时状态等会改变语义的同 ModelId 卡牌必须使用 `semantic_card_signature` 区分。Scry、公开顶牌、把牌放顶/底等效果更新 `known_top_prefix` 和约束。不得使用 teacher snapshot 中的实际 draw pile 顺序。

### 状态 key

不再使用一个字符串 key 同时承担所有职责。统一 canonical serializer 后分别生成：

```text
PublicObservationKey
NoslBeliefKey
SearchBehaviorKey
AuditExactKey
CycleKey
```

其中 `NoslBeliefKey` 只能包含：

- 剩余牌种计数；
- 随机池权重；
- 公开历史；
- 已知计数器；
- 已知顶牌前缀和公开位置约束；
- 规则数据库版本。

不得包含具体 RNG 字、未来牌序、runtime object hash 或完整隐藏 outcome。`SearchBehaviorKey` 必须包含所有会影响未来结算的 Card/Status/Potion/Power/Relic/History/Gold/pending 字段；`AuditExactKey` 额外包含 provenance/evidence/审计计数。所有序列化必须使用 UTF-8、InvariantCulture、稳定集合排序和 schema version。

### 3.1 敌人信息与行为契约（NOSL）

敌人必须和卡牌、遗物一样经过“结构化 → 语义 handler → 真实 CLI fixture → ShadowDiff → Reliable/Estimated/Uncalculable”流程，但敌人的核心对象是 **AI 状态机和 Intent 转移**，不能简单压成一个攻击数值，也不能把 CombatSolver 的隐藏 MonsterAi 状态直接作为 NOSL 输入。

#### 3.1.1 运行时可见信息

真实使用时，学生模型只消费 `PublicStateView` 中由 `LiveCombatSnapshotAdapter`/CLI 实际公开的敌人字段：

```text
enemy_id
enemy_instance_id（仅使用 CLI 公开的稳定 ID；隐藏 runtime object hash 仍留在 sidecar）
hp / max_hp
block
visible_statuses
visible_powers
current_intent_type
damage_per_hit
hits
visible_effects
visible_target
is_alive
```

`current_intent_type`、伤害、段数和可见效果是当前回合的决策输入；下一回合尚未显示的 Intent、MonsterAi RNG、MoveStateMachine 内部指针、隐藏计数器和实际随机结果不得进入 public observation、`NoslBeliefKey` 或学生特征。若字段未被 CLI 公开或未通过版本化证据验证，必须保留 `Estimated`/`Uncalculable`，不得猜测后标为 Reliable。

#### 3.1.2 结构化 Enemy/Intent Schema

新增或补齐以下逻辑结构（名称可按现有 C# 模型适配）：

```text
EnemyState
├── enemy_id / instance_id
├── hp / max_hp / block
├── statuses / powers
├── is_alive / is_hittable / is_minion / is_primary
├── target_rules
├── phase / summon_parent
├── ai_counters
├── visible_intent
└── evidence / source / version

EnemyIntentState
├── intent_id / intent_type
├── damage_per_hit / hits
├── target_rule / target_set
├── effects
├── trigger_phase / condition
├── candidate_next_intents
├── probability / probability_known
├── rng_stream
└── restriction_reason / evidence
```

`CreatureState` 当前缺少 `is_hittable`、Minion/Primary 关系、目标规则和完整 AI 计数器；M3c 必须补齐这些字段，或在字段缺失时显式记录 `target_rules_unknown`，不得默认把所有存活敌人当作可命中目标而晋级 Reliable。

#### 3.1.3 当前影子模拟器行为（基线）

`DeterministicSimulator.ProjectToNextPlayerTurn` 当前按以下顺序处理敌人：

1. 结算玩家回合结束效果、遗物、Power、Orb 和 Doom；
2. 清理/保留/消耗玩家手牌；
3. 设置 `EnemyTurnActive`；
4. 按快照中的敌人顺序遍历存活敌人；
5. 跳过 STUN 敌人；
6. 按 `IntentState` 顺序逐段结算攻击伤害和 `SafeEffects`；
7. 使用敌人状态、玩家格挡和伤害修正；
8. 敌方阶段结束后结算 Doom、推进回合、执行回合开始效果和抽牌。

已验证增量：Shrinker Beetle 的公开 `DebuffStrong` 意图已映射为玩家 `SHRINK` 状态，并同步真实 Power 表示（amount=-1、applier、DamageDecrease=30、3 回合）。

当前默认 `SnapshotEnemyIntentForecastProvider` 在未来回合复用当前 Intent 并标记 `Estimated`。这只代表当前回合的确定性回放和未来意图的保守估计，不代表已经复刻完整 Monster AI。

#### 3.1.4 NOSL 的敌人概率模型

当前已显示 Intent 在本回合内视为确定分支；未来未显示 Intent 必须通过 `EnemyAiBelief`/`EnemyIntentDistribution` 产生候选分支：

```text
P(intent_next | enemy_id, visible_intent, public_history,
  turn, hp_ratio, visible_powers, visible_allies)
```

Expectimax 使用：

```text
Q(s, a) = Σ P(i | public_state) × V(T(s, a, i))
```

概率只能来自版本锁定的状态机分析、真实 CLI 多次采样或已验证的运行时证据。未确认的概率不得均匀化，不得使用本局 MonsterAi RNG 直接决定标签。每个敌人分支记录 `probability_known`、`outcome_quality`、`probability_mass_covered`、`rng_consumption_vector` 和证据引用。

#### 3.1.5 参考项目可采用的设计

允许隔离参考：

- `reference/CombatSolver` 的 `BranchMonsterAiState`、`StateLog`、条件/随机分支、重复限制、冷却和多回合 forecast 结构；
- `reference/slay-the-spire-2-emulator` 的 `EnemyState`、`MoveIndex`、`LastMove` 和按行为模板复用规则。

不得复制：

- CombatSolver 的 MonsterAi RNG、隐藏 MoveStateMachine 状态或已知未来行动；
- emulator 的自定义 `DotNetRandom`/`GameRng` 作为 v0.111 真值；
- 任何只在 SL 模式可见的实际随机结果。

推荐本项目采用：

```text
EnemyAiState（教师/sidecar）
    = 当前 Intent + 历史 + 已验证计数器 + 候选分支

PublicStateView（学生）
    = 当前可见 Intent + 可见敌人状态

EnemyBehaviorTemplate
    = Attack / MultiAttack / Defend / Buff / Debuff /
      Summon / RandomTarget / MultiPhase / Conditional
```

多个敌人可复用同一行为模板；只有参数、条件和真实证据不同的部分才单独实现。

#### 3.1.6 真实使用与训练分工

真实使用时采用闭环：

```text
读取当前 public snapshot
    → 读取当前可见敌人 Intent
    → 模型选择玩家动作
    → CLI 执行动作
    → 重新读取 snapshot
```

训练时采用：

```text
真实 CLI 快照（状态真值）
    → ShadowDiff 验证
    → 当前 Intent 确定性结算
    → 未来 Intent 概率展开
    → Expectimax Top-K / value / risk 标签
```

完整 AI 状态、隐藏 RNG、分支树和实际未来序列只能写入 `teacher-sidecar`，不得进入主 JSONL/Parquet。学生训练可以完全离线，但每个版本仍需保留真实 CLI holdout 回放作为真值门禁。

#### 3.1.7 敌人验收门禁

敌人行为进入 Reliable 主标签前必须同时满足：

1. 当前 Intent、伤害、段数和效果字段可结构化；
2. 目标规则和可命中集合已明确；
3. 多敌人行动顺序与 CLI 一致；
4. 每次命中重新读取当前 enemy state；
5. 条件/召唤/复活/多阶段状态已纳入 `SearchBehaviorKey`；
6. 未知未来 Intent 不被复制成确定动作；
7. 真实 CLI ↔ shadow `mismatch_count=0`；
8. 重复运行的报告和概率质量字节一致；
9. public observation 不含教师 AI 状态或 RNG 泄漏。

未满足条件的敌人仍可用于诊断或辅助 value/risk 数据，但标签质量必须为 `EstimatedByHeuristic`、`SampledWithConfidenceInterval` 或 `Uncalculable`，不能进入 Reliable policy 主损失。

## 4. NOSL Expectimax 算法

新增接口：

```text
NoslExpectimaxTeacher
NoslExpectimaxOptions
NoslTeacherResult
ChanceDistributionProvider
BeliefStateKeyProvider
```

### Max 节点

目标契约是以真实 CLI 的合法 `ActionCandidate` 为准：

```text
PlayCard
UsePotion
Choice
EndTurn
```

模拟器不得自行补出 CLI 不允许执行的根动作。当前实现尚未满足：`CombatSearchSession.GenerateActions` 仍会本地遍历手牌/药水/choice。M3 必须让 normalizer 提供完整 `legal_action_set`、stable action ID、CLI index 映射和限制原因，搜索只能展开该集合；非法动作拒绝和 index↔stable-ID parity 是强制测试。

### Chance 节点

严格执行：

```text
Q(s, a) = Σ P(z | s, a) × V(next_state_z)
```

机会分支之后必须重新进入 Max 节点，不能选择单个“最佳随机结果”。

生产标签必须保存递归条件策略树，而不是单条 Principal Variation：

```text
PolicyNode.Max(action_id -> child)
PolicyNode.Chance(observable_outcome_id, probability -> child)
root_action_value = sum(probability * child_value)
```

未来尚未观察的随机结果不能条件化更早动作。`ActionLine` 只保留为审计路径；`TeacherEvaluator` 必须优先消费按 root action 聚合后的 policy value。确定性根动作也要生成 `probability=1` 的 policy。

### 精确分支策略

优先按等价结果展开：

- 随机目标：按合法目标身份展开；
- 随机生成卡牌：按规则随机池和权重展开；
- 随机弃牌：按可选牌身份和剩余数量展开；
- 未知抽牌：按剩余牌多重集合展开；
- 洗牌：用组合计数/动态规划表示，不直接枚举所有 `n!` 排列；
- 复杂选择：按每个合法选择结果展开。

所有随机入口统一通过版本化 `RandomOperatorRegistry`，包括卡牌、药水、Power/Relic listener、回合开始/结束、自动出牌、敌人 AI、随机目标/状态/伤害/球/生成/消耗/稀有牌选择。每个 operator 必须声明 eligibility、权重、replacement、selection order、observability、RNG stream、证据和版本。

分支概率必须满足：

```text
sum(probability) == 1
```

概率未知时不得强制使用均匀分布。

每个 branch set 还必须记录：

```text
probability_known
outcome_quality = Exact | Sampled | Unknown
probability_mass_covered
proposal_mass
effective_sample_size
confidence_interval
rng_consumption_vector
```

相同 `post SearchBehaviorKey + observable delta` 的结果先合并并累加概率；完整排列、隐藏生成结果和实际 RNG label 只进 sidecar。达到分支上限时必须保留未覆盖质量并降级，不能把截断后的前 N 个结果重新当成总概率 1。

### 搜索边界

首版教师 horizon 明确限定为 `one_player_turn`，搜索到当前玩家回合的动作终点：

```text
EndTurn
玩家死亡
敌人全部死亡
状态不可计算
```

死亡或敌人全灭后必须立即短路，禁止继续出牌、抽牌或推进下一回合。不以固定深度作为正常出口；固定深度只作为诊断模式。

如果标签需要包含 EndTurn 后的敌人结算或更远回合，则只有在结构化 intent effects、目标规则和基于公共历史的 `EnemyAiBelief/MonsterAi` 概率模型完整时才能进入 Exact。复制当前 intent 作为未来 intent 的路径只能为 Estimated/Uncalculable。

### 资源硬上限

默认：

```text
单状态最多 100,000,000 expanded nodes
单状态最多 8GB 估算工作内存
进程级内存预算和 worker quota
```

达到硬上限时：

```text
search_complete = false
confidence = Uncalculable
label_quality = Uncalculable
```

不得将截断结果伪装成 Reliable。

当前 `MaximumStateBytes` 主要是单状态估算，不覆盖 frontier、三套 priority queue、visited、ancestor、checkpoint 和字符串 key。M3 必须增加 session/process 级真实内存压力门禁、CancellationToken 和溢出保护；预算终点保留原状态并标 `BudgetBound`，不得额外投影敌方回合后打分。

### 缓存和等价合并

缓存键必须使用 `SearchBehaviorKey + NoslBeliefKey`。允许合并：

- 相同公共状态；
- 相同剩余牌计数；
- 相同随机池；
- 相同 Power/遗物计数器；
- 相同回合上下文。

不得合并具有不同随机分布的状态。

`ChancePath` 的展示 label 不得拼入转置主键；先按规范化 post-state 合并概率，原始 path 只作为 sidecar audit。转置缓存与训练结果候选必须分离：即使两个 root action 到达同一状态，仍要为每个 root action 保存价值和质量。

## 5. 标签格式

每个 root state 保存：

```text
teacher_mode = NOSL_EXACT_OFFLINE
teacher_best_actions
teacher_top_k
action_values
legal_action_set
action_mask_and_restrictions
soft_action_distribution
contingent_policy_tree
balanced_value
damage_value
loss_value
death_probability
minimum_value
maximum_value
variance
cvar
chance_branch_count
probability_mass
probability_known
outcome_quality
effective_sample_size
confidence_interval
expanded_nodes
search_depth
search_complete
confidence
label_quality
belief_signature
audit_snapshot_reference
```

`action_values` 必须覆盖所有合法 root action。未完成、未知或受限动作也要保留记录，并使用 `value = null + quality/reason`，不得从 action set 中静默删除。`teacher_top_k` 只是展示/蒸馏子集，不能替代全动作价值。

### Top-K 规则

- 默认 `K=5`；
- 期望值差异 `≤ 1e-6` 时视为并列；
- 并列动作按规范化 `action_id` 字典序稳定排序；
- 保存 tie group；
- 同时生成基于优势值和温度的 soft policy；
- `END_TURN` 永远保留在候选集合中。

### 风险规则

Balanced 先按期望值排序：

```text
Balanced expected value
```

死亡概率、最差值和 CVaR 作为独立输出；只有期望值处于 epsilon 并列范围内时，才用风险指标打破平局。

### 质量分级

```text
ExactComplete
ExactWithKnownChance
SampledWithConfidenceInterval
BudgetBound
EstimatedByHeuristic
Uncalculable
```

上述 `label_quality` 和概率元数据目前尚未在 C#/Python 全链路稳定承载，属于 M3 契约交付，不是当前已完成能力。只有 schema、序列化和质量门禁均能复现这些字段后才允许使用对应名称。

训练权重：

```text
ExactComplete / ExactWithKnownChance：policy/value 主损失，权重 1.0
SampledWithConfidenceInterval：辅助损失，权重 0.5–0.8
BudgetBound：默认不进入 policy 主损失；仅在明确上下界/覆盖质量时进入辅助 value/risk
EstimatedByHeuristic：仅辅助 value/risk
Uncalculable：不进入最优动作主损失
```

死亡概率、variance 和 CVaR 必须按 branch probability 加权，并记录 ESS/covered mass。异常、缺概率或 evaluator 失败时输出 `null + Uncalculable + error_code`，不得用 `death_probability=1` 伪装成真实标签。

## 6. 数据生成管线

### Root 状态来源

采用“覆盖夹具 + 随机轨迹”混合：

1. 语义专项 fixture：
   - 消耗；
   - 虚无；
   - 保留；
   - 指定弃牌；
   - 随机目标；
   - 随机生成牌；
   - 多敌人；
   - 低血量；
   - 能量不足；
   - Power/遗物触发；
   - 复杂选择。
2. 不同 seed 的自然合法轨迹；
3. 当前策略、高预算搜索、随机合法动作和近最优动作；
4. 专门构造 hard negative 和高 regret 状态。

### 数据流

```text
CLI public observation
        │
        ▼
PublicStateNormalizer
        │
        ▼
NoslBeliefBuilder
        │
        ▼
NoslExpectimaxTeacher
        │
        ├── 聚合训练标签
        ├── soft policy
        ├── 风险统计
        └── teacher sidecar
```

### Sidecar 规则

完整实机快照、隐藏 RNG、真实牌序和完整分支树只写入：

```text
training/teacher-sidecar/v0.111/
```

训练主数据只保存：

```text
public state
belief signature
aggregated labels
quality metadata
audit_snapshot_reference
```

sidecar 不进入模型输入，不进入 public Parquet 特征。

主 JSONL/Parquet 中不得继续内嵌完整 `teacher_snapshot`；只保存 `audit_snapshot_reference`。sidecar 还必须同时记录 original assembly hash、实际加载的 patched/runtime binary hash 和 patch profile，避免 CLI 兼容门禁只验证 `.original` 文件而无法重建真实运行环境。

### 数据规模

```text
Smoke：1,000 states
Pilot：10,000 states
扩展：100,000 states
```

每阶段保存：

```text
raw JSONL
normalized Parquet
teacher labels
chance statistics
quality report
DatasetManifest
shard SHA-256
generator configuration hash
```

### 数据切分

按以下联合分组切分：

```text
public_state_hash
belief_signature
episode_id
run_seed
feature_schema_version
legal_action_set_hash
```

同一 public state 的不同隐藏状态不得跨 train/validation/test 分区。
实现中的 split policy 必须与这里的联合键统一；重复观测先按 `public_state_hash + belief_signature + schema + legal_action_set` 聚合或去重，并把规则写入 DatasetManifest。当前 1k smoke 的 `126` duplicate warning 必须在 M4 重新验收前解决。

## 7. 概率目录和语义门禁

新增版本化概率目录：

```text
data/random-models/v0.111/
```

每个随机算子记录：

```text
operator_id
source_card_or_power
eligible_outcomes
probability_rule
conditioning_fields
replacement_policy
rng_stream_name
evidence_level
validation_fixtures
observability
probability_quality
rule_schema_version
```

概率来源顺序：

1. v0.111 程序集/语义规则；
2. 固定 CLI 探针；
3. 多次实机统计；
4. 版本化人工确认。

未确认概率不得进入 `ExactWithKnownChance`。

当前未验证对象继续门控：

- Power catalog：`20` simulator-supported、`53` simulator-declared、`210` state-captured-only、`30` unknown；
- Relic catalog：98 simulator-supported、2 partially-supported、20 unsupported-known、97 OutOfScope、0 unknown、25 UnverifiableByCli、56 Uncalculable、24 strict evidence-eligible（含 1 个 no-combat-effect）；
- 181 个战斗相关遗物已经全部拥有终态：98 个 handler-supported、2 个语义 hold，20 个仍未实现 hook，81 个为引擎阻塞；97 个非战斗遗物已显式转为 `OutOfScope`。
- Card：单人战斗 `1099/1099` fully-structured/simulator-executable，但 immediately-executable 仅 `458`，且没有 1099 个逐卡 CLI 行为 probe；
- `data/p1-repeat-verification.json` 的权威矩阵为 `31 P0 + 26 P1 Power + 126 P1 Relic + 29 P1 Card = 212 reports`，双跑字节一致；质量分布为 Reliable=96、Estimated=67、Uncalculable=49；
- Relic closeout 不等于 M3 NOSL closeout：可靠遗物报告仍需经过 NOSL belief/chance/policy/strict-diff 依赖检查，不能自动成为教师主标签。

包含这些对象的状态可以记录，但只能生成 `Estimated` 或 `Uncalculable`。

语义支持状态不能只依赖 catalog 字段。每条 action 在进入 Reliable 前还要做 dependency-aware capability check：其读取的 Card/Power/Relic/intent/random operator 全部已验证，且 `UnknownStatePresent`、未知 DynamicVars、未知 TriggerPhase 不影响该动作。

### 7.1 遗物与卡牌缺口收口审查结论

依据 `D:\STS2BestChoice\work\relic-card-completion\HANDOFF.md`、`data/P1_RELIC_VERIFICATION.md`、`data/relic-card-gap-inventory.json` 和 `data/card-semantic-verification.json`：

| 范围 | 当前结论 | 可进入 NOSL Reliable 主标签？ |
|---|---|---|
| 100 个已探针遗物 | 98 handler-supported + 2 PartiallySupported；报告均 match/mismatch=0，但质量分级后仅 24 relic entries strict eligible | 不能直接作为 Reliable；还需通过 action dependency、概率和 NOSL key 门禁 |
| 101 个战斗遗物未达 Reliable | 20 `UnsupportedKnownEffect` + 2 `PartiallySupported` + 25 `UnverifiableByCli` + 56 `Uncalculable` | 否，保持显式阻塞或语义 hold |
| 97 个非战斗遗物 | 当前 catalog 已为 `OutOfScope` | 否；仅作为范围分类，不进入训练 |
| 1099 个单人卡牌变体 | 语义/handler 已覆盖；607 条 direct matrix 行已登记，64 条 direct Reliable、62 条 numeric-upgrade equivalence proof；仍有 mismatch/Uncalculable | 仅通过 direct/equivalence 且依赖门禁的对象可进入 Reliable |
| 77 个多人/盟友变体 | 单人范围外 | `OutOfScope` |

本结论与“遗物和卡牌缺口已补齐”相容，但“补齐”必须拆成三层理解：

1. **语义层**：单人卡牌 1099 个变体已有结构化语义和 handler；战斗遗物 181 个对象均有终态。
2. **行为证据层**：遗物矩阵现有 132 份报告，其中 99 个 handler-supported、仅 `PARRYING_SHIELD` 保持 PartiallySupported；卡牌 direct matrix 已登记 607 行，签名层 46 个 verified_all_variants（含显式 numeric-upgrade equivalence proof）；`UNCEASING_TOP` 已通过空手触发 fixture。PARRYING_SHIELD 多敌 fixture 已加入，但 CombatTargets 未知时仍不进入 Reliable。81 个对象因当前引擎可观测边界阻塞。
3. **训练标签层**：仍受 M3 的 NOSL belief、随机分支、策略树、事件时序和完整 action-value 门禁约束，不能直接把上述报告全部写入主训练损失。

交付文件的报告口径已完成返工：relic 文档登记 126 个动作，card 文档登记 29 个动作；212 个报告的质量、版本和重复性由 `shadowdiff-rework-verification.json` 与 `p1-repeat-verification.json` 自动生成，禁止手工宣称全部 Reliable。

`data/relics/v0.111/relic-coverage.json` 已刷新为 `generated_at_utc=2026-08-30T00:00:00Z`，并绑定 evidence eligibility、source 和 semantic-hold overlay。

语义复核新增的两个 hold：

- `PARRYING_SHIELD`：已加入多敌随机目标 trace；影子使用真实 post-state 目标仅作差分回放，NOSL 仍标记 Uncalculable。
- `UNCEASING_TOP`：新增 6 张零费牌空手 fixture，确认 AfterHandEmptied 抽牌并晋级 Reliable。

## 8. 分阶段实施

### M0：NOSL 契约冻结（核心修复完成，扩展验收待补）

交付：

- `NoslBeliefState`；
- RNG masking；
- hidden deck-order masking；
- public/teacher/sidecar 边界；
- `belief_signature`；
- schema 和版本门禁。
- `PublicStateNormalizer` 和显式 `ObservationView.Public` 构造；
- `RngAvailability`，禁止零值哨兵；
- public snapshot 不携带 teacher-only ordered piles/instance IDs。

出口：

- 相同 public state、不同 RNG 得到相同标签；
- 相同牌组构成、不同隐藏牌序得到相同标签；
- public leakage 为 0；
- `ExhaustPile` 不进入 draw-eligible multiset；
- masking annotation 不降低 confidence；
- evaluator 拒绝 Teacher view 和任何 raw RNG/hidden-order 输入。

### M1：概率和信念引擎（工具已存在，Exact 验收未通过）

交付：

- 剩余牌多重集合；
- 不放回抽牌；
- 随机目标分布；
- 随机卡池分布；
- 随机弃牌分布；
- 概率质量校验；
- 动态信念更新。
- `known_top_prefix` 和位置约束；
- semantic card signature；
- RandomOperatorRegistry 和版本化权重。

出口：

- 小型手工场景与暴力枚举结果一致；
- 概率总和始终为 1；
- 不使用实际 RNG 输出；
- `A,A,B` 抽一张严格得到 `A=2/3, B=1/3`；
- 等价 post-state 合并后质量仍为 1；
- Unknown stream 不返回 0，未知权重不伪装均匀；
- sampled branch 带 covered mass、ESS、CI，且不标 Exact/Reliable。

### M2：NOSL Expectimax evaluator（协议桥接完成，标签正确性未验收）

交付：

- Max/Chance 搜索；
- 全动作价值；
- top-k；
- soft policy；
- 三目标；
- 风险统计；
- exact/budget/un calculable 状态分类。
- 全部合法 root action 的质量和值；
- 递归 contingent policy tree；
- deterministic/chance root 的统一 policy 表示。

出口：

- `TeacherWorker` 可以调用真实 evaluator；
- 不再依赖 heuristic fallback 生成主标签；
- evaluator 输出绑定完整版本 metadata；
- facade 和 TeacherEvaluator 只按 root policy expected value 排序，不忽略随机动作；
- Chance→Max 反例与独立暴力枚举一致；
- Principal Variation 只作诊断，不作为 NOSL 执行策略。

### M3：影子模拟器 correctness closeout（当前最高优先级）

#### M3a：状态契约与确定性

交付：

- `PublicStateNormalizer`；
- `NoslBeliefKey`、`SearchBehaviorKey`、`AuditExactKey`、受证明约束的 `CycleKey`；
- `StableInstanceIdFactory` 和单调 generated-instance counter；
- UTF-8/Invariant canonical serializer；
- Power/Relic source-aware instance key；
- `ChanceBranchFactory`、显式 `RngAvailability`、`RngContract v0.111`。

出口：

- 删除 `Guid.NewGuid()` 和基于当前牌堆数量的可复用生成 ID；
- key mutation 测试覆盖 Card/Status/Potion/Power/Relic/History/Gold/pending fields；
- `zh-CN`/`en-US`、单线程/多进程得到相同 key/hash；
- source-aware Power 不错误合并；
- hidden RNG/order 变化不改变 NOSL key/label。

#### M3b：精确 chance 和动态规划

交付：

- 等价结果合并；
- 牌堆多重集合 DP；
- 统一 RandomOperatorRegistry；
- lazy/streaming branch set 和贯穿式 BranchBudget；
- exact/sampled/unknown 质量元数据；
- observability-aware outcome key；
- RNG consumption vector。

出口：

- 小池与独立暴力枚举逐值一致；
- 所有 exact operator 的概率质量为 1；
- 不再使用全排列尾序枚举、字典序前 N 个结果或 `seed % n` 作为 Exact；
- 所有随机 listener/turn hook/药水/敌人随机效果均经过同一 chance 边界；
- 大场景触顶后保留未覆盖质量并正确降级。

#### M3c：事件时序、终点和敌人

交付：

- 按真实顺序执行的 event/lifecycle queue；
- pending choice/deferred generation/quiescent fork 门禁；
- 统一 `TryFinalizeCombat`；
- 每次命中重新读取当前 enemy state；
- 结构化 target rules、intent effects 和 `EnemyAiBelief`。

出口：

- 死亡/全灭后不能继续动作或回合推进；
- draw interleave、自动出牌、Power/Relic listener 不再丢状态；
- 多敌 Doom、回合结束击杀、弱化后多段攻击与 CLI 一致；
- 未知未来 intent 不被复制成确定动作；
- `CopyStateInto` 字段完整性有自动门禁。

#### M3d：条件策略和训练标签

交付：

- chance 后重新 Max 的递归 policy tree；
- root action expected value aggregation；
- 全合法动作 value/mask/quality；
- weighted variance/CVaR/death probability；
- stable Top-K/tie/soft policy。

出口：

- 已复现的“随机根动作期望 160、固定动作 60”反例返回随机动作 Top-1；
- 无未来 outcome clairvoyance；
- deterministic 和 stochastic action 使用同一聚合协议；
- evaluator 异常输出 null/Uncalculable，而不是伪造死亡标签。

#### M3e：真实引擎闭环

交付：

- CLI root action 执行与 shadow 同动作执行；
- HP/Block/Energy/piles/Power/Relic/history/RNG-counter delta strict diff；
- original/runtime-patched DLL hash 和 patch profile；
- 固定输入双跑清单。
- 当前 ShadowDiff 已在 HP、Block、Energy、牌堆、Power、Relic、RNG counter 之外，对结构化且可观测的敌方 Intent type/damage/hits 做比较；不支持或缺少数值的 Intent 仍按显式降级处理。
- 最新旧版 inspect trace 仅验证了 choice/error-only 的无崩溃降级路径，未提供可用于 mismatch 门禁的 gameplay public post-state，因此不能据此关闭 M3e。
- 已新增真实 CLI seed `m3e-live-1` 的 DEFEND gameplay trace：ShadowDiff `Reliable/match=true/mismatch_count=0`，重复报告 SHA-256 完全一致；该证据只覆盖单一确定性动作，不能代替全量 M3e 矩阵。
- 同一 seed 的 `EndTurn` trace 暴露 Shrinker Beetle 的 `DebuffStrong` 语义缺口；已加入版本锁定的 `SHRINK`（30% 敌方伤害降低、3 回合、Power amount=-1/applier/dynamic vars）镜像，修复后 ShadowDiff `match=true/mismatch_count=0`，但因未知洗牌保持 `Estimated`。
- 同一 seed 的 `FIRE_POTION` 真实动作已通过严格差分（`Reliable/match=true/mismatch_count=0`），重复报告字节一致；非攻击 Intent 缺省 hits 已按 CLI 公共格式归一为 1。
- 新增 `m3e-live-smoke-manifest.json`：7 个真实动作报告、7 个重复报告、版本锁一致、`mismatch_count=0`；其中 3 个 Reliable、4 个 Estimated（EndTurn 未知洗牌、RandomEnemy 的 CombatTargets、多敌随机目标、Chaos 的 CombatOrbGeneration）。该 manifest 是小型 smoke 证据，不是全量 M3e 出口。
- ShadowDiff 增加 Intent 字段后已重新生成全部 6 份 canonical 报告及重复报告；新的 6/6 manifest 仍保持 `mismatch_count=0`、重复字节一致；新增三敌人 `SWORD_BOOMERANG` 的 CombatTargets 仍为 Estimated，不进入 Reliable。
- 复用 `p0-multi-enemy-targets-trace.jsonl` 完成 3 个多敌人动作（两个指定目标攻击、一个自选防御）ShadowDiff 验证：3/3 `Reliable/mismatch_count=0`，3/3 重复 SHA 一致；生成 `m3e-multi-target-smoke-manifest.json`，仍不代表随机目标或全量 M3e。
- 新增 Defect `CHAOS` 随机 Orb 实机 trace；补齐 ShadowDiff 的 `CHAOS → ChannelOrbs(RANDOM)` 映射并传入 `orb_slots`。当前报告 `mismatch_count=0/match=true/confidence=Estimated`，`CombatOrbGeneration` counter 由 0→1，重复 SHA `2C5F1A773730595A6B685ED61482299A0E770D8DE86C4A6521072D69C02A0DE6` 字节一致；该随机结果保持 Estimated，不晋级 Reliable。
- ShadowDiff 已增加可选 `history_counters` 比较；基于现有 Strike trace 注入版本锁定的公开计数断言，`attacks/cards/draw/exhaust/discard` 全部匹配，报告与重复报告 `match=true/mismatch_count=0`，SHA `99EE28C77219E780D03C52B9563FEB859E5A6CCA8FAB0C055D33E9BC4CDAA7C0` 一致。真实 CLI 尚未默认导出该块，因此旧报告不受影响。
- 复跑既有 `TRUE_GRIT` 随机消耗和 `SWORD_BOOMERANG` 随机目标 trace：两者 `match=true/mismatch_count=0`，但比较范围为 `aggregate_count_only`，因此保持 `Estimated`，不晋级 Reliable。

出口：

- 所有 Reliable action `mismatch_count=0`；
- 重复运行报告字节一致；
- catalog、runner、验证文档和 repeat manifest 数量完全一致；
- 未通过对象自动降级。

#### M3f：性能和资源

交付：

- COW/persistent piles 和 compact planner state；
- 128-bit fingerprint + full canonical collision audit；
- transition/turn-start/action cache；
- root/shard deterministic parallel；
- process-level memory budget、worker quota、CancellationToken。

出口：

- 正确性门禁不变；
- 单线程与并行 action values/policy/hash 字节一致；
- 报告 nodes/s、allocation、GC、peak RSS、cache hit；
- 100M/8GB 只作为硬上限，不作为完成证明。

### M4：1k NOSL Smoke（M3 通过后重新生成）

交付：

- 1,000 个 NOSL 标签；
- sidecar；
- 聚合报告；
- hidden-state invariance 报告；
- 真实 CLI 根动作差分报告。
- 完整 legal-action coverage；
- duplicate/dedup 报告；
- probability mass、policy tree 和 quality 分布报告。

出口：

- Reliable/Estimated/Uncalculable 分类正确；
- 语义门禁通过；
- 无 RNG/未来牌序泄漏；
- 当前诊断集 `Reliable=0` 不能直接升级，必须在 M3d/M3e 后重新生成并验收。

### M5：10k Pilot

交付：

- 分层状态分布；
- 三目标标签；
- hard negative；
- train/validation/test/challenge；
- 数据重建报告。

出口：

- 每个必需分层 Reliable 比例至少 95%；
- 所有版本和 shard hash 一致；
- 同一 public state/belief/schema/legal-action group 不跨 split 泄漏；
- `duplicate warning=0` 或所有重复组有明确、可重建的聚合记录。

当前状态：M5 的前置 source 管线已用 28 个真实 CLI fixture 验收，产物见 `data/m5-source-pilot-all28-*`；并已复用 direct-matrix、种子变体和 Silent 基础 fixture 扩展为 540 状态候选，产物见 `data/m5-source-expanded-v3-*`；另有 428 条 Reliable-only 实验候选 `data/m5-reliable-candidate-v2.jsonl`，其中严格 holdout-backed 子集为 15 条 `data/m5-reliable-holdout-backed.jsonl`。这些产物都不能替代 10k Pilot：v3 仍有 168 条 Uncalculable，Reliable-only 候选严重偏向 Ironclad（414/428），且 M3e holdout 仍不完整。生产 M5 仍需在扩展多敌人、低血量、复杂选择、随机目标/卡池、Power/Relic 组合和 hard-negative 后重新采集，并在每个必需语义分层完成 CLI↔Shadow holdout 后再扩展。

### M6：100k Pilot 扩展

交付：

- 100,000 个可重建状态；
- 分布可视化；
- 长尾语义覆盖；
- teacher 搜索耗时和内存报告；
- challenge set。

出口：

- 每分层 Reliable 比例至少 95%；
- 概率质量和标签重复运行一致；
- 复杂状态失败原因可追溯。

### M7：监督训练

训练内容：

1. 合法动作 mask；
2. soft policy 蒸馏；
3. top-k 排序；
4. 三个价值头；
5. 死亡风险；
6. 置信度和弃权。

Reliable 标签进入主损失，Estimated 只进入辅助损失。

### M8：运行时接入

运行规则：

```text
真实 CLI 生成合法 ActionCandidate
        │
        ▼
模型排序
        │
        ▼
限时 CombatSearchSession/Expectimax 验证
        │
        ├── 可靠模拟值优先
        ├── 未知语义自动弃权
        ├── 低置信度关闭学习剪枝
        └── 异常回退现有搜索
```

离线完整教师不直接部署到实时战斗。

## 9. 必须通过的测试

### NOSL 不变性

- 相同 public state + 不同 RNG state：标签完全一致；
- 相同牌组构成 + 不同未来牌序：标签完全一致；
- 实际 CLI 随机结果改变但公共观测不变：教师标签不随之改变。
- 任意 runtime object hash、隐藏 instance ID、hidden discard order 改变时 `NoslBeliefKey` 不变；
- `ExhaustPile`/removed cards 不能回到 draw-eligible counts；
- `ObservationView.Teacher` 不能作为 NOSL evaluator 输入。

### 概率正确性

- 分支概率总和为 1；
- 不放回抽牌概率正确；
- 随机池权重正确；
- 机会节点后重新求 Max；
- 不允许未知概率伪装成均匀分布；
- A,A,B 多重集合概率为 2/3、1/3；
- 等价 post-state 概率合并后总质量为 1；
- sampled/unknown 分支显式降级并带 covered mass/ESS/CI。

### 穷举正确性

- 小场景与独立暴力枚举一致；
- 完整模式与高预算模式一致；
- 达到硬上限后为 `Uncalculable`；
- 多次运行 action values、top-k、policy tree 和聚合 hash 一致；
- chance→Max 反例禁止 clairvoyance；
- 死亡/全灭终点禁止继续出牌和推进回合。

### 真实引擎验证

- 根动作 CLI 回放；
- ShadowDiff；
- HP、Block、Energy、牌堆、Power、Relic、计数器；
- RNG 消耗计数；
- 版本 metadata；
- public/teacher 隔离；
- original 与 runtime-patched assembly hash/patch profile 可重建。

### 数据门禁

- schema 解析率 100%；
- stable ID 缺失为 0；
- public leakage 为 0；
- 版本混杂拒绝；
- train/validation/test 无 public-state group 泄漏；
- Reliable 标签全部满足完整概率和语义条件。
- 每个合法 root ActionCandidate 都有 value/mask/quality 或明确 null/reason；
- `teacher_snapshot` 不进入主 JSONL/Parquet；
- duplicate warning 已解决或有 manifest 聚合依据。

## 10. 硬件与性能

当前硬件保持：

```text
CPU：9950X3D
RAM：32GB
GPU：12GB
```

离线教师：

- 按 root/shard 并行；
- 每个 worker 独立缓存；
- 不共享可变 RNG；
- 内存超过 80% 时减少 worker；
- 单状态 8GB 硬上限；
- GPU 不用于首版不规则树搜索。

GPU 主要用于：

- PyTorch 监督训练；
- 批量推理；
- ONNX 对照。

只有出现长时间超过 80–85% RAM、swap 持续增长或 shard 无法流式处理时，才升级到 64GB。

## 11. 计划完成定义

首版 NOSL 教师完成必须同时满足：

1. `NoslBeliefState` 不包含具体 RNG/未来牌序；
2. 离线 evaluator 已真正接入 `TeacherWorker`；
3. M3a-M3e 的状态、概率、策略树、时序和 CLI 差分门禁全部通过；
4. 完整概率分支可精确穷举的小场景全部通过；
5. 未知概率和未验证语义不会进入 Reliable；
6. 相同公共状态对隐藏 RNG/牌序具有标签不变性；
7. 1k、10k、100k 数据均可重建，且 duplicate/split 规则可复现；
8. 每个必需分层 Reliable 比例至少 95%；
9. 真实 CLI 根动作与影子模拟器差分通过；
10. 三目标、top-k、soft policy、contingent policy tree 和风险标签可追溯；
11. 模型训练数据不包含 raw RNG、未来牌序或 teacher-only state；
12. 运行时仍保留限时 Expectimax 和安全回退；
13. 版本不匹配、语义未知、资源超限和 evaluator 异常均自动降级；
14. 单线程/并行生成的标签和 manifest 字节一致。

## 12. 明确排除

- 不把 `reference/CombatSolver` 源码、RandomForeseer 代码、程序集或依赖并入本项目；
- 可独立重实现其 root snapshot、fork/COW、coverage、fingerprint 和 strict-diff 架构思想，并将其仅作为隔离 oracle；
- `reference/slay-the-spire-2-emulator` 可作为 MIT 项目的隔离参考。若复制其仓库自有代码，必须保留 MIT copyright/permission notice，并记录源 commit、文件路径和改动；其 `decompiled/` 内容来自游戏程序集，不能仅凭仓库根 LICENSE 视为可直接再发布代码，默认按语义参考处理；
- 不直接移植该项目的 `DotNetRandom`/`GameRng`、NativeAOT ABI、固定整数 observation 或已知 seed 逻辑；本项目继续使用已验证的 v0.111 xoshiro RNG、NOSL belief 和自身 schema；
- 不使用 RandomForeseer 运行时；
- 不使用本局具体 RNG 生成 NOSL 标签；
- 不使用真实未来抽牌顺序；
- 不把实际随机结果作为教师已知条件；
- 不将完整 teacher sidecar 直接输入学生模型；
- 不把未验证对象标为 Reliable；
- 不将离线完整穷举直接用于实时决策；
- 不在当前阶段扩展地图、路线、事件、商店和完整牌局策略。

## 13. 文档产物

本计划的配套文档：

- `D:/STS2BestChoice/STS2SuperModel/PLAN.md`（总计划）；
- `D:/STS2BestChoice/STS2SuperModel/NOSL_EXPECTIMAX_TEACHER.md`；
- `D:/STS2BestChoice/STS2SuperModel/SHADOW_SIMULATOR_OPTIMIZATION_RECOMMENDATIONS.md`；
- `D:/STS2BestChoice/STS2SuperModel/docs/research/headless-simulator-research-agent.md`；
- `P0_VERIFICATION.md`、P1 语义覆盖文档和 DatasetManifest 说明。

当前状态记录必须保持为：M0-M2 的基础结构/桥接存在，但 M3 correctness closeout 尚未通过；现有 heuristic、sampled、budget-bound 或未验证语义不得作为 Reliable NOSL 主教师标签。

### 2026-08-31 增量记录（卡牌语义收口）

- `COLOSSUS` 已完成真实 CLI→ShadowDiff 语义对齐：`COLOSSUS_POWER`、`DamageDecrease=0.5`、`Damage/TurnEnd` 触发阶段均已镜像。
- 报告 `data/p1-csharp-card-direct-colossus-diff-report.json`：`Reliable`、`match=true`、`mismatch_count=0`；连续两次运行 SHA-256 一致（`F3F37A1E4E74A04069170D07F849B6DB27861063A8DBDBF5C44A5683F6452182`）。
- 14 张已修复卡牌定向回归全部 `match=true` 且 `mismatch_count=0`；THRASH 因隐藏随机消耗保持 `Estimated`，其余为 `Reliable`。
- `CLOAK_AND_DAGGER` 生成小刀语义已补齐（运行时使用 `stats.cards` 字段），报告更新为 `Reliable / match=true / mismatch_count=0`；直接矩阵覆盖版本推进至 v16（aligned mismatch 85）。
- 同一运行时字段缺口已扩展修复 `BLADE_DANCE`、`UP_MY_SLEEVE`；三张卡牌报告均为 `Reliable / match=true / mismatch_count=0`，直接矩阵覆盖推进至 v17（aligned Reliable 101、mismatch 83）。
- 新增 `calculateddamage` 回退，修复 `ASHEN_STRIKE`、`BULLY`、`MEMENTO_MORI`、`PERFECTED_STRIKE`、`PRECISE_CUT`；22 张定向回归全绿，覆盖推进至 v18（aligned Reliable 106、mismatch 78）。
- 统一读取 `stats.repeat`，并对描述中的 `twice` 使用 2 次命中；修复 `CONFLAGRATION`、`DAGGER_SPRAY`、`TWIN_STRIKE`，覆盖推进至 v19（aligned Reliable 109、mismatch 75）。
- 修正 `DRUM_OF_BATTLE` 的 `energy` 字段为耗尽触发效果，避免出牌时立即加能量；报告为 `Estimated / match=true / mismatch_count=0`，覆盖推进至 v20（mismatch 74）。
- `PACTS_END` 现在按公开 `exhaust_pile_count >= stats.cards` 条件决定是否造成伤害；当前 fixture 条件不满足，报告为 `Estimated / match=true / mismatch_count=0`，覆盖推进至 v21（mismatch 73）。
- `FINISHER`、`FLECHETTES` 读取运行时 `calculatedhits`；当当前计数为 0 时不生成攻击，报告均为 `Estimated / match=true / mismatch_count=0`，覆盖推进至 v22（mismatch 71）。
- 增加 `calculatedblock` 回退，修复 `EXPECT_A_FIGHT` 的运行时格挡预览；报告为 `Reliable / match=true / mismatch_count=0`，覆盖推进至 v23（aligned Reliable 110、mismatch 70）。
- `ESCAPE_PLAN` 不再把条件格挡作为必然格挡；当前未观测到 Skill 抽牌时保持 `Estimated / match=true / mismatch_count=0`，覆盖推进至 v24（mismatch 69）。
- `PYRE` 的 `stats.energy` 已从“立即加能量”改为永久 `TURN_START_ENERGY`，并同步为 `PYRE_POWER`；报告为 `Reliable / match=true / mismatch_count=0`，覆盖推进至 v25（mismatch 68）。
- `ANTICIPATE` 现在同时生成本回合 `DEXTERITY` 与 `ANTICIPATE_POWER` 临时标记；报告为 `Reliable / match=true / mismatch_count=0`，覆盖推进至 v26（mismatch 67）。
- 补齐 `POUNCE` 的 `FREE_SKILL_POWER`、`UNRELENTING` 的 `FREE_ATTACK_POWER`、`PREDATOR` 的 `DRAW_CARDS_NEXT_TURN_POWER` 与 `STRANGLE` 的敌方触发 Power；四份报告均为 `Reliable / match=true / mismatch_count=0`，覆盖推进至 v27（mismatch 63）。
- 补齐 `JUGGLING`、`UNMOVABLE`、`SPEEDSTER`、`JUGGERNAUT` 的内部状态、Live Power ID 和触发阶段；四份报告均为 `Reliable / match=true / mismatch_count=0`，覆盖推进至 v28（mismatch 59）。
- `NOXIOUS_FUMES` 已映射为永久 `TURN_START_ALL_ENEMY_POISON` 与 `NOXIOUS_FUMES_POWER`；报告为 `Reliable / match=true / mismatch_count=0`，覆盖推进至 v29（mismatch 58）。
- `CRUELTY` 已映射为玩家侧易伤目标攻击增伤状态与 `CRUELTY_POWER`，伤害公式从玩家 Power 读取百分比；报告为 `Reliable / match=true / mismatch_count=0`，覆盖推进至 v30（mismatch 57）。
- `VICIOUS` 已映射为施加易伤时抽牌，`STAMPEDE` 已映射为回合结束随机自动打出手牌攻击；两份报告均为 `Reliable / match=true / mismatch_count=0`，覆盖推进至 v31（mismatch 55）。
- `BUBBLE_BUBBLE` 不再把条件中毒作为无条件中毒；当前目标没有 Poison 时报告为 `Estimated / match=true / mismatch_count=0`，覆盖推进至 v32（mismatch 54）。
- `TANK` 已补齐 `TANK_POWER`、DynamicVars、敌人伤害 1.5 倍结算及公开 Intent 刷新；报告为 `Reliable / match=true / mismatch_count=0`，覆盖推进至 v33（mismatch 53）。
- `INFERNO` 已补齐 `INFERNO_POWER`、`SelfDamage=1`、回合开始失血及玩家失血触发全体伤害；报告为 `Reliable / match=true / mismatch_count=0`，覆盖推进至 v34（mismatch 52）。
- `MANGLE` 已从原先错误的全体 `PIERCING_WAIL` 路径拆分为单目标临时 Strength -10 与 `MANGLE_POWER`；报告为 `Reliable / match=true / mismatch_count=0`，覆盖推进至 v35（mismatch 51）。
- `CRIMSON_MANTLE` 已补齐 `CRIMSON_MANTLE_POWER`、`SelfDamage=1`、回合开始失血及格挡结算；报告为 `Reliable / match=true / mismatch_count=0`，覆盖推进至 v36（mismatch 50）。
- `BOUNCING_FLASK` 已改用 `RandomEnemyStatus` 重复施毒；单一存活目标时仍镜像每次 `CombatTargets` 消耗并同步 `POISON_POWER`，报告为 `Estimated / match=true / mismatch_count=0`，覆盖推进至 v37（mismatch 49）。
- 随机攻击的单目标路径现在仍镜像每次 `CombatTargets` 消耗；`SWORD_BOOMERANG`、`RICOCHET` 均为 `Estimated / match=true / mismatch_count=0`，覆盖推进至 v38（mismatch 47）。
- `DOMINATE` 现在先施加 Vulnerable，再用既有 `AmountByTargetVulnerableStacks` 按目标当前易伤层数获得 Strength；报告为 `Reliable / match=true / mismatch_count=0`，覆盖推进至 v39（mismatch 46）。
- `SECOND_WIND` 已接入 `ExhaustNonAttacksAndBlock`，`FIEND_FIRE` 已接入 `ExhaustHand` 后按实际耗尽数量重复伤害；两份报告均为 `Reliable / match=true / mismatch_count=0`，覆盖推进至 v40（mismatch 44）。
- `CASCADE` 已识别为 X 费卡并正确消耗当前全部能量；当前空牌堆 fixture 为 `Estimated / match=true / mismatch_count=0`，覆盖推进至 v41（mismatch 43），非空牌堆自动出牌分支仍待验证。
- 本次仅更新卡牌直接证据，不改变 M0-M2 契约；M3e 全量 holdout 仍为 partial，未将未验证语义晋级为 Reliable 主训练标签。

### 2026-08-31 增量记录（v42：直接矩阵一致性收口）

- `HELLRAISER`、`ONE_TWO_PUNCH`、`PHANTOM_BLADES`、`WELL_LAID_PLANS` 已补齐出牌后结构化 Power；触发阶段与当前已实现的自动出牌、攻击重放、Shiv 保留/首次增伤路径同步。
- 上述四张牌的直接安装报告均为 `Estimated / match=true / mismatch_count=0`；在正向触发或 Choice fixture 完成以前不会晋级 Reliable。
- `CALCULATED_GAMBLE` 已接入“弃掉手牌后抽取相同数量”及洗牌路径，直接报告为 `Uncalculable / match=true / mismatch_count=0`，不会把未知洗牌结果伪装成 Exact。
- `FLANKING` 已镜像敌方 `FLANKING_POWER`、`Applier=0`、`Damage/TurnEnd`；由于它依赖“其他玩家”语义，保持 `Estimated / match=true / mismatch_count=0`。
- `STOKE` 已镜像耗尽手牌、按实际耗尽数生成随机牌及未知 RNG counter 消耗，直接报告为 `Estimated / match=true / mismatch_count=0`。
- `INFERNAL_BLADE` 已补齐“生成一张随机攻击并令其本回合免费”的公开计数行为；目前只剩 `CombatCardGeneration.counter` 一处差分。真实引擎对 33 张可生成攻击执行 `TakeRandom`/全池 Fisher–Yates，消耗 32 次 RNG；在版本锁定的完整候选池进入 `RandomOperatorRegistry` 前继续保持 `Estimated`。
- 新增 `training/sync_card_direct_matrix_results.py`：只在 fixture 与 normalized action ID 同时一致时，用当前报告同步矩阵。同步结果为 607 行、140 行匹配证据、110 行 direct Reliable、1 行 mismatch、461 行 timeout、5 行 runtime error。
- 新增 `data/m3e-card-direct-holdout-coverage-v42.json` 与 `data/m3e-card-persistent-power-repeat-v42.json`；本批 8 份报告双跑字节一致。全局 repeat-verified direct Reliable 当前仍为 64/110，因此 M3e 保持 `partial`。

### 2026-08-31 增量记录（v43：Infernal Blade RNG 收口）

- 根据 v0.111 `CardFactory.GetDistinctForCombat`、`IEnumerableExtensions.TakeRandom` 与 `ListExtensions.UnstableShuffle`，`INFERNAL_BLADE` 会先过滤 Ironclad 攻击池，再对完整候选池执行 Fisher–Yates，最后取 1 张。
- 版本锁定候选池为 33 张；排除 Starter `STRIKE_IRONCLAD`/`BASH`、Ancient `BREAK`、`CanBeGeneratedInCombat=false` 的 `FEED` 及没有当前 runtime model 的 `GRAPPLE`。
- `GenerateRandomCards` 的无放回已改为完整候选池 shuffle；masked RNG 代表路径也按 `pool_count - 1` 镜像 counter，而不是错误地按生成数量计数。有放回生成仍按每张一次 RNG 消耗处理。
- `INFERNAL_BLADE` 报告现为 `Estimated / match=true / mismatch_count=0`，`CombatCardGeneration` 的引擎与影子计数均为 `0 -> 32`；双跑 SHA-256 完全一致。
- 当前 607 行直接矩阵中，141 行可执行证据全部 `mismatch_count=0`；其中 direct Reliable 110、Estimated 28、Uncalculable 2、非 play matched 1。仍有 461 行 timeout/non-combat witness 与 5 行 runtime error。
- 全局当前 repeat hash 覆盖仍只有 64/110 个 direct Reliable，因此 v43 仍为 `partial`，不得据此启动正式 M5 训练。

### 2026-08-31 增量记录（v44：Reliable 双跑与非执行行分类）

- 对当时 110 个 direct Reliable 报告执行完整双跑，110/110 字节一致；该门禁同时暴露 `DISMANTLE` 和 `SPITE` 的旧报告已过期。
- `DISMANTLE` 不再把“目标有易伤时额外命中”当作无条件二连击；`SPITE` 不再把“本回合失去过生命时额外命中”当作无条件重复。两张牌当前直接 fixture 均为 `Estimated / match=true / mismatch_count=0`，等待条件成立的正向 fixture 后再评估 Reliable。
- 修正后 direct Reliable 为 108，108/108 均拥有当前二次运行的相同 SHA-256；所有 141 个可执行直接报告继续保持 `mismatch_count=0`。
- 原先笼统记录的 461 个 `runtime_timeout` 已按真实终态拆分：358 个 fixture 没有进入战斗而停在 `card_reward`，23 个需要稳定 Choice fixture，80 个 runtime 不可用；真正的 ShadowDiff combat timeout 为 0，另有 5 个 runtime error。
- 新增 `data/m3e-card-direct-holdout-coverage-v44.json` 与 `data/m3e-card-direct-reliable-repeat-v43.json`。当前阻塞不再是已执行报告差分或 Reliable 重复性，而是缺少正确角色/上下文 fixture、Choice 契约和不可用模型终态分类。

### 2026-08-31 增量记录（v45：按所属角色隔离重采集）

- 新增 `training/recapture_card_direct_contexts.py`：从 v0.111 卡牌 catalog 读取所属角色，每张牌启动独立 CLI 进程，清空起始遗物，并写入 `card-direct-traces4`，避免旧批次复用进程后停留在 `card_reward` 的污染。
- 对可映射到 Ironclad/Silent/Defect/Regent/Necrobinder/Colorless 的 321 张旧 `fixture_not_entered_combat` 卡牌完成重采集；139 张直接零差异，182 张暴露出真实语义差分。
- `sync_card_direct_matrix_results.py` 现在在报告 action 对齐时同步 `decision=combat_play`，`build_card_direct_witness_report.py` 优先读取 `card-direct-traces4`，因此新证据不会再被旧 trace3 或旧 decision 掩盖。
- 当前真实矩阵为：607 行；已执行 462 行（76.11%）；match 280；mismatch 182；direct Reliable 193，其中当前 repeat-verified 108；Estimated 81；直接 Uncalculable 5。
- 未执行终态为：37 个仍未进入战斗的非标准上下文对象、23 个 Choice fixture、80 个 runtime unavailable、5 个 runtime error；真正的 ShadowDiff timeout 为 0。
- v44 的“已执行行全部零差分”仅作为重采集前历史检查点；v45 的 182 个新差分是当前权威状态。必须按差分字段聚类修复，不得回退旧 trace 或把新报告降格隐藏。

### 2026-08-31 增量记录（v46：首轮字段聚类收口）

- 对 182 个真实差分按字段聚类处理，而不是逐卡盲修：通用能力安装关闭 48 张；带精确 CLI Power 证据的状态安装关闭 32 张；延迟能量/X 费关闭 13 张；Focus、双状态、条件状态首批关闭 10 张。
- Power 卡只有在没有任何结构化 `ApplyStatus` 时才使用同名 Power 安装；该路径始终增加 `generic_power_trigger_fixture_pending`，因此不会因一次安装报告就伪升 Reliable。
- 补齐 `INFERNAL_BLADE` 无放回全池 shuffle、延迟能量 `ENERGY_NEXT_TURN_POWER`、延迟召唤、Retain/Star、Focus 临时状态和多种已观测 Power ID/TriggerPhase；所有未完成触发器继续保持 Estimated。
- 当前权威矩阵：607 行，已执行 462；match 383；mismatch 79（130 个字段）；direct Reliable 196，其中 repeat-verified 108；Estimated 178；直接 Uncalculable 8。
- 相比 v45，真实 mismatch 从 182 降到 79。剩余差分主要集中在手牌/牌堆移动、Shuffle/随机生成 RNG、临时 Strength 恢复与 Intent 刷新、随机伤害、终止回合/终局效果。
- 新增 `data/m3e-card-direct-holdout-coverage-v46.json`；M3e 和生产 M4/M5 继续保持 `partial/blocked by correctness gates`。

### 2026-08-31 增量记录（v47：确定性生成与抽牌堆插入）

- 补齐自我复制到弃牌堆、Inky Shiv、Dazed/Wound/Slimed/Burn/Void、Debris 填手牌以及 Storm of Steel 弃牌后生成 Shiv 的确定性语义。
- `GenerateCards` 现在支持 `AmountByEnergySpent` 和 `RandomizeGeneratedPosition`；向抽牌堆插入生成牌时，已知 Shuffle 精确选择位置，masked Shuffle 只镜像每张一次 counter 并保持降级质量。
- 补齐 Soul 向抽牌堆/手牌/弃牌堆生成，`SHINING_STRIKE` 回抽牌堆顶，以及 unknown `REBOOT` Fisher–Yates 的 `pile_count-1` counter 镜像。
- 相关定向测试 `RebootMovesAllNonExhaustedCardsBeforeDrawingAndExhaustsItself`、`MetamorphosisGeneratesWithReplacementAtRandomDrawPositionsForTheCombat` 2/2 通过。
- 当前权威矩阵：match 398、mismatch 64（105 字段）、direct Reliable 204、repeat-verified 108、Estimated 184、直接 Uncalculable 9。相较 v45 已从 182 降到 64。
- 新增 `data/m3e-card-direct-holdout-coverage-v47.json`；下一批优先处理随机生成卡池/RNG、Forge/手牌变化、临时 Strength 恢复与 Intent、随机伤害和终止回合效果。

### 2026-08-31 增量记录（v48：随机池计数与伤害重复）

- 根据真实 CLI 的 `TakeRandom` counter，补齐六类随机生成代表池：Bundle of Joy=50、Distraction=39、Jack of All Trades=49、Manifest Authority=50、White Noise=19；Jackpot 使用有放回三次生成。代表分支只用于 aggregate ShadowDiff，并保持 Estimated，不作为实际训练候选身份。
- `BuildCard` 接入公开 `stars`，Stardust 不再误用 Energy X；RIP_AND_TEAR 的描述二连击现在镜像两次 `CombatTargets`。
- 修正说明文本中的 `twice` 被误判为攻击重复：Debilitate、Shatter、Tesla Coil 均只执行一次基础攻击；Ice Lance 的 `repeat` 属于 Frost 数量，不再重复伤害。
- Protector/Squeeze/Unleash 的 `calculateddamage` 属于 Osty companion，当前无 Osty 的 fixture 不再错误当作玩家攻击；Reboot 现正式接入 Reboot+Draw 路径并匹配 Shuffle counter。
- 当前矩阵：match 414、mismatch 48、direct Reliable 207、repeat-verified 108、Estimated 196、直接 Uncalculable 10。真实差分已从 v45 的 182 降到 48。
- 新增 `data/m3e-card-direct-holdout-coverage-v48.json`；下一批集中处理 Forge/手牌变换、临时 Strength/Intent、Power DynamicVars/Amount、终止回合和药水/随机目标边界。

### 2026-08-31 增量记录（v49：已执行卡牌语义零差异收口）

- 462 个已执行卡牌 witness 已全部达到 `mismatch_count=0`；v45 暴露的 182 个真实语义差分已按字段聚类全部关闭，没有通过降级或回退旧 trace 隐藏差分。
- 补齐 Forge/Sovereign Blade、Shiv 生成实例 ID、临时 Strength 与公开 Intent 刷新、Power Amount/DynamicVars、随机 Orb/药水计数、X 费随机目标、Void Form 结束回合，以及 Alchemize 的 NOSL aggregate 计数。
- 当前直接证据为：Reliable 223、Estimated 235、Uncalculable 15；223 个 direct Reliable 已全部双跑，`different_count=0`，字节级一致。
- 新增 `training/verify_card_direct_reliable_repeat.py`、`data/m3e-card-direct-reliable-repeat-v49.json` 和 `data/m3e-card-direct-holdout-coverage-v49.json`。
- M3e 仍为 `partial`：剩余 134 个非执行终态由 39 个未进入战斗 fixture、14 个 Choice fixture 和 81 个 runtime unavailable 组成；旧 ShadowDiff 异常报告已重建，runtime error 归零。这些对象不会进入 Reliable 主训练标签。

### 2026-09-01 增量记录（v50：Choice 与特殊上下文首批收口）

- 新增 `--resolve-choice` 采集模式：CLI 在 `card_select` 后读取公开 `min_select/max_select`，发送稳定 `select_cards` 动作；ShadowDiff 将选择动作与根 `play_card` 组合比较，不把选择后的公开状态误判为缺失。
- 已将 9 个 Choice fixture 的零差异报告纳入 trace4（其中 3 个为 Reliable），并补采 3 个特殊上下文对象（LEAP、SCRAPE、SECRET_WEAPON）；当前矩阵提升至 474 个 match、0 mismatch、223 个 direct Reliable，223/223 双跑通过。
- 非执行终态从 146 降至 134：未进入战斗 39、Choice fixture 14、runtime unavailable 81；其余对象仍不进入 Reliable 主训练标签。
- 当前权威收口文件为 `data/m3e-card-direct-holdout-coverage-v50.json`；v49 保留为历史批次记录。

### 2026-09-01 增量记录（v51：资源不足卡牌定向回采）

- CLI 新增 fixture-only 命令 `set_combat_resources`。命令只能在 `enter_room` 成功进入战斗后调用，当前支持直接覆盖 `Energy` 与 `Stars`；v0.111 的 `MaxEnergy` 为只读计算属性，若请求覆盖会返回 `unsupported`，不会伪造该字段。
- `training/recapture_card_direct_contexts.py` 新增 `--combat-resources`，仅为 `runtime_unavailable` 回采路径在出牌前设置 `energy=10, stars=10`，普通 fixture 默认流程不变。
- 对 45 个可映射 `runtime_unavailable` 卡牌执行定向回采：14 个零差异 match（其中 12 个 Reliable、2 个 Estimated）、8 个真实 mismatch、21 个仍因角色/伙伴/引擎上下文不可执行、2 个 Shadow 输入异常。只有 14 个 match 的 trace/report 已复制到权威 `data/card-direct-traces4` 与 `data/p1-csharp-card-direct-*-diff-report.json`。
- 当前 direct matrix：607 行，488 个已对齐报告；235 个 direct Reliable、237 个 Estimated、15 个直接 Uncalculable；39 个需非标准战斗上下文、14 个需 Choice fixture、67 个仍 runtime unavailable；mismatch=0，runtime_error=0。235/235 direct Reliable 双跑 `different_count=0`。
- 资源回采没有关闭 M3e：伙伴/多敌人/特殊事件卡仍需专用 fixture；未匹配报告保持 Estimated/Uncalculable，不得进入 Reliable NOSL 主训练集。M4/M5 生产数据门禁仍保持开放。

### 2026-09-02 增量记录（v52：标准角色 Choice 收口）

- 修正 ShadowDiff 的 Choice 解析：选择效果现在读取根出牌而非被选择卡牌的描述；只要 CLI 返回稳定 `select_cards` ID 就建立显式 `ChoiceSpec`，卡牌迁移继续由根卡牌语义驱动。
- 补齐 13 个 Choice 的行为映射：手牌丢弃/消耗、放回抽牌堆顶、Minion 变换、Nightmare 延迟复制、Scavenge 延迟能量、Transfigure Replay/费用，以及 Discovery/Splash 随机生成聚合计数。
- 13/13 现有真实 CLI Choice trace 均达到 `mismatch_count=0`：11 个 Reliable，Discovery/Splash 因隐藏随机生成保持 Estimated。Minion 变换实例 ID 改为引擎实际格式 `card:MINION_*:NNN`，定向 Core 测试通过。
- 当前矩阵为 607 行、501 个 match、0 mismatch；direct Reliable=246、Estimated=239、直接 Uncalculable=15；246/246 Reliable 双跑字节一致。Choice 阻塞由 14 降到 1。
- 唯一剩余 Choice 是 `ABUNDANCE`：catalog 明确标记为 multiplayer-only 事件卡，现有 trace 停留在 card_reward/card_select 且没有 public combat pre/post state，因此保持 Uncalculable，不进入 Reliable。
- M3e 仍为 partial：39 个特殊上下文、67 个 runtime unavailable 和上述 1 个 multiplayer-only Choice 仍需显式终态或专用 fixture；正式 M4/M5 不因本批自动放行。

### 2026-09-02 增量记录（v53：事件/衍生/状态卡直接证据）

- `recapture_card_direct_contexts.py` 允许事件、衍生、状态和诅咒卡使用 Ironclad 作为无语义改写的 CLI 战斗宿主；39 个旧 `fixture_not_entered_combat` 对象全部重新进入真实战斗。
- 39 个对象中 38 个获得零差异证据：22 Reliable、16 Estimated。首轮 29 个直接匹配；其余通过真实 CLI 差分补齐 Intangible、MaxHP 下降、临时 Strength/Monologue、Metamorphosis 随机生成与随机插入计数、Relax 延迟资源、Toric Toughness、Stun Intent 等语义后匹配。
- 新增稳定 Minion 变换 ID `card:MINION_*:NNN`，随机生成到抽牌堆时在 masked Shuffle 下逐张镜像位置 RNG counter；新增 `EffectKind.StunEnemy`，只改变公开 Intent，不虚构敌人 Power。
- 当前权威矩阵：607 行、539 match、0 mismatch；direct Reliable=268、Estimated=255、直接 Uncalculable=15；268/268 Reliable 双跑字节一致。`fixture_not_entered_combat` 从 39 降为 1。
- 剩余 `GRAND_FINALE` 已在真实 CLI 中成功终结战斗，但 direct-card 严格 runner 不接纳终局 `card_reward` post-state；尝试直接放宽导致该诊断路径长时间运行，已撤回改动并保持 Uncalculable。后续应建立独立 terminal-summary runner，而不是降低 strict-public-state 门禁。
- 非执行终态现为 69：67 runtime unavailable、1 multiplayer-only `ABUNDANCE` Choice、1 terminal `GRAND_FINALE`。M3e 全语义分层和敌人闭环仍未关闭。

### 2026-09-02 增量记录（v54：资源卡确定性语义）

- 复用 v53 高资源真实 CLI trace，关闭 6 个确定性差异：Banshee's Cry 不再把费用降低预览误当作获得能量；Guiding Star 安装下回合抽牌；Particle Wall 返回手牌；Reflect 安装正确 Power；Dying Star 使用正确临时降力量来源 Power；Resonance 分离玩家增力与全敌降力。
- 6/6 报告为 `Reliable/mismatch_count=0`，并纳入 trace4。当前矩阵为 545 match、0 mismatch、274 direct Reliable；274/274 双跑字节一致。
- runtime unavailable 从 67 降到 61；所有未验证对象继续保持显式 Uncalculable/终态，不因相邻卡牌语义相似而晋级。
- 当前卡牌非执行终态为 63：61 runtime unavailable、ABUNDANCE multiplayer-only Choice、GRAND_FINALE terminal runner 缺口。M3e 仍为 partial。

### 2026-09-02 增量记录（v55：终局摘要与随机选择边界）

- 新增 ShadowDiff `--allow-terminal` 诊断模式。终局 `card_reward` 只比较 terminal summary，并强制 `confidence=Estimated`，不会进入 direct Reliable；`ReadActualPowers` 对终局缺少 `enemies` 字段时安全退出。
- `BURY`、`SEVEN_STARS`、`GRAND_FINALE` 均获得零差异 terminal-summary 证据；`QUASAR` 补齐 3 选 1 随机生成的 aggregate count 与 `CombatCardGeneration` 消耗，保持 Estimated。
- 当前权威矩阵：607 行、548 match、0 mismatch；direct Reliable=274、Estimated=259、直接 Uncalculable=15；274/274 Reliable 双跑字节一致。`fixture_not_entered_combat=0`，`choice_fixture_required=1`，`runtime_unavailable=58`。
- 非执行终态现为 59：58 runtime unavailable、1 个 multiplayer-only `ABUNDANCE` Choice。多人专属、不可打出状态/任务卡以及运行时缺模型对象继续保持显式 Uncalculable；M3e 全语义分层仍为 partial。

### 2026-09-02 增量记录（v56：M3c 敌人公开目标契约）

- `CreatureState` 新增公开目标字段：`is_hittable`、Primary/Secondary、Minion 和 `target_restrictions`；CLI 从真实运行时导出这些字段，ShadowDiff 解析并在字段存在时纳入严格比较。
- 玩家动作生成和影子目标展开都排除 `is_hittable=false` 的敌人；目标规则进入 `ExactKey`、`CycleKey` 与 `NoslBeliefKey`，防止搜索合并可选目标集合不同的状态。
- 新增 `EnemyAiBelief`：当前公开 Intent 与未来预测分开；未来分支沿用显式概率、质量和限制原因，不把隐藏 MonsterAi/RNG 暴露给学生。`BattleSearchSession` 已通过该 belief 消费未来 Intent。
- 修正未来 Intent 概率质量：`ProbabilisticEnemyIntentForecastProvider` 不再把输入概率自动归一化；质量小于 100% 时保留原始质量并降级 Estimated，超过 100% 时标记 Uncalculable，零质量显式记录不可计算原因。belief signature 同时包含未来敌人状态、目标规则和限制原因。
- 真实 CLI `DEFEND_IRONCLAD` 单敌 smoke 及 `p0-multi-enemy-targets` 三敌 3 个动作的目标字段全部对齐；4 份报告均为 `strict_public_state / Reliable / mismatch_count=0`，三敌报告各比较 12 个目标身份字段。证据见 `data/m3c-enemy-target-contract-v1.json`。该 smoke 只关闭公开字段管线，不代表非默认目标限制、召唤/复活/多阶段和未来 AI 状态机已完成。
- direct card 权威矩阵和 274 份 Reliable 双跑未改变，因此本批不重复执行全量卡牌双跑。M3c/M3e 仍为 partial，M5 生产门禁保持关闭。

### 2026-09-02 增量记录（v57：M3c 敌人公开历史与阶段）

- 新增 `EnemyAiPublicState`，只记录从玩家公开观测重建的信息：首次/最近观测回合、已观测回合数、`initial/spawned/active` 阶段，以及每回合一个 `IntentType:Hits` 签名。该结构不包含内部 move ID、状态机 StateLog 或 MonsterAi RNG。
- CLI 与实机 `LiveCombatSnapshotAdapter` 使用相同的公开历史规则；跨战斗会清空历史，同一回合重复抓取不会重复计数。`CreatureState`、`NoslBeliefState`、Public/NOSL/Search/Audit/Cycle Key 和 `EnemyAiBelief` signature 均纳入公开历史。
- 影子回合推进会追加新一回合公开 Intent，未来 Intent 分支落定后会替换当前回合的预测签名；缺少经过验证的未来状态机时仍保持 Estimated，不因历史字段匹配而晋级 Reliable。
- 三敌直接动作 3/3 报告为 `Reliable/mismatch_count=0`，每份比较 15 个公开 AI 字段；单敌 EndTurn 报告为 `Estimated/mismatch_count=0`，历史从 `Attack:1` 推进为 `Attack:1, Attack:4`。证据见 `data/m3c-enemy-public-history-v1.json`。
- 本批关闭“公开历史/阶段/可观测计数器”的状态契约，但没有读取隐藏状态机；召唤、复活、多阶段和各敌人未来概率模板仍是 M3c 剩余工作，M5 生产门禁继续关闭。





