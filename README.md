# STS2SuperModel

Slay the Spire 2 v0.111.0 单人战斗回合模型基础设施：真实 CLI 状态采集、确定性影子模拟、Expectimax 教师搜索和后续 PyTorch/ONNX 模型训练。

本仓库**只包含模型与数据管线相关内容**，不包含 `STS2BestChoice` 游戏模组源码或发布 DLL。

## 版本锁定

- Game: `v0.111.0`
- Commit: `41cef1ea`
- `sts2.dll` SHA-256: `0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9`
- CLI protocol: `0.2.0`
- Trace schema: `1`

## 仓库布局与外部依赖

本仓库**不自带** `STS2BestChoice.Core` 源码。`training/TeacherEvaluator/` 与
`training/ShadowDiff/` 两个 C# 项目通过相对路径引用**同级目录**中的 Core：

```
<父目录>/
├── STS2BestChoice/                    # 另一个仓库：战斗规划模组 + Core
│   └── STS2BestChoice.Core/STS2BestChoice.Core.csproj
└── STS2SuperModel/                    # 本仓库
    └── training/TeacherEvaluator/     # ProjectReference → ../../../STS2BestChoice/...
```

因此只 clone 本仓库时，Python 侧管线可以正常工作，但**两个 C# 项目会构建失败**。
需要 C# evaluator 或 ShadowDiff 时，请把 `STS2BestChoice` 仓库检出到本仓库的同级目录。

历史上 Core 曾以副本形式内嵌在 `core/` 下；为避免与主仓库产生语义漂移，
该副本已移除，改为单一来源引用。

## 数据入库策略

`data/` 只版本化**审计追溯链**（manifest、质量门禁、差分与覆盖率报告，`*.json` / `*.md`）。
数据集本体（`*.jsonl` / `*.parquet` / `*.pt`）与原始 CLI 转储默认不入库，
由 manifest 记录的生成参数与 SHA-256 负责可追溯与可重建。

历史上已跟踪的数据文件不受此策略影响。确需长期版本化的精选数据集用
`git add -f <path>` 显式加入，并在提交信息中说明理由。

## 目录

- `PLAN.md`：完整实施计划与验收标准
- `RELIC_CARD_GAP_COMPLETION_PLAN.md`：遗物与卡牌缺口收口的 agent 执行计划
- `data/`：v0.111 Power/Relic catalog、P0 差分报告、Trace 和 smoke 数据
- `training/`：Trace 转换、Schema、Manifest、Parquet、数据切分和 ShadowDiff
- `training/TeacherEvaluator/`：接收 `sts2.teacher-evaluator.v1` 请求，校验 `nosl_belief_state`，并调用 C# `NoslExpectimaxTeacher` 的 evaluator bridge
- `training/schemas/nosl-belief-state-v1.json`：仅由 CLI 公共观测派生的 NOSL 教师输入契约
- `training/verify_repeat_runs.py`：P0/P1 差分报告双跑 SHA-256 验证
- `training/build_card_semantic_report.py`：生成 v0.111 卡牌语义专项统计
- `sts2-cli-v0111/`：v0.111 headless CLI、状态快照和训练 Trace 扩展
- `杀戮尖塔种子机制研究.md`、`种子机制核验.md`：随机机制研究记录

教师标签 smoke 产物位于 `data/teacher-realsmoke-1000.jsonl`；该批次包含 1,000 条
经 evaluator 处理的记录（874 个唯一 public 状态），仍明确标记为
`EstimatedByHeuristic`/`BudgetBound`。当前 M0-M2 已增加严格 NOSL 输入和离线
Expectimax 入口，但尚未宣告 10k/100k 大规模 Reliable 数据完成。

卡牌专项统计位于 `data/card-semantic-verification.json`。差分报告双跑验证使用：

```powershell
python training/verify_repeat_runs.py
```

测试临时目录由根目录 `pytest.ini` 排除；需要清理时使用
`python training/clean_pytest_residue.py --apply`。

## 本地 CLI 准备

游戏程序集和第三方 DLL 不提交到仓库。安装本地 Slay the Spire 2 后，在 `sts2-cli-v0111` 下运行 `setup.sh`，或按上游 CLI 文档将所需运行库放入本地 `lib/`。

## 验证

```powershell
# CLI
dotnet build .\sts2-cli-v0111\src\Sts2Headless\Sts2Headless.csproj -c Debug --no-restore
python -m pytest -q .\sts2-cli-v0111\tests\test_v0111_consistency.py .\sts2-cli-v0111\tests\test_combat.py

# Training tools（需 Python 3.12、PyArrow、jsonschema、pytest）
$env:PYTHONPATH='.python-deps'
python -m pytest -q .\training --ignore=.\training\test-output

# Dataset quality gate (requires Python 3.12 + PyArrow)
python .\training\run_quality_gate.py `
  --dataset-path data\p0-combat-action-training.jsonl `
  --dataset-kind tool_smoke `
  --training data\p0-combat-action-training.jsonl `
  --trace data\p0-combat-action-trace.jsonl `
  --manifest data\p0-combat-action-manifest.json `
  --parquet-manifest data\p0-combat-action-parquet\parquet-manifest.json `
  --split-dir data\p0-combat-action-splits `
  --output data\dataset-quality-gate.json

# Build the C# evaluator bridge (requires the external Core checkout)
dotnet build .\training\TeacherEvaluator\STS2BestChoice.TeacherEvaluator.csproj -c Release
```

P0/P1 语义证据见 `data/P0_VERIFICATION.md`、`data/P1_POWER_VERIFICATION.md` 和
`data/P1_RELIC_VERIFICATION.md`；NOSL 契约与后续数据路线见 `PLAN_NOSL.md`。
教师数据的大规模生成、PyTorch 训练和 ONNX 接入属于后续阶段。
