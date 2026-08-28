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
```

1,000 状态 Smoke 产物位于 `data/teacher-realsmoke-1000*`，质量门禁报告必须为
`verdict=pass`。未知语义或 evaluator 回退只能标记 Estimated，不能提升为 Reliable。

## 当前状态

P0 教师数据闭环已完成；提交 `553887a`。下一阶段是扩展角色/难度/场景多样性、补齐
语义 handler，再生成可用于 Reliable policy 主损失的 10k/100k 数据。
