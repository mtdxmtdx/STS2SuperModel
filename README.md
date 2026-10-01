# STS2SuperModel

Slay the Spire 2 **v0.111.0** 单人战斗决策研究：CLI 公共状态采集、影子模拟、Expectimax 教师、数据审计，以及学习模型实验。

本仓库保存模型与数据管线；游戏模组与 `STS2BestChoice.Core` 在独立的同级仓库中。

## 从这里开始

| 入口 | 内容 |
|---|---|
| [当前状态](docs/STATUS.md) | 当前工作边界、B/C/D 停止状态与证据限制 |
| [文档导航](docs/README.md) | 计划、研究、想法、历史交接与验证报告 |
| [开发与验证](docs/DEVELOPMENT.md) | 环境、同级 Core 依赖、构建和测试命令 |
| [历史资料](docs/archive/README.md) | 已结束阶段的计划与交接记录，保留原文 |

**当前不等于训练就绪。** 仓库包含旧阶段的模型和验证产物；新版 NOSL 的完整语义、跨对象集成和训练资格尚未闭合。B（Power）、C（遗物）、D（药水）专项已于 2026-10-01 停止并在本地归档。本次文档整理不合入这些副本、不启动训练。

## 目录

```text
training/              数据转换、教师、ShadowDiff、训练与验证工具
sts2-cli-v0111/         锁定版本的真实引擎无头 CLI
sts2-seed-gui/          种子与标注 GUI
data/                 目录、manifest、质量门禁、精选历史产物
docs/                 当前状态、开发说明和分类文档
```

- [TeacherEvaluator](training/TeacherEvaluator/) 与 [ShadowDiff](training/ShadowDiff/)：引用外部 Core 的 C# 入口。
- [公共观测契约](training/schemas/README.md)：输入、轨迹与数据格式。
- [CLI 使用说明](sts2-cli-v0111/README.md)；[GUI 使用说明](sts2-seed-gui/README.md)。

## 版本与依赖

| 项目 | 锁定值 |
|---|---|
| 游戏 | `v0.111.0`，commit `41cef1ea` |
| 原版 `sts2.dll` SHA-256 | `0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9` |
| CLI protocol / trace schema | `0.2.0` / `1` |
| C# | .NET 9 |
| Python 训练环境 | Python 3.12；依赖见 [requirements-training.txt](training/requirements-training.txt) |

所需目录布局：

```text
<父目录>/
├── STS2BestChoice/
│   └── STS2BestChoice.Core/STS2BestChoice.Core.csproj
└── STS2SuperModel/
    └── training/TeacherEvaluator/
```

只检出本仓库不会自动获得 Core 或游戏运行库。部分 Python 工具可独立运行；调用 evaluator、ShadowDiff 或真实 CLI 的工具仍依赖相应程序集。游戏和第三方运行库按 [开发说明](docs/DEVELOPMENT.md) 在本地准备，不随本仓库发布。

## 数据与证据

- 目录、manifest、差分、覆盖率和质量门禁构成审计链；入口见 [文档导航](docs/README.md#数据与验证证据)。
- 大型数据集、临时 trace、缓存与本机运行库默认不入库；既有已跟踪数据保持原样。
- 历史模型交付物有明确忽略规则例外。具体规则以 [.gitignore](.gitignore) 为准，不把所有 `.pt` / `.onnx` 一概排除。
- manifest 可记录生成参数和来源，但**并不保证仅凭公开仓库即可重建所有历史数据**。
- `Estimated`、`BudgetBound`、单项零差分和旧批次门禁不等于新版总体 `Reliable` 或训练放行。

## 计划入口

[工程基线](PLAN.md)、[NOSL 教师基线](PLAN_NOSL.md)、[遗物/卡牌缺口基线](RELIC_CARD_GAP_COMPLETION_PLAN.md) 保留原路径，便于已有引用继续定位；执行状态以 [当前状态](docs/STATUS.md) 为准。其他历史计划和研究材料已分类到 `docs/`。
