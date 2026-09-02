> 历史协作任务文档（2026-08-27）。P0 教师数据闭环已于 2026-08-28 完成；当前
> 现役计划、状态和后续任务以 `PLAN.md` 与 `data/P0_VERIFICATION.md` 为准。

## Agent A：扩展 Power 真实行为验证

```text
你负责 STS2SuperModel 第二优先级中的 Power 语义和真实引擎差分扩展。

工作目录：
D:\STS2BestChoice

版本锁定：
- Game: v0.111.0
- Commit: 41cef1ea
- sts2.dll SHA-256:
  0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9
- CLI protocol: 0.2.0
- Trace schema: 1

开始前必须阅读：
1. D:\STS2BestChoice\AGENTS.md
2. D:\STS2BestChoice\STS2BestChoice\AGENTS.md
3. D:\STS2BestChoice\STS2SuperModel\PLAN.md
4. D:\STS2BestChoice\STS2SuperModel\data\P0_VERIFICATION.md
5. D:\STS2BestChoice\STS2BestChoice\docs\CURRENT_STATE.md
6. D:\STS2BestChoice\STS2SuperModel\data\powers\v0.111\power-catalog.json
7. D:\STS2BestChoice\STS2SuperModel\data\powers\v0.111\power-coverage.json

目标：
从仍处于 simulator_declared 的 Power 中，完成第一批真实 CLI↔影子模拟器行为验证。

第一批优先目标：
- THORNS
- ACCURACY
- PLATING
- POISON
- PANACHE

如果某个 Power 在当前 CLI 中无法可靠构造场景，可以替换为另一个容易稳定复现的 simulator_declared Power，但必须在报告中说明替换原因。

必须执行：

1. 从 v0.111 sts2.dll IL、运行时 Power 对象和真实 CLI 行为确认每个 Power：
   - 原始 Power ID
   - amount/stack
   - owner/applier
   - DynamicVars
   - internal counters
   - trigger phase
   - 触发顺序
   - 持续时间和清除时机
   - RNG 流及消费数量
   - 与伤害、格挡、出牌、回合边界的实际关系

2. 不允许仅根据：
   - 中文名称
   - Wiki 描述
   - 反射方法名称
   - simulator 白名单
   将 Power 标记为 LiveObserved 或 Reliable。

3. 为每个 Power 建立固定种子 CLI fixture：
   D:\STS2BestChoice\STS2SuperModel\training\fixtures\p1-power-<name>-commands.jsonl

4. 新建独立探针驱动器：
   D:\STS2BestChoice\STS2SuperModel\training\run_p1_power_probes.py

   不要修改现有 run_p0_probes.py 的既有 P0 矩阵。

5. 使用真实 CLI 执行动作，并使用：
   D:\STS2BestChoice\STS2SuperModel\training\ShadowDiff
   对同一动作运行 STS2BestChoice.Core.DeterministicSimulator。

6. 差分至少比较：
   - 玩家/敌人 HP
   - 格挡
   - 能量
   - 手牌和牌堆
   - Power ID、amount、owner/applier
   - DynamicVars
   - internal counters
   - Power 出现和消失
   - RNG 七条流 counter
   - 回合推进
   - terminal state

7. 如果发生差异：
   - 先确认真实引擎事实；
   - 修正最接近根因的最小代码；
   - 不写针对单个 fixture 的硬编码绕过；
   - 不通过跳过字段、降低 confidence 或删除比较来让报告变绿。

8. 允许修改的主要文件：
   - STS2BestChoice.Core\Simulation\DeterministicSimulator.cs
   - training\ShadowDiff\Program.cs
   - tests 中与本批 Power 对应的测试
   - PowerCatalogTests.cs
   - training\export_power_catalog.py
   - training\test_power_catalog.py
   - 新建的 p1-power fixtures/runner/reports

9. 不得修改：
   - ExpectimaxEngine.cs 的分支采样
   - TeacherWorker
   - 教师标签 Schema
   - PyTorch/ONNX 模型
   - Relic catalog 的行为状态
   - RandomForeseer 运行时依赖
   - 游戏版本锁

证据提升规则：
只有同时满足以下条件才能加入 runtimeProbedSet：
- 真实 CLI fixture 成功执行；
- ShadowDiff confidence=Reliable；
- mismatch_count=0；
- fixture 可重复运行；
- 对应报告文件已保存；
- 关键触发前后状态都被验证。

验收命令：

dotnet test D:\STS2BestChoice\STS2BestChoice\tests\STS2BestChoice.Tests.csproj -c Release --no-restore

python D:\STS2BestChoice\STS2SuperModel\training\run_p1_power_probes.py

训练工具测试：
工作目录 D:\STS2BestChoice\STS2SuperModel\training
运行全部 test_*.py，并确保 PyArrow 测试没有被错误跳过。

最终交付：
- 每个 Power 的真实行为结论
- 修改文件清单
- fixture 和差分报告路径
- 每个报告的 confidence/mismatch_count
- 更新后的 Power catalog 统计
- 全部测试的实际输出
- 未解决差异及原因
- 给下一位 Agent 的简短接手说明

不要提交或推送 Git，除非用户明确要求。
```

