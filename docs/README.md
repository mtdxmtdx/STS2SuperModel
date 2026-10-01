# 文档导航

先读 [当前状态](STATUS.md)，再按主题选文档。设计稿、历史验收和当前执行状态分别管理，避免把旧“完成”当成现行资格。

## 当前入口

- [项目说明](../README.md)
- [当前状态与暂停事项](STATUS.md)
- [开发与验证](DEVELOPMENT.md)
- [Agent 执行约定](../AGENTS.md)

## 基线计划

这些文件保留根目录路径用于已有工具及文档引用；它们描述历史基线，不自动覆盖当前状态。

- [工程计划](../PLAN.md)
- [NOSL Expectimax 教师计划](../PLAN_NOSL.md)
- [遗物与卡牌缺口计划](../RELIC_CARD_GAP_COMPLETION_PLAN.md)
- [归档计划及交接](archive/README.md)

## 架构与实验想法

尚未自动转化为开发任务或验收结论。

- [全局决策架构](ideas/GLOBAL_DECISION_ARCHITECTURE_IDEA.md)
- [全局决策计划](ideas/PLAN_GLOBAL_DECISION.md)
- [NOSL 全局模型改进](ideas/NOSL_GLOBAL_MODEL_IMPROVEMENT_IDEAS.md)
- [自博弈策略迭代](ideas/SELF_PLAY_POLICY_ITERATION_IDEA.md)
- [影子模拟器优化建议](ideas/SHADOW_SIMULATOR_OPTIMIZATION_RECOMMENDATIONS.md)

## 研究

- [种子机制研究](research/杀戮尖塔种子机制研究.md)
- [种子机制核验](research/种子机制核验.md)
- [无头模拟器研究](research/headless-simulator-research-agent.md)
- [STS1 / STS2 牌组评价研究](research/STS1_STS2_DECK_EVALUATION_RESEARCH_REPORT.md)
- [STS2 RL Agent 分析](research/STS2_RL_AGENT_ANALYSIS.md)

## 数据与验证证据

每份报告只证明其自身声明的版本、对象、终点和比较范围。

- [P0 验证](../data/P0_VERIFICATION.md)
- [P1 卡牌](../data/P1_CARD_VERIFICATION.md)、[Power](../data/P1_POWER_VERIFICATION.md)、[遗物](../data/P1_RELIC_VERIFICATION.md)
- [药水覆盖率](../data/potions/v0.111/potion-coverage.md)、[遗物覆盖率](../data/relics/v0.111/relic-coverage.md)
- [历史模型与 holdout 产物](../data/combat_model/)
- [已死亡目标规则](verification/a0-dead-target-rule.md)
- [数据契约](../training/schemas/README.md)

## 本地草稿与外部档案

维护者本地可能还有未提交的新版计划、`docs/plans/` 工作包文件和大型证据档案。它们不因出现在本地磁盘上就成为本次 GitHub 发布内容；公开入口只链接本次提交或既有版本中存在的文件。B/C/D 档案位置与资格见 [当前状态](STATUS.md)。
