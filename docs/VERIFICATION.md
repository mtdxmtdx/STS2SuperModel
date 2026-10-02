> 历史基线记录：本文的11卡等数量属于 d01c5f8e 的原始 M0–M2 检查点。当前上游接入与验证请看 [COVERAGE_BRIDGE](../docs/COVERAGE_BRIDGE.md) 和 coverage_manifest.json；不要把历史数量视为现有目录限制。

# 验证记录（2026-10-01）

## 原 M0–M2 实施证据

- 固定上游原始源码：构建成功，45/45 项相关基线测试。
- NOSL 集成：31/31；核心定向回归：93/93。
- 根候选的完整终局分布在隐藏 seed/牌序替换后逐项保持一致。
- 两进程 JSONL：2 次决策获胜，终局 70 HP，奖励选择 0 次，过期 token 被拒绝。
- 另一份回滚副本恢复 6 个原引擎文件后，原 45 项测试通过。原始实施目录仍保留实现。

这批证据只覆盖 [状态说明](STATUS.md) 的有限范围；不是全内容验收或客户端对拍。原始日志及本机路径保留在本地归档，不提交。

## 本次整理副本实际复验

复制为独立 main 工作副本后，在根目录运行：

```powershell
dotnet build ./Nosl.M012.sln -c Release -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --nologo
dotnet test ./tests/Nosl.Tests/Nosl.Tests.csproj -c Release --no-build -m:1 -nr:false --nologo
python -B ./tools/protocol_smoke.py
```

结果：构建成功（退出码 0）；集成 31 passed、0 failed（退出码 0）；协议烟测（退出码 0）：

```text
PROTOCOL PASS reset=real_silent_a10 sample=public_invariant stale=rejected decisions=2 result=win final_hp=70 reward_choices=0 policy=separate_process
```

源码未改规则逻辑；调整的是目录、使用命令、文档导航、协议 trace 输出目录和发布过滤。所有 JSON 解析通过，当前文档本地相对链接已核对；Git 暂存区没有构建产物、原始日志或本机工作路径。

M3–M8 未运行；V4 原规格的 30 项合成偏好测试保留为历史输入，不追加宣称它们属于真实教师验收。
