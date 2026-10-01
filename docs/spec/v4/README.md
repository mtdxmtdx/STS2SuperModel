# STS2 NOSL 整场战斗实施包 V4

2026-10-01 · Silent A10 · 单人战斗内决策 · 独立学生网络

## 从这里开始

- `PLAN_NOSL_FULL_COMBAT_V4.md`：完整路线、M0–M8、职责、交付和通过条件。
- `TEACHER_CONTRACT_V4.md`：公共信息、终局目标、药水9点、额外收益5点/80%、标签与推理接口。
- `AGENT_IMPLEMENTATION_PROMPT_V4.md`：直接交给编码agent，默认做训练准备，不擅自正式训练或发布。
- `config/`：已确认偏好与待校准工程参数分开记录；源码参考基线不是用户客户端版本。
- `acceptance_catalog.json`：50项待接真实组件的验收规范，全部尚未执行。
- `contracts/`、`tests/`：30项已执行的合成偏好契约检查，仅Python标准库。
- `contract_test_report.txt`：本次测试输出。
- `DELIVERY_STATUS.json`：已做与未做事项。
- `SOURCES.md`：源码和研究参考及核对范围。

## 复验局部契约

在本目录执行：

```sh
python -m unittest discover -s tests -v
```

Python 3.10+。本次在Python 3.13.5执行，30项通过。无需模拟器、游戏文件、GPU或额外依赖。

**这不是游戏教师或模型实现。**这些检查只针对合成完整结果分布、候选评分与阈值规则；没有证明真实游戏保真、NOSL隔离或神经网络水平。M0–M6完成以后才可能达到训练就绪；全范围支持有缺口时必须准确标记。

候选风险参数K=1000、eta=0.2的`calibrated`仍为false，不能宣称用户已经选定这组参数。9/5/80%才是当前已确认数值。模型部署不读取seed、隐藏牌序或教师现场评分。

本次没有修改GitHub，没有启动正式训练，没有对拍客户端。硬件和用户实际游戏版本仍待补全，但不阻止开展公开信息、契约与环境适配的前置工作。
