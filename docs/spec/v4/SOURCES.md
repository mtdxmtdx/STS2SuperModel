# 来源与证据边界

## 用户需求

来源为本轮完整对话及《讨论.txt》。该文件确认Silent A10与目标内容；后续用户回复明确：9点可用而非立刻用；5点是整场额外期望战损；80%可接受；允许安全拖回合；选择90%无伤/10%掉30；无限防够后先做其他有益动作。

9/5/80%属于本项目要求，不作为全体玩家共识或游戏规则事实引用。旧计划只保留未被本轮修改的独立模型边界，不从旧文档推断现有代码完成情况。

## 源码（2026-10-01定点核对，未构建或全仓审计）

- R1 main ref: https://api.github.com/repos/mtdxmtdx/sts2-sim/branches/main
  核对sha：a3a66276ee79bf9f75e8af32e9ef5a51592b26f8。
- R2 README: https://github.com/mtdxmtdx/sts2-sim/blob/a3a66276ee79bf9f75e8af32e9ef5a51592b26f8/README.md
  基线v0.111.0、.NET9、随机模式区别、缺失项、整局未深克隆。
- R3 clone: https://github.com/mtdxmtdx/sts2-sim/blob/a3a66276ee79bf9f75e8af32e9ef5a51592b26f8/src/Sts2Sim.Core/Combat/CombatState.Clone.cs
  本轮重读1–65行；当前对话此前也已读取此提交完整内容。
- R4 state description: https://github.com/mtdxmtdx/sts2-sim/blob/a3a66276ee79bf9f75e8af32e9ef5a51592b26f8/src/Sts2Sim.Core/Combat/StateDescription/CombatStateDescription.cs
  本轮重读1–55行；不是公共序列化契约、含seed/RNG。
- R5 CombatRoom: https://github.com/mtdxmtdx/sts2-sim/blob/a3a66276ee79bf9f75e8af32e9ef5a51592b26f8/src/Sts2Sim.Core/Rooms/CombatRoom.cs
  本轮读取1–210、240–435行；生命周期、ResolveVictoryOnceAsync、AfterCombatEnd/AfterCombatVictory。
- R6 RunDriver: https://github.com/mtdxmtdx/sts2-sim/blob/a3a66276ee79bf9f75e8af32e9ef5a51592b26f8/src/Sts2Sim.Core/Runs/RunDriver.cs
  本轮读取1–230行；外部决策与整局结果/截断区分。
- R7 RNG: https://github.com/mtdxmtdx/sts2-sim/blob/a3a66276ee79bf9f75e8af32e9ef5a51592b26f8/src/Sts2Sim.Core/Runs/RunRngSet.cs
  当前对话此前读取同提交内容；CloneExact、CloneReseeded及keyed语义。技术实施仍需按固定checkout核查全部调用链。

容器git下载尝试因网络DNS失败；源码定点核查通过GitHub连接完成。没有本地完整checkout，也未运行.NET构建或测试。不能把本包测试等同于模拟器保真结果。

## 研究方法（本轮打开并核对摘要，不涉及PDF表格分析）

- R8 Silver & Veness (2010), Monte-Carlo Planning in Large POMDPs.
  https://papers.nips.cc/paper/4031-monte-carlo-planning-in-large-pomdps
  支持信念采样＋模拟器规划的路线；不是NOSL适配器自动正确的证明。
- R9 Anthony et al. (2017), Thinking Fast and Slow with Deep Learning and Tree Search.
  https://arxiv.org/abs/1705.08439
  支持搜索规划与网络泛化、迭代改进的分工；不推断STS2效果。
- R10 Ross et al. (2011), A Reduction of Imitation Learning and Structured Prediction to No-Regret Online Learning.
  https://proceedings.mlr.press/v15/ross11a.html
  支持关注学生自身诱导的状态分布及数据聚合。
- R11 Agarwal et al. (2021/2022), Deep Reinforcement Learning at the Edge of the Statistical Precipice.
  https://arxiv.org/abs/2108.13264
  支持报告统计不确定性而不只看平均分。

本包的终局代价公式、具体阶段划分、候选超参数和接口是工程设计提案；不冒称来自以上论文，也不声称其理论保证已适用于此实现。