## Agent B：扩展遗物真实行为验证

```text
你负责 STS2SuperModel 第二优先级中的遗物模拟语义和真实引擎差分扩展。

必须使用独立分支/worktree，避免与 Power Agent 同时修改
DeterministicSimulator.cs 发生覆盖。

工作目录：
D:\STS2BestChoice

版本锁定：
- Game: v0.111.0
- Commit: 41cef1ea
- sts2.dll SHA-256:
  0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9
- CLI protocol: 0.2.0

开始前阅读：
1. D:\STS2BestChoice\AGENTS.md
2. D:\STS2BestChoice\STS2BestChoice\AGENTS.md
3. D:\STS2BestChoice\STS2SuperModel\PLAN.md
4. D:\STS2BestChoice\STS2SuperModel\data\P0_VERIFICATION.md
5. D:\STS2BestChoice\STS2SuperModel\data\relics\v0.111\relic-catalog.json
6. D:\STS2BestChoice\STS2SuperModel\data\relics\v0.111\relic-coverage.json
7. D:\STS2BestChoice\STS2BestChoice\tests\RelicCatalogTests.cs
8. D:\STS2BestChoice\STS2BestChoice\tests\RelicSimulationTests.cs

当前事实：
- 299 个遗物已结构化捕获；
- 14 个已声明模拟器映射已有真实差分；
- 165 个遗物为 UnsupportedKnownEffect；
- 119 个遗物为 Unknown；
- 未验证遗物不能用于 Reliable 教师标签。

第一批优先目标：

第一阶段：
- INCENSE_BURNER
- SUNDIAL

这两个已有部分 Core 测试/逻辑，优先补真实 CLI 差分。

第二阶段：
- TOUGH_BANDAGES
- TUNGSTEN_ROD
- UNCEASING_TOP

如果实机 fixture 无法稳定构造，只完成可以可靠验证的项目，并说明其余阻塞原因。

必须执行：

1. 从 ModelDb.AllRelics 导出的 ID、v0.111 IL 和真实 CLI 行为确认：
   - 稳定 relic ID
   - counter
   - DynamicVars
   - 战斗开始/出牌/受伤/弃牌/回合边界触发
   - owner 检查
   - 是否只触发一次
   - counter 更新和重置时机
   - RNG 流消费

2. Wiki 只可用于名称、描述和版本线索，不能作为运行时语义证明。

3. 每个遗物创建固定 fixture：
   D:\STS2BestChoice\STS2SuperModel\training\fixtures\p1-relic-<name>-commands.jsonl

4. 新建：
   D:\STS2BestChoice\STS2SuperModel\training\run_p1_relic_probes.py

   不修改 run_p0_probes.py 的既有矩阵。

5. 使用真实 CLI 和 ShadowDiff 比较：
   - HP/格挡/能量
   - 手牌/牌堆
   - relic ID
   - counter
   - DynamicVars
   - Power 状态
   - RNG counters
   - 回合边界
   - terminal state

6. 实现模拟器 handler 时：
   - 修改最接近根因的最小区域；
   - 不为单个 fixture 写特殊分支；
   - 未知状态必须保留；
   - 不把未知遗物当成无效果；
   - 不降低差分标准。

7. 允许修改：
   - DeterministicSimulator.cs 中遗物处理部分
   - RelicCatalogTests.cs
   - RelicSimulationTests.cs
   - RelicTrainingFeatureTests.cs
   - ShadowDiff 遗物导入/比较部分
   - 新建的 p1-relic fixtures、runner、reports
   - relic coverage 输出

8. 不得修改：
   - Power runtimeProbedSet
   - ExpectimaxEngine
   - TeacherWorker
   - 模型训练代码
   - CLI 普通协议
   - RandomForeseer 运行时依赖

提升为 SimulatorSupported/LiveObserved 的必要条件：
- 真实 CLI 行为已经观察；
- ShadowDiff confidence=Reliable；
- mismatch_count=0；
- counter/DynamicVars 已纳入比较；
- 触发前后均有快照；
- 重复执行结果一致。

验收：
- Core Release 全量测试通过；
- 新增遗物 fixture 全部通过；
- 原有 21 个 P0 fixture/41 个报告保持零差异；
- relic coverage 数量内部一致；
- 未验证遗物仍保持 UnsupportedKnownEffect/Unknown。

最终报告：
- 已验证遗物列表
- 每个遗物的真实触发顺序
- fixture/report 路径
- simulator 改动
- catalog 状态变化
- 测试结果
- 尚未解决项目及证据缺口

不要提交或推送 Git，除非用户明确要求。
```

