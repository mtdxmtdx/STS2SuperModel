# STS2 Seed/Run GUI

本目录提供一个轻量的本地人工标注工具，把 seed、CLI 状态和 `.run` 历史整理为结构化的局外决策数据。

### 来源 ID与标注者 ID

- **来源 ID（source ID）**：标识数据来自哪一局或哪一批记录，例如 `.run` 文件名、`seed+局号` 或 `run-20260831-001`。它不是动作 ID，也不会改变游戏状态。
- **标注者 ID（annotator ID）**：标识实际录入、核对这条记录的人或程序，例如 `panyitong` 或 `agent-global-01`。同一批数据应保持一致，便于追溯和复核。

## 当前能力

- 输入固定 RunContext 和 seed；
- 启动 `sts2-cli-v0111`，读取当前地图和 public state；
- 显示地图节点和路线；
- 创建会话后自动读取首个 Act 的 SVG 地图；左键高亮节点，右键在节点旁打开填写窗；
- 节点填写窗支持生命、金币、卡牌、遗物、药水的获得/失去；每一项均可搜索、下拉选择并通过“＋添加”增加多行；失去项来自当前已知资源，获得项来自完整版本目录；
- 目录接口提供 v0.111.0 的 607 条卡牌、299 条遗物和 69 条药水选项，界面显示中文名称并保存稳定 ID；
- 服务端校验路线合法性：禁止回头、重复节点、跨 Act 和跳层；羽翼之靴最多 3 次且只能选下一层节点；
- 已记录的路线节点使用绿色高亮；地图额外显示合成的“先古之民”起点和首领节点；`RestSite` 显示为“篝火”，`Unknown` 显示为“问号”。
- 起始路线门禁以“先古之民”作为第 0 层起点，因此第一层所有合法节点都可以正常选择。
- 手动录入路线、奖励、商店、篝火、事件和 Ancient 决策；
- 保存 SQLite 草稿、checkpoint 和只读分支；
- 导入 `.run` 的已发生路线与聚合结果；
- 导出 JSONL、Manifest、Reliable 子集和分组切分数据；
- 执行 public 状态校验和状态差异检查；
- 通过显式概率树或外部 provider 运行 Expectimax 教师搜索；
- 批量物化全局教师记录。

## 启动

在 PowerShell 中：

```powershell
cd D:\STS2BestChoice\STS2SuperModel\sts2-seed-gui
python .\app.py --port 8765
```

浏览器打开 <http://127.0.0.1:8765/>。

默认 CLI 路径：

```text
..\sts2-cli-v0111\src\Sts2Headless\bin\Debug\net9.0\Sts2Headless.exe
```

也可以在页面中填写 CLI 可执行文件路径。运行真实 CLI 前，需要按上游 CLI 文档准备本地游戏 DLL。

## 数据输出

默认写入：

```text
data\annotation.sqlite
data\global_behavior.jsonl
data\global_behavior.manifest.json
```

记录保留 RunContext、来源、pre/post state hash、动作来源、SL 状态和质量等级。

## API

```text
GET  /api/health
GET  /api/catalogs
GET  /api/sessions
POST /api/sessions
GET  /api/sessions/{id}
POST /api/sessions/{id}/refresh-map
GET  /api/sessions/{id}/route-state
POST /api/sessions/{id}/route-validate
POST /api/sessions/{id}/route-select
POST /api/sessions/{id}/decisions
POST /api/sessions/{id}/operations
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

## 数据工具

按 run/episode/seed/branch 稳定分组切分：

```powershell
python .\dataset_export.py .\data\global_behavior.jsonl .\data\splits
```

在已安装 PyArrow 的训练环境中转换 Parquet：

```powershell
python .\parquet_export.py .\data\global_behavior.jsonl .\data\global_behavior.parquet
```

批量生成教师标签：

```powershell
python .\teacher_batch.py .\data\teacher_inputs.jsonl .\data\global_teacher.jsonl
```

外部概率 provider 使用 JSONL 协议：

```text
request:  {"cmd":"enumerate_outcomes", "state":{}, "action":{}, "depth":N}
response: {"type":"outcomes", "outcomes":[{"probability":0.7,"next_node":{...}}]}
```

概率必须显式提供且总和为 1；GUI 不自行猜测随机分布。

## 数据边界

- seed 只用于固定上下文重建、重放和切分，不作为模型特征；
- `.run` 只提供已经发生的路线和聚合结果，缺失字段保持 unknown；
- public state 不写 raw RNG、未来节点内容或 teacher-only 字段；
- 人工行为、启发式教师和 CounterfactualTeacher 分开保存；
- 只有 `ExactPublic + verified_no_sl` 记录进入 Reliable 导出；
- 未知效果、缺失合法动作集和无法重放的记录保留为辅助数据。

## 目录

- `app.py`：HTTP API、会话、checkpoint、teacher 路由；
- `models.py`：RunContext、DecisionRecord；
- `cli_session.py`：CLI JSONL 会话；
- `storage.py`：SQLite、JSONL、Manifest、质量导出；
- `teacher_search.py` / `provider_tree.py`：Expectimax 和概率 provider；
- `dataset_export.py` / `parquet_export.py` / `teacher_batch.py`：数据出口；
- `static/`：前端；`tests/`：定向回归测试。
