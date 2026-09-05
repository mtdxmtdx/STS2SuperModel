#!/usr/bin/env python3
"""Build the v0.111 potion audit from the runtime registry and local evidence."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from collections import Counter
from pathlib import Path
from typing import Any, Iterable


ROOT = Path(__file__).resolve().parents[1]
WORKSPACE = ROOT.parent
VERSION = {
    "game_version": "v0.111.0",
    "game_commit": "41cef1ea",
    "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
    "cli_protocol_version": "0.2.0",
    "trace_schema": 1,
}
POTION_ACTION_RE = re.compile(r"(?:^|:)potion(?:-runtime)?:([A-Z0-9_]+)(?::|$)", re.IGNORECASE)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--runtime-catalog",
        type=Path,
        default=ROOT / "data" / "potions" / "v0.111" / "potion-runtime-catalog.json",
    )
    parser.add_argument(
        "--adapter",
        type=Path,
        default=WORKSPACE / "STS2BestChoice" / "Mod" / "LiveCombatSnapshotAdapter.cs",
    )
    parser.add_argument("--evidence-dir", type=Path, default=ROOT / "data")
    parser.add_argument(
        "--localization-eng",
        type=Path,
        default=ROOT / "sts2-cli-v0111" / "localization_eng" / "potions.json",
    )
    parser.add_argument(
        "--localization-zhs",
        type=Path,
        default=ROOT / "sts2-cli-v0111" / "localization_zhs" / "potions.json",
    )
    parser.add_argument(
        "--output-json",
        type=Path,
        default=ROOT / "data" / "potions" / "v0.111" / "potion-coverage.json",
    )
    parser.add_argument(
        "--output-md",
        type=Path,
        default=ROOT / "data" / "potions" / "v0.111" / "potion-coverage.md",
    )
    return parser.parse_args()


def load_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def implemented_potions(runtime_catalog: dict[str, Any]) -> set[str]:
    return {
        row["potion_id"]
        for row in runtime_catalog["potions"]
        if row.get("simulator_supported") is True
    }


def extract_potion_id(action_id: Any) -> str | None:
    if not isinstance(action_id, str):
        return None
    match = POTION_ACTION_RE.search(action_id.upper())
    return match.group(1) if match else None


def version_matches(row: dict[str, Any]) -> bool:
    return all(row.get(key) == value for key, value in VERSION.items())


def evidence_index(directory: Path) -> tuple[dict[str, list[str]], dict[str, list[str]]]:
    traces: dict[str, set[str]] = {}
    strict: dict[str, set[str]] = {}
    for path in sorted(directory.glob("*.json")):
        try:
            row = load_json(path)
        except (json.JSONDecodeError, UnicodeDecodeError):
            continue
        if not isinstance(row, dict) or str(row.get("action_kind") or "").lower() != "use_potion":
            continue
        potion_id = extract_potion_id(row.get("normalized_action_id"))
        if potion_id is None:
            continue
        traces.setdefault(potion_id, set()).add(path.name)
        if (
            version_matches(row)
            and row.get("confidence") == "Reliable"
            and row.get("match") is True
            and row.get("mismatch_count") == 0
            and row.get("comparison_scope") == "strict_public_state"
        ):
            strict.setdefault(potion_id, set()).add(path.name)

    for path in sorted(directory.glob("*.jsonl")):
        try:
            with path.open(encoding="utf-8") as stream:
                for line in stream:
                    if not line.strip():
                        continue
                    row = json.loads(line)
                    potion_id = extract_potion_id(row.get("normalized_action_id"))
                    if potion_id is not None and row.get("status") == "ok" and version_matches(row):
                        traces.setdefault(potion_id, set()).add(path.name)
        except (json.JSONDecodeError, UnicodeDecodeError):
            continue
    return (
        {key: sorted(values) for key, values in traces.items()},
        {key: sorted(values) for key, values in strict.items()},
    )


def localization_value(values: dict[str, str], potion_id: str, suffix: str, fallback: str) -> str:
    value = values.get(f"{potion_id}.{suffix}")
    return value if isinstance(value, str) and value.strip() else fallback


def build_rows(
    raw: dict[str, Any],
    implemented: set[str],
    trace_evidence: dict[str, list[str]],
    strict_evidence: dict[str, list[str]],
    eng: dict[str, str],
    zhs: dict[str, str],
) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    for item in raw["potions"]:
        potion_id = item["potion_id"]
        deprecated = potion_id == "DEPRECATED_POTION"
        test_only = potion_id == "MOCK_DISCARD_AND_ADD_SHIVS_POTION"
        out_of_scope = deprecated or test_only
        simulator_supported = potion_id in implemented
        reports = strict_evidence.get(potion_id, [])
        traces = trace_evidence.get(potion_id, [])
        reliable = simulator_supported and bool(reports)
        methods = item.get("declared_methods") or []
        il_inspected = any(method.get("has_il") and method.get("il_sha256") for method in methods)

        if out_of_scope:
            support_status = "OutOfScope"
            blocking_reason = "deprecated_or_test_only_registry_entry"
            next_action = "None"
        elif simulator_supported:
            support_status = "SimulatorSupported"
            blocking_reason = None if reliable else "strict_runtime_evidence_missing"
            next_action = "None" if reliable else "Add version-locked CLI ShadowDiff fixture"
        else:
            support_status = "UnsupportedKnownEffect"
            blocking_reason = "simulator_handler_missing"
            next_action = "Implement simulator handler, then add version-locked CLI ShadowDiff fixture"

        if reports:
            evidence_level = "LiveObserved"
            evidence_reference = "; ".join(reports)
        elif traces:
            evidence_level = "TraceObserved"
            evidence_reference = "; ".join(traces)
        elif il_inspected:
            evidence_level = "IlInspected"
            evidence_reference = "potion-runtime-catalog.json"
        else:
            evidence_level = "RegistryObserved"
            evidence_reference = "potion-runtime-catalog.json"

        fallback_name = potion_id.replace("_", " ").title()
        rows.append(
            {
                "potion_id": potion_id,
                "canonical_name": localization_value(eng, potion_id, "title", fallback_name),
                "localized_name_zh": localization_value(zhs, potion_id, "title", fallback_name),
                "rarity": item.get("rarity") or "Unknown",
                "runtime_type": item.get("runtime_type"),
                "target_type": item.get("target_type") or "Unknown",
                "dynamic_var_names": item.get("dynamic_var_names") or [],
                "description": localization_value(eng, potion_id, "description", ""),
                "description_zh": localization_value(zhs, potion_id, "description", ""),
                "cataloged": True,
                "structured": True,
                "state_captured": True,
                "il_inspected": il_inspected,
                "runtime_probed": bool(traces),
                "evidence_level": evidence_level,
                "evidence_reference": evidence_reference,
                "simulator_supported": simulator_supported,
                "support_status": support_status,
                "blocking_reason": blocking_reason,
                "next_action": next_action,
                "combat_relevance": "OutOfScope" if out_of_scope else ("CombatPassive" if potion_id == "FAIRY_IN_A_BOTTLE" else "CurrentTurn"),
                "affects_current_turn": not out_of_scope,
                "reliable_eligible": reliable,
            }
        )
    return rows


def count(rows: Iterable[dict[str, Any]], key: str) -> int:
    return sum(bool(row.get(key)) for row in rows)


def render_markdown(document: dict[str, Any]) -> str:
    summary = document["summary"]
    lines = [
        "# v0.111 药水覆盖率审计",
        "",
        "> 分母来自 `MegaCrit.Sts2.Core.Models.ModelDb.AllPotions`；本报告只审计，不实现药水语义。",
        "",
        "## 汇总",
        "",
        "| 指标 | 数量 |",
        "|---|---:|",
        f"| 游戏目录药水总数 | {summary['total_potions']} |",
        f"| 已结构化 | {summary['structured_count']} |",
        f"| 已捕获状态 | {summary['state_captured_count']} |",
        f"| 已检查 IL | {summary['il_inspected_count']} |",
        f"| 已完成真实引擎观测 | {summary['runtime_probed_count']} |",
        f"| 严格证据合格 | {summary['reliable_eligible_count']} |",
        f"| 模拟器声明支持 | {summary['simulator_supported_count']} |",
        f"| 已知不支持 | {summary['unsupported_known_count']} |",
        f"| OutOfScope | {summary['out_of_scope_count']} |",
        f"| Unknown | {summary['unknown_count']} |",
        "",
        "## 逐项",
        "",
        "| ID | 名称 | 稀有度 | IL | 探针 | 模拟器 | 严格证据 | 状态 | 下一步 |",
        "|---|---|---|---:|---:|---:|---:|---|---|",
    ]
    for row in document["potions"]:
        lines.append(
            f"| `{row['potion_id']}` | {row['localized_name_zh']} | {row['rarity']} | "
            f"{'✓' if row['il_inspected'] else '—'} | {'✓' if row['runtime_probed'] else '—'} | "
            f"{'✓' if row['simulator_supported'] else '—'} | {'✓' if row['reliable_eligible'] else '—'} | "
            f"{row['support_status']} | {row['next_action']} |"
        )
    lines += [
        "",
        "## 来源与边界",
        "",
        "- 权威分母：游戏程序集注册表 `ModelDb.AllPotions`。",
        "- 名称与说明：v0.111 本地化文件，仅作可读交叉引用，不决定语义或数值。",
        "- IL：目录记录每个声明方法的 IL SHA-256；无实现体的废弃条目不计为已检查。",
        "- 严格证据：版本锁匹配、`strict_public_state`、`Reliable`、`match=true`、`mismatch_count=0`。",
        "- `structured` 表示已有逐药水审计结构；`reliable_eligible` 表示可进入 Reliable NOSL 数据，两者不得混用。",
        "",
    ]
    return "\n".join(lines)


def main() -> int:
    args = parse_args()
    raw = load_json(args.runtime_catalog)
    if raw.get("assembly_sha256") != VERSION["assembly_sha256"]:
        raise ValueError("runtime catalog assembly SHA-256 does not match the v0.111 lock")
    if raw.get("total_potions") != len(raw.get("potions") or []):
        raise ValueError("runtime catalog total does not match row count")
    ids = [row["potion_id"] for row in raw["potions"]]
    if len(ids) != len(set(ids)):
        raise ValueError("runtime catalog contains duplicate potion IDs")

    implemented = implemented_potions(raw)
    if not implemented <= set(ids):
        raise ValueError(f"adapter references IDs outside runtime registry: {sorted(implemented - set(ids))}")
    traces, strict = evidence_index(args.evidence_dir)
    rows = build_rows(raw, implemented, traces, strict, load_json(args.localization_eng), load_json(args.localization_zhs))
    status_counts = Counter(row["support_status"] for row in rows)
    summary = {
        "total_potions": len(rows),
        "cataloged_count": count(rows, "cataloged"),
        "structured_count": count(rows, "structured"),
        "state_captured_count": count(rows, "state_captured"),
        "il_inspected_count": count(rows, "il_inspected"),
        "runtime_probed_count": count(rows, "runtime_probed"),
        "reliable_eligible_count": count(rows, "reliable_eligible"),
        "simulator_supported_count": count(rows, "simulator_supported"),
        "unsupported_known_count": status_counts["UnsupportedKnownEffect"],
        "out_of_scope_count": status_counts["OutOfScope"],
        "unknown_count": status_counts["Unknown"],
        "support_status_counts": dict(sorted(status_counts.items())),
    }
    if summary["unknown_count"] != 0:
        raise ValueError("unknown_count must be zero")
    if any(not row["next_action"] for row in rows):
        raise ValueError("every potion must have an explicit next_action")

    document = {
        "schema_version": 1,
        "catalog_version": "potion-coverage-v0.111",
        "source": {
            "authoritative_registry": raw["source"],
            "runtime_catalog": args.runtime_catalog.name,
            "runtime_catalog_sha256": sha256(args.runtime_catalog),
            "adapter": str(args.adapter),
            "localization_role": "human-readable cross-reference only",
        },
        "version_lock": VERSION,
        "summary": summary,
        "potions": rows,
    }
    args.output_json.parent.mkdir(parents=True, exist_ok=True)
    args.output_json.write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    args.output_md.write_text(render_markdown(document), encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
