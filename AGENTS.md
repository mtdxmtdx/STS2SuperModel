# STS2SuperModel Agent 入口

## 范围与阅读顺序

本仓库实现战斗教师、数据及训练研究，不包含主模组运行时代码。开始工作先读：

1. [README.md](README.md)：用途、依赖和目录。
2. [docs/STATUS.md](docs/STATUS.md)：当前状态与已停止事项。
3. [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)：本次需要的构建及验证入口。
4. [docs/README.md](docs/README.md)：按任务定位基线设计和历史证据。

## 版本与接口

- Game `v0.111.0` / commit `41cef1ea`；CLI protocol `0.2.0` / trace schema `1`。
- 教师采集入口：`training/collectors/teacher_worker.py`。
- C# evaluator：`training/TeacherEvaluator/`；依赖同级 `STS2BestChoice` 仓库中的纯 Core。
- 只使用当前请求明确涉及的仓库；不因同级依赖存在就扩展修改范围。

## 执行与证据

- 保留已有未提交改动；按本次任务精确选择提交文件，不把本地草稿、数据或整个 `docs/` 顺手加入。
- 旧计划、交接文件和测试数量是历史记录，不是当前通过证明。按 `docs/STATUS.md` 区分现行状态、基线设计与归档。
- B/C/D 语义工作包已停止归档；不从旧提示词自动恢复。候选新后端的评估不等于已经接入。
- 未知语义、缺失概率或 evaluator 回退保持 `Estimated` / `Uncalculable`；单项零差不授予总体训练资格。
- 数据与模型入库规则遵守 `.gitignore` 的现有例外；未读取输入或未运行门禁时不宣称可重建或验收通过。
- 源码变更运行对应测试；纯文档整理验证导航和提交范围即可。关键里程碑更新当前状态，不把临时流水账写进本文件。
