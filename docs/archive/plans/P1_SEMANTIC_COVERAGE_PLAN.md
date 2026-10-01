 # P1 语义覆盖与真实差分验证实施计划

 更新时间：2026-08-28
 状态：待分配给其他 agent 执行

 ## 1. 目标

 本计划只处理 STS2 单回合战斗中的 P1 语义覆盖：

 1. 剩余未验证 Power；
 2. 剩余战斗遗物；
 3. 卡牌语义和模拟器 handler 缺口；
 4. 真实 CLI 与影子模拟器差异报告。

 P0 教师数据闭环已经完成，不要重复实现 Expectimax 分层采样、TeacherWorker、
 100/1,000 Smoke 或 DatasetManifest。

 最终只有通过真实引擎差分的对象才能晋级为 simulator_supported、LiveObserved、Reliable。

 ## 2. 固定版本

 Game version: v0.111.0
 Game commit: 41cef1ea
 sts2.dll SHA-256: 0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9
 CLI protocol: 0.2.0
 Trace schema: 1

 开始前阅读：

 - D:\STS2BestChoice\AGENTS.md
 - D:\STS2BestChoice\STS2BestChoice\AGENTS.md
 - D:\STS2BestChoice\STS2SuperModel\AGENTS.md
 - D:\STS2BestChoice\STS2SuperModel\PLAN.md
 - D:\STS2BestChoice\STS2SuperModel\data\P0_VERIFICATION.md
 - D:\STS2BestChoice\STS2SuperModel\data\P1_POWER_VERIFICATION.md

 ## 3. 工作目录

 Core 与影子模拟器：

 D:\STS2BestChoice\STS2BestChoice\STS2BestChoice.Core

 模组和真实状态：

 D:\STS2BestChoice\STS2BestChoice\Mod
 D:\STS2BestChoice\STS2BestChoice\tests

 模型、fixture 和报告：

 D:\STS2BestChoice\STS2SuperModel\training
 D:\STS2BestChoice\STS2SuperModel\data

 CLI：

 D:\STS2BestChoice\STS2SuperModel\sts2-cli-v0111

 需要 Core 的 ShadowDiff 通过现有相对项目引用访问本地 Core。不要把整个模组工程复制到 GitHub 模型仓库。

 ## 4. P0 隔离规则

 不得重写或顺手重构：

 - ExpectimaxEngine 的随机采样协议；
 - TeacherWorker 的标签质量分类；
 - trace_to_training 的 public/teacher 配对；
 - CombatSnapshot 和 ActionCandidate 的冻结字段；
 - 已通过的 P0/P1 报告；
 - 版本锁和质量门禁阈值。

 发现回归时先保留报告并恢复 P0 行为，不得用修改测试期望值掩盖回归。

 ## 5. 阶段 0：基线盘点

 从 Power、Relic、Card catalog 和已有报告重新计算当前状态，不直接相信旧数字。

 运行：

     cd D:\STS2BestChoice\STS2SuperModel
     python training/export_power_catalog.py
     python training/run_p0_probes.py
     python training/run_p1_power_probes.py --include-p0
     python training/run_p1_relic_probes.py
     python -m pytest training -q --disable-warnings --ignore=training/test_replay_action.py
     dotnet test D:\STS2BestChoice\STS2BestChoice\tests\STS2BestChoice.Tests.csproj -c Release --no-restore

 记录 catalog 数量、已验证数量、Reliable 数量、mismatch 数、测试结果和版本锁。

 建议产物：

 - data/p1-baseline-inventory.json
 - data/p1-baseline-test-output.txt

 ## 6. 工作线 A：Power

 从 data/powers/v0.111/power-catalog.json 和 power-coverage.json 筛选：

 - simulator_support 为 simulator_declared；
 - 属于单人战斗范围；
 - 没有 Reliable 行为报告。

 优先顺序：

 1. 改变伤害、格挡、能量、抽牌或生存；
 2. 有回合触发、内部计数器或 DynamicVars；
 3. 改变敌人意图、目标或随机结果；
 4. 只影响长期牌局的对象标记 out-of-scope。

 每个 Power 必须创建最小 fixture，覆盖初始状态、施加、触发、回合边界、
 Power amount、DynamicVars、计数器和 RNG before/after。

 文件命名：

 - training/fixtures/p1-power-<slug>-commands.jsonl
 - data/p1-csharp-<slug>-diff-report-0.json
 - data/p1-csharp-<slug>-diff-report-1.json

 模拟器修改必须检查 Power ID、owner/applier、amount、DynamicVars、内部计数器、
 trigger phase、StatusState 映射和 RNG stream。未知效果不得默认为无效果。

 ## 7. 工作线 B：战斗遗物

 从 data/relics/v0.111/relic-catalog.json 和 relic-coverage.json 筛选当前单人战斗
 会影响开战、抽牌、能量、费用、伤害、格挡、HP、弃牌、消耗、回合结束、
 Power、Potion 或随机目标的遗物。

 每个遗物至少覆盖：

 - 未触发状态；
 - 触发条件刚好满足；
 - 触发后状态；
 - counter/DynamicVars 变化；
 - 必要的回合边界；
 - RNG before/after；
 - public/teacher 隔离。

 文件命名：

 - training/fixtures/p1-relic-<slug>-commands.jsonl
 - data/p1-csharp-relic-<slug>-diff-report-0.json
 - data/p1-csharp-relic-<slug>-diff-report-1.json

 遗物必须保留原始 ID、owner、amount、DynamicVars、counters、active、trigger phases、
 semantic tags、numeric modifiers、handler ID、confidence、evidence 和 source version。

 ## 8. 工作线 C：卡牌语义

 按以下类别建立缺口清单：

 - 无结构化 effect；
 - simulator handler 缺失；
 - target 错误；
 - cost/upgrade/DynamicVars 错误；
 - Power/Relic 触发链错误；
 - RNG、随机目标或随机弃牌错误；
 - Exhaust、Ethereal、Retain、Innate 错误；
 - Choice/CardSelect 缺失；
 - 生成卡牌或多目标效果错误。

 至少覆盖：

 - 攻击、格挡、能量、抽牌；
 - 消耗、虚无、保留；
 - 指定弃牌、随机弃牌；
 - 多目标、随机目标；
 - 多卡选择；
 - 生成卡；
 - 费用随能量变化；
 - 升级数值；
 - Power 施加和回合触发。

 Choice 动作必须使用 choice_id 和 selected_card_instance_ids，不能把手牌索引作为
 训练主键。

 每次修改检查 CardTextSemanticCompiler、EffectSpec、DeterministicSimulator、
 ShadowSimulationTransitions、MutableCombatState、ActionCandidate、CLI preview、
 Power/Relic trigger chain 和 RNG。

 ## 9. 工作线 D：CLI 与影子差分

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

 - HP、MaxHP、Block、Energy、MaxEnergy；
 - Hand、Draw、Discard、Exhaust；
 - Power、Relic、amount、DynamicVars、counters；
 - 敌人 HP、Block、Power、Intent；
 - round/turn；
 - stable action IDs；
 - RNG counters。

 报告至少包含 schema_version、版本锁、fixture、seed、action、confidence、
 match、mismatch_count、mismatches、RNG before/after、真实 hash、影子 hash 和
 重复运行 hash。

 ## 10. 晋级门禁

 Reliable 必须同时满足：

 - 真实 CLI fixture 存在；
 - 影子模拟器执行同一动作；
 - 关键字段 mismatch_count=0；
 - Power/Relic/Card 内部状态一致；
 - RNG counter 一致；
 - 两次报告字节级一致；
 - confidence=Reliable；
 - 版本 metadata 完整；
 - public/teacher 无泄漏。

 否则保持 simulator_declared、Estimated 或 Uncalculable，并记录风险原因。

 ## 11. 每批验证命令

     dotnet test D:\STS2BestChoice\STS2BestChoice\tests\STS2BestChoice.Tests.csproj -c Release --no-restore
     python D:\STS2BestChoice\STS2SuperModel\training\run_p1_power_probes.py --include-p0
    python D:\STS2BestChoice\STS2SuperModel\training\run_p1_relic_probes.py
    python -m pytest D:\STS2BestChoice\STS2SuperModel\training -q --disable-warnings --ignore=D:\STS2BestChoice\STS2SuperModel\training\test_replay_action.py
    python D:\STS2BestChoice\STS2SuperModel\training\verify_repeat_runs.py

