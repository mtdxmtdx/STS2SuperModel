# STS2 Seed/Run GUI Agent 入口

## 项目定位

本目录是本地人工标注 GUI，用于把高手视频中的局外决策转换为结构化
`global_behavior` 数据，并提供 checkpoint、分支和全局教师树接口。
不播放或识别视频，不替代战斗模组或影子模拟器。

## 运行

```powershell
cd D:\STS2BestChoice\STS2SuperModel\sts2-seed-gui
python .\app.py --port 8765
```

浏览器访问 `http://127.0.0.1:8765/`。默认对接 sibling
`sts2-cli-v0111`；也可在界面填写 CLI 路径。

## 技术与目录

- Python 标准库 HTTP/SQLite 后端；静态 HTML/CSS/JavaScript 前端；
- `models.py`：RunContext、DecisionRecord；
- `app.py`：API、会话、checkpoint、teacher 路由；
- `storage.py`：SQLite、JSONL、Manifest、Reliable 导出；
- `teacher_search.py` / `provider_tree.py`：Expectimax 与概率 provider；
- `dataset_export.py` / `parquet_export.py` / `teacher_batch.py`：数据出口；
- `static/`：GUI；`tests/`：最小回归测试。

## 数据边界

- seed 只重建可见上下文，不作为模型特征；
- public state 不写 raw RNG、未来内容或 teacher-only 字段；
- 人工行为、启发式教师、CounterfactualTeacher 分开存储；
- `ExactPublic + verified_no_sl` 才能进入 Reliable 导出。

## 当前状态

GUI 计划 G0–G6b 已完成。影子模拟器的具体 `enumerate_outcomes` 实现由
`STS2BestChoice.Core` 或外部 provider 提供；接入时必须提供显式概率并保持
版本/RunContext 一致。定向测试命令：
`python -W error::ResourceWarning -m unittest discover -s tests -v`。
