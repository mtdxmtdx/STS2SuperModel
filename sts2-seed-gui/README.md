# STS2 Seed/Run GUI

本目录提供一个轻量的本地人工标注工具，用于把高手视频中的局外决策录入为结构化数据。

## 当前 MVP

- 不播放或解析视频；用户在外部观看视频并手动填写选择；
- 输入固定 RunContext 和 seed；
- 启动现有 `sts2-cli-v0111`，读取 `get_map` 和当前 JSON 状态；
- 显示地图节点和路线；
- 录入路线、奖励、商店、篝火、事件和 Ancient 决策；
- 保存 SQLite 草稿；
- 导出 `global_behavior.jsonl`；
- 导出 JSONL 旁车 `global_behavior.manifest.json`（行数、SHA-256、质量和 SL 统计）；
- 提供会话级数据校验（seed/context、动作合法性、public view 泄漏）；
- 提供 `dataset_export.py`，按 run/episode/seed/branch 稳定分组切分 train/validation/test JSONL；
- 支持导入 `.run` 历史文件的基础聚合字段；
- CLI 地图已加载时，对 `.run` 楼层节点类型执行基础对齐并报告 mismatch_count；
- 默认不执行人工动作，避免把视频中的专家决策误执行到 CLI。

## 启动

在 PowerShell 中：

```powershell
cd D:\STS2BestChoice\STS2SuperModel\sts2-seed-gui
python .\app.py --port 8765
```

浏览器打开 <http://127.0.0.1:8765/>。

默认 CLI 路径会尝试查找：

```text
..\sts2-cli-v0111\src\Sts2Headless\bin\Debug\net9.0\Sts2Headless.exe
```

也可以在页面中填写 CLI 可执行文件路径。运行真实 CLI 前，需要按上游 CLI 文档准备本地游戏 DLL；这些文件不由本 GUI 保存或上传。

## 数据输出

默认写入：

```text
data\annotation.sqlite
data\global_behavior.jsonl
data\global_behavior.manifest.json
```

每条记录保留 `run_context_hash`、来源、视频时间戳、`pre/post_state_hash`、动作来源、SL 状态和质量等级。视频本体不写入项目。

## API

```text
GET  /api/health
GET  /api/sessions
POST /api/sessions
GET  /api/sessions/{id}
POST /api/sessions/{id}/refresh-map
POST /api/sessions/{id}/decisions
POST /api/sessions/{id}/checkpoints
POST /api/sessions/{id}/restore-checkpoint
POST /api/sessions/{id}/branches
POST /api/sessions/{id}/import-run
POST /api/sessions/{id}/export
POST /api/sessions/{id}/validate
POST /api/sessions/{id}/diff
POST /api/sessions/{id}/teacher-records
POST /api/sessions/{id}/teacher-evaluate
POST /api/sessions/{id}/teacher-search
POST /api/sessions/{id}/teacher-cli-tree
POST /api/sessions/{id}/teacher-provider-tree
POST /api/sessions/{id}/teacher-export
POST /api/sessions/{id}/export-reliable
```

分组切分：

```powershell
python .\dataset_export.py .\data\global_behavior.jsonl .\data\splits
```

脚本生成三个 split JSONL 和 `global_behavior.split.manifest.json`，并报告组重叠数量；Parquet 转换在训练环境中执行。

在已安装 PyArrow 的训练环境中转换 Parquet：

```powershell
python .\parquet_export.py .\data\global_behavior.jsonl .\data\global_behavior.parquet
```

会同时生成 `global_behavior.manifest.json`，并执行 Parquet 读回行数校验。

全局教师结果通过独立接口写入，必须引用已有 checkpoint，不会修改人工行为记录：

`teacher-evaluate` 可在带有 CLI save 的 checkpoint 上对一组动作执行一次分支评估；当前使用保守的一步启发式分数，输出标记为 `EstimatedByHeuristic`，用于替换为正式全局 evaluator 前的接口验证。

`teacher-cli-tree` 在带有 CLI save 的 checkpoint 上逐个回放候选动作，记录每个确定性 post-state（概率 1.0），并可设置 `depth=1..4`；它不会猜测隐藏随机结果。

`teacher-provider-tree` 连接外部影子/语义提供器（JSONL 协议 `enumerate_outcomes`），由提供器返回显式概率的隐藏分支，再交给同一套 Expectimax。提供器响应概率必须和为 1。

`teacher-search` 接受显式概率的有限分支树，运行深度限制 Expectimax；概率必须由上游 CLI/模拟器提供，不按分支数量推断均匀分布。
也可以传入 `cli_response` 与 `outcomes_by_action`，服务会从 CLI 的公开 `choices/options/cards` 生成稳定 action ID，再运行相同搜索。
地图、事件、篝火、奖励和商店候选会同时生成可直接交给 v0.111 CLI 的 `cli_action` 与参数。

`export-reliable` 只导出 `label_quality=ExactPublic` 且 `sl_status=verified_no_sl` 的记录，其他记录只在 manifest 中统计为排除项。

批量生成教师标签（输入由 CLI/影子模拟器准备的概率树 JSONL）：

```powershell
python .\teacher_batch.py .\data\teacher_inputs.jsonl .\data\global_teacher.jsonl
```

```json
{
  "parent_checkpoint_id": "checkpoint-…",
  "action_values": [{"action_id": "map:1:0", "value": 0.82}],
  "teacher_best_actions": ["map:1:0"],
  "teacher_value": 0.82
}
```

## 设计边界

- seed 只用于固定上下文下的地图重建，不作为模型特征；
- CLI 返回的 teacher/audit 字段不会进入 `public_state`；
- 没有战斗逐动作视频时，允许 `partial_episode` 和 `CombatSummary` 跳转；
- 人工选择标记为 `human_expert_observed`，不自动标记为最优教师标签；
- `sl_status=unknown` 的记录不会进入 Reliable NOSL 主集。
