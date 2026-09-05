# Line D 完成交付：药水语义 + 遗物 Hook

## 版本锁

```text
Game: v0.111.0 / commit 41cef1ea
sts2.dll SHA-256: 0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9
CLI protocol: 0.2.0
Trace schema: 1
Feature schema: combat-feature-v1 / 1
```

## 交付提交

- 模型/CLI/ShadowDiff/数据仓库：`04dab48` (`feat: close line D potion and relic semantics`)
- 外部影子模拟器 Core 仓库：`e9b1395` (`feat: implement potion and relic semantic hooks`)
- Core 仓库当前没有配置 `origin`；模型仓库的远端是 `https://github.com/mtdxmtdx/STS2SuperModel.git`。

## D1 药水

- 28 个确定性药水由 `PotionSemanticCatalog` 映射到运行时 `DynamicVars`/`EffectSpec`，随机或未验证对象保持非 Reliable 终态。
- 药水覆盖：Reliable eligible `2 → 28`；严格差分 28/28，0 mismatch。
- 采集器支持 `--potions` / `--potion-sets`，公共快照包含药水槽位计数；特征契约未变更。
- `holdout-potion-v1` 已冻结并重复验证。

## D2 遗物

20 个目标的终态见 `data/relics/v0.111/D2_RELIC_VERIFICATION.md`：

- Reliable：`DIVINE_RIGHT`, `DATA_DISK`, `GORGET`, `EMBER_TEA`, `SWORD_OF_JADE`, `SLING_OF_COURAGE`, `GREMLIN_HORN`, `BOOK_REPAIR_KNIFE`, `LIZARD_TAIL`, `RUNIC_PYRAMID`, `RINGING_TRIANGLE`, `PAPER_PHROG`, `PAPER_KRANE`, `THE_ABACUS`（14）。
- UnsupportedWithReason：`STONE_CRACKER`, `GIRYA`, `BOOKMARK`, `BIIIG_HUG`, `GALACTIC_DUST`, `HISTORY_COURSE`（6）。
- 全目录 Reliable eligible：`24 → 38`。
- `affects_current_turn`：20/20 非空。
- 当前 semantic hold：`PARRYING_SHIELD`；`UNCEASING_TOP` 已由空手 fixture 解除。
- 非起始遗物数量 ≥2 的 Reliable 比例：`15811 / 49414 = 31.997%`；角色分布 `52.323% / 47.677%`。
- `holdout-relic-v1` 已冻结，重复 SHA：`B7018CB52EF95807403575B843BC0D234A717CEDAF0744186F900DBE7A4563C2`。

## NOSL 数据

- 合并唯一状态：`67,799`。
- Reliable：`49,414`；Estimated：`18,369`；Uncalculable：`16`。
- 质量门禁：`pass`，0 public leak、0 stable-ID 缺失、0 malformed、0 冲突状态。
- 数据来源 SHA-256：`25B50DCDB1FCBD253E124AD3329FCBE730613E13F1919B0AB85656591FB21796`。

## 验证

```text
dotnet build STS2BestChoice.csproj -c Release -p:Sts2Dir=...  -> 0 errors
dotnet test tests/STS2BestChoice.Tests.csproj -c Release --no-restore -> 808 passed, 0 failed
python -m pytest training -q --disable-warnings --ignore=training/test_replay_action.py -> 148 passed, 1 skipped
python training/verify_repeat_runs.py -> 226 reports, different=0, missing=0, added=0
```

D2 反向测试故意改变 Paper Phrog 倍率后，ShadowDiff 输出 1 failed report、`enemy.enemy:TERROR_EEL:1.hp` mismatch；恢复后直接 fixture 回到 mismatch=0。回滚副本验证：`hash_mismatches=0`、`created_left=0`、`py_compile exit=0`。

四项事务产物：

```text
D:\STS2BestChoice\work\line-d-d2-relics\MODIFIED_FILE
D:\STS2BestChoice\work\line-d-d2-relics\DIFF_FILE.patch
D:\STS2BestChoice\work\line-d-d2-relics\VERIFICATION.txt
D:\STS2BestChoice\work\line-d-d2-relics\ROLLBACK.sh
```

## 远端状态

模型仓库已成功推送到 `origin/main`，远端包含 `04dab48` 与 `4b01ce2`。Core 仓库没有配置 `origin`，需要指定其目标 GitHub 仓库后才能推送。
