# 开发与验证

所有命令从仓库根目录运行。本文整理既有入口，不声称本次文档调整重新通过了游戏构建或完整测试。

## 环境与依赖

- .NET 9；Python 3.12 训练环境，Python 依赖见 [requirements-training.txt](../training/requirements-training.txt)。个别脚本还需其自身声明的依赖。
- `STS2BestChoice.Core` 位于本仓库同级 `STS2BestChoice/STS2BestChoice.Core/`，由 TeacherEvaluator 和 ShadowDiff 的 `ProjectReference` 引用。
- 锁定游戏 `v0.111.0 / 41cef1ea`，不要把 manifest 的最低兼容版本当成实际测试版本。
- 游戏与第三方 DLL 在本机准备。CLI 的运行库准备方式见 [CLI README](../sts2-cli-v0111/README.md) 和 `sts2-cli-v0111/setup.sh`；不把这些二进制提交到本仓库。
- `--no-restore` 仅用于已经还原 NuGet 依赖的环境。首次构建先完成对应项目的 restore。
- `.python-deps` 是原维护环境的本地依赖目录；使用虚拟环境时使用该环境的解释器，不必复制这个目录。

## 常用验证入口

下列命令沿用原 README。选择与本次改动相关的检查，不机械全量重跑。数据门禁示例所引用的本地数据集、trace 和 parquet 可能被 Git 忽略；先按对应 manifest 准备输入，不把缺少数据误判成测试通过。


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


## 证据与数据维护

- 原始 trace、转换结果、manifest、质量门禁和模型产物要区分；保留失败和 Estimated 样本，不通过过滤失败来抬高资格。
- [.gitignore](../.gitignore) 已隔离构建缓存、临时工作目录、本机运行库和大型派生数据；模型交付物有显式例外。
- `training/verify_repeat_runs.py` 验证指定历史报告的复现性，不代表新版完整语义验收。
- 清理脚本 `training/clean_pytest_residue.py` 的 `--apply` 会实际清理残留；先核对目标，历史唯一证据不作为临时缓存处理。

文档调整只检查路径、导航、内容保留和提交范围；涉及语义或协议时再运行相应测试。提交应明确区分本次修改和既有未提交工作。
