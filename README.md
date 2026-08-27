# STS2SuperModel

Slay the Spire 2 v0.111.0 单人战斗回合模型基础设施：真实 CLI 状态采集、确定性影子模拟、Expectimax 教师搜索和后续 PyTorch/ONNX 模型训练。

本仓库**只包含模型与数据管线相关内容**，不包含 `STS2BestChoice` 游戏模组源码或发布 DLL。

## 版本锁定

- Game: `v0.111.0`
- Commit: `41cef1ea`
- `sts2.dll` SHA-256: `0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9`
- CLI protocol: `0.2.0`
- Trace schema: `1`

## 目录

- `PLAN.md`：完整实施计划与验收标准
- `data/`：v0.111 Power/Relic catalog、P0 差分报告、Trace 和 smoke 数据
- `training/`：Trace 转换、Schema、Manifest、Parquet、数据切分和 ShadowDiff
- `sts2-cli-v0111/`：v0.111 headless CLI、状态快照和训练 Trace 扩展
- `杀戮尖塔种子机制研究.md`、`种子机制核验.md`：随机机制研究记录

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
```

P0 验收证据见 `data/P0_VERIFICATION.md`。教师数据的大规模生成、PyTorch 训练和 ONNX 接入属于后续阶段。