卡牌专项统计：

    python D:\STS2BestChoice\STS2SuperModel\training\build_card_semantic_report.py `
      --cards D:\STS2BestChoice\STS2BestChoice\data\cards\generated\0.111.0\cards.json `
      --semantics D:\STS2BestChoice\STS2BestChoice\data\cards\generated\0.111.0\semantics.json `
      --output D:\STS2BestChoice\STS2SuperModel\data\card-semantic-verification.json

 记录每个对象的 mismatch_count、重复 SHA-256、Reliable/Estimated/Uncalculable 数量、
stable ID 缺失数和 public leakage 数。

pytest 临时目录由仓库根 `pytest.ini` 的 `norecursedirs` 排除；残留清理使用
`python training/clean_pytest_residue.py --apply`，不清理源码或正式数据。

 ## 12. Agent 交付格式

 每批交付必须包含：

 1. 修改文件清单；
 2. fixture 清单；
 3. 报告清单；
 4. catalog before/after；
 5. Core、CLI、Training 测试结果；
 6. 每个对象的 mismatch_count；
 7. 双跑 SHA-256；
 8. 仍为 Estimated/Uncalculable 的对象；
 9. 是否修改了 P0 文件；
 10. 后续缺口。

 ## 13. 完成定义

 P1 只有在以下条件全部满足时完成：

 - Power、遗物、卡牌缺口均有清单；
 - 每个 Reliable 对象都有真实 fixture；
 - 每个 Reliable 报告 mismatch_count=0；
 - 所有 Reliable 报告双跑一致；
 - Core、CLI、Training 门禁通过；
 - 未知效果仍明确标记；
 - public/teacher 隔离通过；
 - 版本混杂会失败；
 - catalog、报告、PLAN 和 P0_VERIFICATION 状态一致。

 P1 完成后才进入覆盖均衡的 1,000 数据、10,000 Pilot、模型输入编码和监督训练。
