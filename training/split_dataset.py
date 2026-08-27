#!/usr/bin/env python3
"""Deterministically split normalized training JSONL by episode/trace group.

Dataset-level gates enforced here:

- an episode (or trace group) never spans train/validation/test/challenge;
- rows that share a public_state_hash (i.e. the same state linked to teacher
  payloads) are merged into one group before assignment so identical states
  cannot be separated across main collections;
- mixed game/CLI/schema/generator metadata rejects the whole dataset instead
  of silently combining incompatible traces;
- a missing generator_config_hash is rejected.

``verify_split_dir`` re-checks written split trees independently, including
cross-split episodes/states and manifest digests.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from collections import defaultdict
from pathlib import Path


SPLITS = ("train", "validation", "test", "challenge")
VERSION_KEYS = (
    "game_version",
    "game_commit",
    "assembly_sha256",
    "cli_protocol_version",
    "simulator_version",
    "semantic_database_version",
    "scorer_version",
    "feature_schema_version",
    "model_version",
    "generator_config_hash",
)


def digest(path: Path) -> str:
    hasher = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            hasher.update(block)
    return hasher.hexdigest().upper()


def split_for(group: str) -> str:
    bucket = int.from_bytes(hashlib.sha256(group.encode("utf-8")).digest()[:4], "big") % 100
    if bucket < 10:
        return "challenge"
    if bucket < 30:
        return "test"
    if bucket < 50:
        return "validation"
    return "train"


def _group_key(row: dict, line_no: int, input_path: Path) -> str:
    return str(row.get("episode_id") or row.get("trace_id") or row.get("provenance", {}).get("trace_id") or f"row-{line_no}")


def _state_hashes(row: dict) -> set[str]:
    hashes: set[str] = set()
    for key in ("state_hash_public", "public_state_hash", "post_state_hash"):
        value = row.get(key)
        if value:
            hashes.add(str(value))
    return hashes


class _UnionFind:
    def __init__(self) -> None:
        self.parent: dict[str, str] = {}

    def find(self, item: str) -> str:
        self.parent.setdefault(item, item)
        root = item
        while self.parent[root] != root:
            root = self.parent[root]
        while self.parent[item] != root:
            self.parent[item], item = root, self.parent[item]
        return root

    def union(self, left: str, right: str) -> None:
        left_root, right_root = self.find(left), self.find(right)
        if left_root == right_root:
            return
        # Deterministic canonical representative: lexicographically smaller id.
        if right_root < left_root:
            left_root, right_root = right_root, left_root
        self.parent[right_root] = left_root


def _load_rows(input_path: Path) -> tuple[dict[str, list[dict]], dict[str, object] | None]:
    """Stream rows into per-group buckets while gating version metadata."""
    groups: dict[str, list[dict]] = defaultdict(list)
    metadata: dict[str, object] | None = None
    with input_path.open(encoding="utf-8") as handle:
        for line_no, line in enumerate(handle, 1):
            if not line.strip():
                continue
            row = json.loads(line)
            current = {key: row.get(key) for key in VERSION_KEYS}
            missing = [key for key, value in current.items() if value in (None, "")]
            if missing:
                raise ValueError(f"{input_path}:{line_no}: missing metadata: {', '.join(missing)}")
            if "generator_config_hash" in missing:
                raise ValueError(f"{input_path}:{line_no}: generator_config_hash is required; refusing to split")
            if metadata is None:
                metadata = current
            elif current != metadata:
                changed = [key for key in VERSION_KEYS if current[key] != metadata[key]]
                raise ValueError(f"{input_path}:{line_no}: mixed metadata: {', '.join(changed)}")
            groups[_group_key(row, line_no, input_path)].append(row)
    return groups, metadata


def _merged_groups(groups: dict[str, list[dict]]) -> tuple[dict[str, list[dict]], dict[str, list[str]]]:
    """Merge groups that share any public state hash via union-find.

    Returns (canonical_group_name -> all rows, canonical -> member names).
    This guarantees identical states (and their teacher links) never land in
    two different main collections.
    """
    uf = _UnionFind()
    for name in sorted(groups):
        uf.find(name)
    hash_owner: dict[str, str] = {}
    for name in sorted(groups):
        for row in groups[name]:
            for state_hash in _state_hashes(row):
                owner = hash_owner.get(state_hash)
                if owner is None:
                    hash_owner[state_hash] = name
                else:
                    uf.union(owner, name)

    merged: dict[str, list[dict]] = defaultdict(list)
    members: dict[str, list[str]] = defaultdict(list)
    for name in sorted(groups):
        canonical = uf.find(name)
        merged[canonical].extend(groups[name])
        members[canonical].append(name)
    return dict(merged), {name: sorted(vals) for name, vals in members.items()}


def try_resolve_recorded_path(recorded: str, anchor_dirs: tuple[Path, ...]) -> Path | None:
    """Resolve a path recorded in a manifest against candidate anchors.

    Manifests may store sources relative to whichever directory the producer
    ran from (e.g. ``..\\data\\source.jsonl``). Each anchor is joined with
    ``recorded`` via os.path so Windows-style separators and ``..`` segments
    normalize correctly; the first existing hit wins.
    """
    candidates: list[str] = []
    if os.path.isabs(recorded):
        candidates.append(recorded)
    else:
        for anchor in anchor_dirs:
            candidates.append(os.path.normpath(os.path.join(str(anchor), recorded)))
    for candidate in candidates:
        path = Path(candidate)
        if path.exists():
            return path
    return None


def verify_split_dir(split_dir: Path) -> tuple[list[str], dict]:
    """Independently verify a written split tree. Returns (violations, stats)."""
    violations: list[str] = []
    stats: dict[str, dict] = {}
    episode_to_split: dict[str, str] = {}
    hash_to_split: dict[str, str] = {}

    summary_path = split_dir / "split-manifest.json"
    summary: dict = {}
    if not summary_path.exists():
        violations.append(f"missing split-manifest.json in {split_dir}")
    else:
        try:
            summary = json.loads(summary_path.read_text(encoding="utf-8"))
        except json.JSONDecodeError as exc:
            violations.append(f"split-manifest.json is not valid JSON: {exc}")

    total_rows = 0
    for name in SPLITS:
        path = split_dir / f"{name}.jsonl"
        if not path.exists():
            violations.append(f"missing split file {path.name}")
            continue
        rows = 0
        episodes: set[str] = set()
        with path.open(encoding="utf-8") as handle:
            for line_no, line in enumerate(handle, 1):
                if not line.strip():
                    continue
                try:
                    row = json.loads(line)
                except json.JSONDecodeError as exc:
                    violations.append(f"{path}:{line_no}: invalid JSON: {exc}")
                    continue
                rows += 1
                episode = row.get("episode_id") or row.get("trace_id")
                if episode:
                    episodes.add(str(episode))
                    other = episode_to_split.setdefault(str(episode), name)
                    if other != name:
                        violations.append(
                            f"episode '{episode}' crosses splits: present in both "
                            f"'{other}' and '{name}' ({path.name} line {line_no})"
                        )
                for state_hash in _state_hashes(row):
                    other = hash_to_split.setdefault(state_hash, name)
                    if other != name:
                        violations.append(
                            f"state_hash '{state_hash}' appears in multiple main collections: "
                            f"'{other}' and '{name}' ({path.name} line {line_no})"
                        )
        total_rows += rows
        stats[name] = {"row_count": rows, "episode_count": len(episodes)}

        manifest_path = split_dir / f"{name}.manifest.json"
        if manifest_path.exists():
            try:
                manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            except json.JSONDecodeError as exc:
                violations.append(f"{manifest_path.name} is not valid JSON: {exc}")
                manifest = None
            if isinstance(manifest, dict):
                if manifest.get("row_count") != rows:
                    violations.append(
                        f"{manifest_path.name}: row_count={manifest.get('row_count')!r} does not match file rows={rows}"
                    )
                recorded_sha = manifest.get("output_sha256")
                actual_sha = digest(path)
                if recorded_sha != actual_sha:
                    violations.append(
                        f"{manifest_path.name}: output_sha256 mismatch: recorded {recorded_sha}, actual {actual_sha}"
                    )
        else:
            violations.append(f"missing per-split manifest {manifest_path.name}")

    if summary:
        if summary.get("total_rows") != total_rows:
            violations.append(
                f"split-manifest.json: total_rows={summary.get('total_rows')!r} does not match sum of files={total_rows}"
            )
        source = summary.get("source")
        source_sha = summary.get("source_sha256")
        if source and source_sha:
            # Recorded sources may be relative to the producer's working
            # directory at split time; try split_dir, repo root and CWD.
            source_path = try_resolve_recorded_path(
                str(source),
                (split_dir, split_dir.parent.parent, split_dir.parent.parent / "training"),
            )
            if source_path is None:
                violations.append(f"split-manifest.json: source file not found: {source}")
            else:
                actual_source_sha = digest(source_path)
                if actual_source_sha != source_sha:
                    violations.append(
                        f"split-manifest.json: source_sha256 mismatch: recorded {source_sha}, actual {actual_source_sha}"
                    )
    return violations, stats


def split(input_path: Path, output_dir: Path) -> dict:
    groups, metadata = _load_rows(input_path)
    merged_groups, mergers = _merged_groups(groups)

    rows_by_split: dict[str, list[dict]] = {name: [] for name in SPLITS}
    groups_by_split: dict[str, list[str]] = {name: [] for name in SPLITS}
    assigned: dict[str, str] = {}
    hash_assignment: dict[str, str] = {}
    for canonical in sorted(merged_groups):
        name = split_for(canonical)
        groups_by_split[name].append(canonical)
        rows_by_split[name].extend(merged_groups[canonical])
        if canonical in assigned:
            raise ValueError(f"episode '{canonical}' crossed splits: {assigned[canonical]} vs {name}")
        assigned[canonical] = name
        # Post-write guard invariant: identical state hashes must share one
        # collection; merged unions make this true by construction.
        for row in merged_groups[canonical]:
            for state_hash in _state_hashes(row):
                owner = hash_assignment.setdefault(state_hash, name)
                if owner != name:
                    raise ValueError(
                        f"state_hash '{state_hash}' would be placed into both '{owner}' and '{name}'"
                    )

    output_dir.mkdir(parents=True, exist_ok=True)
    manifests = {}
    for name in SPLITS:
        path = output_dir / f"{name}.jsonl"
        with path.open("w", encoding="utf-8") as handle:
            for row in rows_by_split[name]:
                handle.write(json.dumps(row, ensure_ascii=False, separators=(",", ":")) + "\n")
        manifests[name] = {
            "split": name,
            "row_count": len(rows_by_split[name]),
            "group_count": len(groups_by_split[name]),
            "groups_sha256": hashlib.sha256("\n".join(groups_by_split[name]).encode("utf-8")).hexdigest().upper(),
            "source": str(input_path),
            "source_sha256": digest(input_path),
            "output_sha256": digest(path),
            "byte_count": path.stat().st_size,
            "storage_format": "jsonl",
            **(metadata or {}),
        }
        (output_dir / f"{name}.manifest.json").write_text(json.dumps(manifests[name], indent=2) + "\n", encoding="utf-8")
    summary = {
        "schema_version": 1,
        "source": str(input_path),
        "source_sha256": digest(input_path),
        "split_policy": "sha256(episode_id|trace_id)%100: challenge<10, test<30, validation<50, train>=50",
        "total_rows": sum(len(rows) for rows in rows_by_split.values()),
        "total_groups": len(groups),
        "merged_group_count": len(merged_groups),
        "group_mergers": {canonical: [m for m in members if m != canonical]
                          for canonical, members in mergers.items() if len(members) > 1},
        "splits": manifests,
        **(metadata or {}),
    }
    (output_dir / "split-manifest.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")

    violations, _ = verify_split_dir(output_dir)
    if violations:
        raise ValueError("split verification failed after write: " + "; ".join(violations))
    return summary


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output-dir", required=True, type=Path)
    args = parser.parse_args()
    print(json.dumps(split(args.input, args.output_dir), ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
