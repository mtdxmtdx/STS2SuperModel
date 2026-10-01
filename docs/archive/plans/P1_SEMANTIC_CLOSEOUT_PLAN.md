 # P1 语义覆盖收口计划

 更新时间：2026-08-28
 用途：交给其他 agent 执行 P1 语义覆盖的剩余收口工作
 范围：STS2 v0.111.0 单人战斗语义、真实 CLI 与影子模拟器差分

 本计划只处理 P1 语义覆盖和验证。P0 教师数据闭环已经完成，不要重复实现
 Expectimax 分层采样、TeacherWorker、100/1,000 Smoke、DatasetManifest 或
 public/teacher 基础契约。

 ## 1. 收口目标

 把当前仍处于 declared、structured-only 或未行为验证状态的战斗语义，
 按批次转换为有证据的 simulator_supported、LiveObserved、Reliable。

 收口对象分为：

 1. Power；
 2. 战斗遗物；
 3. 卡牌语义和模拟器 handler；
 4. 真实 CLI 与影子模拟器差分验证。

 Reliable 必须同时满足：

 - 真实 CLI fixture 已执行；
 - 影子模拟器执行同一动作；
 - 关键状态 mismatch_count=0；
 - 版本 metadata 完整；
 - 第二次运行报告 SHA-256 与第一次一致；
 - public/teacher 无泄漏；
 - 未知效果没有被静默当成无效果。

 ## 2. 当前基线

 开始前重新生成基线，不直接使用旧交接文档中的数字。

 当前已知基线：

 - Power 总数：283；
 - Power 已行为探针：20；
 - Power 仍为 simulator_declared：53；
 - 战斗遗物总数：299；
 - 遗物已探针：20；
 - 已知但未支持的战斗遗物：161；
 - 未知遗物：117；
 - v0.111 卡牌变体：1,176；
 - 单人战斗范围：1,099；
 - 单人战斗范围 fully structured：1,099；
 - 已验证 P0/P1 报告：71 份，全部 Reliable / 0 mismatch；
 - 当前 1,000 教师 Smoke：全部 Estimated，不得作为 Reliable Policy 主标签。

 基线产物：

     data/p1-baseline-inventory.json
     data/p1-baseline-test-output.txt
     data/card-semantic-verification.json

 ## 3. 固定版本锁

 所有批次严格使用：

     Game version: v0.111.0
     Game commit: 41cef1ea
     sts2.dll SHA-256:
     0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9
     CLI protocol: 0.2.0
     Trace schema: 1

 如果版本、程序集哈希、CLI 协议、语义数据库或 scorer 发生变化：

 1. 停止当前批次；
 2. 生成新的版本基线；
 3. 不把旧报告和新报告混合；
 4. 不把旧教师标签用于新版本训练。

 ## 4. 工作目录

 Core 和模拟器：

     D:\STS2BestChoice\STS2BestChoice\STS2BestChoice.Core
     D:\STS2BestChoice\STS2BestChoice\Mod
     D:\STS2BestChoice\STS2BestChoice\tests

 模型和验证工具：

     D:\STS2BestChoice\STS2SuperModel\training
     D:\STS2BestChoice\STS2SuperModel\data
     D:\STS2BestChoice\STS2SuperModel\sts2-cli-v0111

 GitHub 模型仓库工作副本：

     D:\STS2BestChoice\work\github-STS2SuperModel

 Core 不复制进 GitHub 模型仓库。ShadowDiff 继续通过现有项目引用访问本地 Core。

 ## 5. 与 NOSL 模型的边界

 学生模型不知道随机数状态。

 学生模型输入不得包含：

 - run seed；
 - 原始 RNG state words；
 - RNG 内部状态；
 - 完整抽牌堆顺序；
 - 未来牌身份；
 - teacher snapshot；
 - 仅教师可见的隐藏字段。

 教师、ShadowDiff 和验证器可以读取 teacher snapshot、隐藏牌堆和 RNG counter，
 但这些字段只用于重建影子状态、计算机会分布、生成 teacher label 和差分验证。
 不得加入 public feature 或模型输入。

 ## 6. 阶段 0：收口前基线

 在任何修改前运行：

     $ErrorActionPreference = 'Stop'
     python D:\STS2BestChoice\STS2SuperModel\training\export_power_catalog.py
     python D:\STS2BestChoice\STS2SuperModel\training\run_p0_probes.py
     python D:\STS2BestChoice\STS2SuperModel\training\run_p1_power_probes.py --include-p0
     python D:\STS2BestChoice\STS2SuperModel\training\run_p1_relic_probes.py
     python D:\STS2BestChoice\STS2SuperModel\training\verify_repeat_runs.py
     python -m pytest D:\STS2BestChoice\STS2SuperModel\training -q --disable-warnings --ignore=D:\STS2BestChoice\STS2SuperModel\training\test_replay_action.py
     dotnet test D:\STS2BestChoice\STS2BestChoice\tests\STS2BestChoice.Tests.csproj -c Release --no-restore
     python D:\STS2BestChoice\STS2SuperModel\training\build_card_semantic_report.py --cards D:\STS2BestChoice\STS2BestChoice\data\cards\generated\0.111.0\cards.json --semantics D:\STS2BestChoice\STS2BestChoice\data\cards\generated\0.111.0\semantics.json --output D:\STS2BestChoice\STS2SuperModel\data\card-semantic-verification.json

 记录 catalog 数量、已验证数量、Reliable 数量、mismatch 数、测试结果、版本锁、
 已知 warning 和当前未完成列表。

 ## 7. 工作线 A：Power 收口

 从以下文件筛选剩余对象：

     data/powers/v0.111/power-catalog.json
     data/powers/v0.111/power-coverage.json

 筛选条件：

 - simulator_support 为 simulator_declared；
 - 属于单人战斗范围；
 - 没有 Reliable 行为报告。

 优先顺序：

 1. 改变本回合伤害、格挡、能量或费用；
 2. 改变抽牌、生成牌、弃牌或消耗；
 3. 有回合开始/结束触发；
 4. 有内部计数器或 DynamicVars；
 5. 有随机目标、随机生成或自动出牌；
 6. 仅多人/盟友或仅长期牌局效果标记 out-of-scope。

 每批建议 5-15 个 Power。

 每个 Power 必须有：

     training/fixtures/p1-power-<slug>-commands.jsonl
     data/p1-csharp-<slug>-diff-report-*.json

 必须覆盖：

 - Power 未触发状态；
 - 施加 Power 的动作；
 - Power 触发动作；
 - 回合边界；
 - amount；
 - owner/applier；
 - DynamicVars；
 - internal counters；
 - Power 到 StatusState 的映射；
 - RNG before/after；
 - Power 移除、递减或重置。

 模拟器修改必须检查真实 Power ID、来源、触发阶段和随机流。未知效果不得默认为无效果。

 ## 8. 工作线 B：战斗遗物收口

 从以下文件筛选：

     data/relics/v0.111/relic-catalog.json
     data/relics/v0.111/relic-coverage.json

 优先处理：

 1. 开战时改变 HP、能量、抽牌或费用；
 2. 改变伤害、格挡、力量、敏捷或状态；
 3. 弃牌、消耗、受伤、格挡、击杀触发；
 4. 回合开始/结束触发；
 5. 有 counter、DynamicVars 或随机目标；
 6. 只影响地图、商店、奖励的遗物标记 out-of-scope。

 每批建议 5-15 个遗物。

 文件：

     training/fixtures/p1-relic-<slug>-commands.jsonl
     data/p1-csharp-relic-<slug>-diff-report-*.json

 必须覆盖未触发、触发条件、触发后状态、计数器、回合边界、RNG counter 和
 public/teacher 隔离。遗物不得因 handler 未完成而从 snapshot 删除或默认为无效果。

 ## 9. 工作线 C：卡牌语义收口

 以单人战斗范围为准。使用 build_card_semantic_report.py 生成：

 - all variants；
 - single-player combat variants；
 - fully structured；
 - simulator-executable；
 - runtime-handler-resolvable；
 - unparsed clauses；
 - Exhaust；
 - Ethereal；
 - Retain；
 - Random；
 - Choice；
 - Power/Relic trigger。

 每批至少覆盖一组真实 fixture：

 1. 消耗；
 2. 虚无；
 3. 保留；
 4. 指定弃牌；
 5. 随机弃牌；
 6. 多目标；
 7. 随机目标；
 8. CardSelect；
 9. 生成卡；
 10. X 费用或费用随能量变化；
 11. 升级前后数值；
 12. Power/Relic 触发；
 13. 回合开始/结束效果；
 14. 自动出牌。

 Choice 动作必须使用 choice_id、selected_card_instance_ids 和 stable action_id，
 不能把当前手牌索引作为训练主键。

 修改时同时检查：

 - CardTextSemanticCompiler；
 - EffectSpec；
 - DeterministicSimulator；
 - ShadowSimulationTransitions；
 - MutableCombatState；
 - ActionCandidate；
 - CLI preview；
 - Power/Relic trigger chain；
 - RNG stream；
 - state fingerprint；
 - Exhaust/Ethereal/Retain/Innate 目的地。

 仅有文本解析或单元测试不能晋级 Reliable，必须有真实 CLI/ShadowDiff fixture。

 ## 10. 工作线 D：统一差分门禁

 标准流程：

 1. 固定 seed；
 2. 启动 CLI；
 3. 采集 public snapshot；
 4. 采集 teacher snapshot；
 5. 执行动作；
 6. 采集 post snapshot；
 7. 用同一 teacher state 重建影子状态；
 8. 执行影子动作；
 9. 比较真实和影子结果；
 10. 保存报告；
 11. 再运行一次并比较 SHA-256。

 必须比较：

     player.hp
     player.max_hp
     player.block
     player.energy
     player.max_energy
     player.powers
     player.relics
     hand
     draw_pile_count
     discard_pile_count
     exhaust_pile_count
     enemies.hp
     enemies.block
     enemies.powers
     enemies.intent
     turn
     round
     counters
     rng counters
     normalized action ID

 public view 不可见字段只在 teacher diff 中比较，并标明来源。

 每份报告至少包含：

     schema_version
     game_version
     game_commit
     assembly_sha256
     fixture
     seed
     action
     normalized_action_id
     confidence
     match
     mismatch_count
     mismatches
     rng_before
     rng_after
     real_state_hash
     shadow_state_hash

 ## 11. 晋级规则

 只有同时满足以下条件，才允许更新 catalog：

 - 真实 CLI fixture 存在；
 - 影子模拟器执行同一动作；
 - 关键字段 mismatch_count=0；
 - Power/Relic/Card 内部状态一致；
 - RNG counter 一致；
 - 双跑报告字节级一致；
 - confidence=Reliable；
 - 版本 metadata 完整；
 - public/teacher 无泄漏。

 否则保持 simulator_declared、Estimated 或 Uncalculable，并记录风险原因。
 不得修改测试期望值来掩盖差异。

 ## 12. 批次隔离和回归

 每个 agent 只提交自己的批次：

 - 不覆盖其他 agent 的报告；
 - 不修改 P0 期望值；
 - 不改动 TeacherWorker 输出协议；
 - 不改动 NOSL public feature 边界；
 - 不混合不同版本 catalog；
 - 不提交本地 DLL；
 - 不重写已通过的 P0/P1 报告。

 如果出现回归：

 1. 停止晋级；
 2. 保存差异报告；
 3. 定位最小根因；
 4. 修复或回退当前批次；
 5. 重新执行全量回归。

 ## 13. 最终交付物

 代码：

 - Core simulator/semantic 修改；
 - ShadowDiff 修改；
 - 单元测试；
 - 新增 fixture；
 - 新增探针脚本。

 数据和报告：

 - Power catalog/coverage；
 - Relic catalog/coverage；
 - card-semantic-verification；
 - 每个对象的 C# diff report；
 - p1-repeat-verification；
 - P1 baseline 和最终 gate。

 文档：

 - 更新 data/P1_POWER_VERIFICATION.md；
 - 更新或新增遗物验证报告；
 - 更新卡牌专项验证报告；
 - 更新 data/P0_VERIFICATION.md 当前摘要；
 - 更新 PLAN.md 当前进度；
 - 记录 Estimated、Uncalculable 和 out-of-scope 对象。

 ## 14. Agent 交付摘要模板

     ## P1 语义收口批次交付

     - 批次：
     - agent：
     - 工作目录：
     - 版本锁：
     - 修改文件：
     - 新增 fixture：
     - 新增报告：
     - Power before -> after：
     - Relic before -> after：
     - Card coverage before -> after：
     - Reliable 数量：
     - Estimated 数量：
     - Uncalculable 数量：
     - mismatch_count：
     - 双跑 SHA-256：
     - Core 测试：
     - CLI 测试：
     - Training 测试：
     - public leakage：
     - stable ID 缺失：
     - 仍未完成对象：
     - 是否修改 P0 文件：
     - 后续批次：

 ## 15. P1 完成定义

 P1 语义收口只有在以下条件全部满足时完成：

 - Power 缺口有完整清单；
 - 战斗遗物缺口有完整清单；
 - 卡牌单人战斗语义有专项统计；
 - 每个 Reliable 对象都有真实 CLI fixture；
 - 每份 Reliable 报告 mismatch_count=0；
 - 全量报告双跑 SHA-256 一致；
 - Core、CLI、Training 门禁通过；
 - public/teacher 隔离通过；
 - 未知效果仍显式标记；
 - 版本混杂会失败；
 - catalog、报告、P1 文档、P0 摘要一致；
 - Estimated/Uncalculable 对象未提升为 Reliable；
 - teacher/RNG 字段未泄漏到 NOSL 模型输入；
 - P0 教师数据闭环没有回归。

 P1 完成后才进入：

     NOSL 分布均衡 1,000 状态 v2
     -> 10,000 Pilot
     -> PublicStateEncoder / ActionCandidateEncoder
     -> 监督学习基线
     -> 离线 RL
     -> ONNX/C# 接入