## Agent C：补齐 CLI 战斗范围质量门禁

```text
你负责 STS2SuperModel 第二优先级中的 CLI 战斗范围完整验证。

工作目录：
D:\STS2BestChoice\STS2SuperModel\sts2-cli-v0111

开始前阅读：
1. D:\STS2BestChoice\STS2SuperModel\sts2-cli-v0111\CLAUDE.md
2. D:\STS2BestChoice\STS2SuperModel\sts2-cli-v0111\docs\v0111-migration.md
3. D:\STS2BestChoice\STS2SuperModel\PLAN.md
4. D:\STS2BestChoice\STS2SuperModel\data\P0_VERIFICATION.md
5. training\schemas\trace-schema-v1.json
6. training\schemas\public-state-schema-v1.json
7. training\schemas\teacher-state-schema-v1.json

范围：
只补当前单人战斗回合的 CLI/Trace/Replay 门禁。

不得处理：
- 地图策略
- 商店
- 奖励
- 事件决策
- save/load
- 完整牌局自动通关
- Power/遗物模拟器 handler
- Expectimax 教师生成
- PyTorch/ONNX

目标：

1. 增加真实多卡选择测试：
   - card_select 输出稳定 choice_id；
   - 每个选项有稳定 card_instance_id；
   - selected_card_instance_ids 可跨快照回放；
   - index 变化不改变训练动作主键。

2. 增加跨进程稳定 ID 测试：
   - 同一 seed、同一 deck、同一命令序列；
   - 两个 CLI 进程的 public state hash 一致；
   - ActionCandidate ID 一致；
   - card/potion/enemy/choice ID 一致。

3. 增加 Trace 中断恢复测试：
   - action 执行失败；
   - 写入 failed_step；
   - recovery_required=true；
   - 已完成 trace 行不丢失；
   - quit 或异常退出前 flush；
   - 恢复后新 trace 与旧 trace 关系明确。

4. 增加旧协议兼容测试：
   - card_index/target_index 仍可执行；
   - stable ID 只作为训练层扩展；
   - CLI protocol 仍为 0.2.0。

5. 增加 public/teacher 隔离测试：
   public 禁止出现：
   - run_seed
   - RNG raw words
   - 完整未来抽牌身份
   - teacher-only pile order

   teacher snapshot 必须包含恢复影子状态所需信息，但不得把原始 seed 写进模型特征。

6. 增加 combat-scope gate：
   新建一个明确的测试入口或脚本，例如：
   tests/run_combat_scope_gate.py
   或 training/run_cli_combat_gate.py

   输出：
   - 测试数量
   - 版本锁
   - 稳定回放结果
   - Trace schema 结果
   - public leakage 数量
   - 失败项目

允许修改：
- sts2-cli-v0111/src/Sts2Headless
- sts2-cli-v0111/tests
- training/replay_action.py
- training/validate_dataset.py
- 相关 schema 和测试

不得修改：
- DeterministicSimulator 的 Power/遗物行为
- Power/Relic catalog 证据等级
- ExpectimaxEngine
- 教师标签
- 模型代码

验收命令：

dotnet build .\src\Sts2Headless\Sts2Headless.csproj -c Debug --no-restore

python -m pytest -q tests\test_v0111_consistency.py tests\test_combat.py <新增测试文件>

同时运行 training 目录全部测试。

完成后更新文档时必须明确：
- combat-scope gate 是否全绿；
- reward/shop/save-load/full-run 仍为 out-of-scope；
- 不得将定向测试描述成完整 CLI 全量测试。

不要提交或推送 Git，除非用户明确要求。
```

