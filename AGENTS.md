# STS2SuperModel Agent 入口

## 项目定位

本仓库实现 STS2 单回合教师数据与模型训练管道，不包含主模组运行时代码。

## 版本锁

- Game `v0.111.0` / commit `41cef1ea`
- CLI protocol `0.2.0` / trace schema `1`
- 主要入口：`training/collectors/teacher_worker.py`
- C# evaluator：`training/TeacherEvaluator/`

## 常用验证

```powershell
python -m pytest training -q --disable-warnings --ignore=training/test_replay_action.py
dotnet build training/TeacherEvaluator/STS2BestChoice.TeacherEvaluator.csproj -c Release --no-restore
python training/verify_repeat_runs.py
```

1,000 状态 Smoke 产物位于 `data/teacher-realsmoke-1000*`，质量门禁报告必须为
`verdict=pass`。未知语义或 evaluator 回退只能标记 Estimated，不能提升为 Reliable。

## 当前状态

M0-M2 NOSL 教师基础闭环已完成并通过当前工作树验证：`NoslBeliefState` 只来自
CLI 公共观测，未知随机效果走概率分支，`NOSL_EXACT_OFFLINE` 入口已接通。
Core 测试为 713 passed，Training 测试为 65 passed/1 skipped；TeacherEvaluator
Release 构建无错误。下一阶段是补齐剩余语义、生成并分层验证 1k/10k/100k NOSL
数据；随机药水池和未确认语义必须继续标记 Estimated/Uncalculable。

权威计划：`PLAN_NOSL.md`（NOSL 流程）、`RELIC_CARD_GAP_COMPLETION_PLAN.md`
（遗物/卡牌语义收口）和 `PLAN.md`（全局数据/训练路线）。