## Agent D：数据质量与泄漏门禁

```text
你负责第二优先级中的训练数据质量门禁，不生成教师标签，也不训练模型。

工作目录：
D:\STS2BestChoice\STS2SuperModel

阅读：
- PLAN.md
- data\P0_VERIFICATION.md
- training\schemas\*.json
- training\validate_dataset.py
- training\split_dataset.py
- training\build_dataset_manifest.py
- training\build_dataset_report.py

目标：

1. 检查并补齐：
   - game/version/assembly/CLI/simulator/scorer/schema/model metadata
   - source/shard/generator config SHA-256
   - episode/seed group 切分
   - public/teacher 泄漏检测
   - action stable ID 完整性
   - Reliable/Estimated/Uncalculable 统计一致性
   - 重复状态和重复动作检测
   - 空 teacher_best_actions 的明确分类
   - malformed/partial JSONL 的错误定位

2. 增加数据集级门禁：
   - 同一 episode 不得跨 train/validation/test/challenge；
   - 相同 public_state_hash 的相关 teacher states 不得被拆到不同主集合；
   - mixed version 必须拒绝；
   - 缺少 generator_config_hash 必须拒绝；
   - seed 和 RNG raw words 不得进入 public features；
   - Uncalculable 不得作为 policy 主标签。

3. 新建机器可读报告：
   data/dataset-quality-gate.json

4. 增加负向测试：
   - 混合 v0.110/v0.111
   - 错误程序集 hash
   - public 泄漏
   - episode 跨 split
   - 空 stable action ID
   - 非法 Reliable 标签
   - 损坏 Parquet/JSONL
   - shard hash 不一致

允许修改：
- training 下的数据工具、schema 和测试
- 数据质量报告

不得修改：
- Core simulator
- CLI 游戏行为
- Expectimax
- TeacherWorker
- PyTorch/ONNX
- Power/Relic 证据等级

验收：
- training 全部测试通过；
- 现有 P0 smoke artifact 仍可解析；
- 所有新建负向 fixture 都被正确拒绝；
- 输出清楚区分工具 smoke 数据与真正教师数据。

不要提交或推送 Git，除非用户明确要求。
```

## 推荐调度

```text
可并行：
Agent C（CLI）
Agent D（数据质量）

独立分支并行、依次合并：
Agent A（Power）
Agent B（Relic）

建议合并顺序：
CLI → 数据质量 → Power → Relic
```

Power 和遗物 Agent 完成后，再统一运行全套回归并同步到 GitHub 的 `mtdxmtdx/STS2SuperModel`。
