#!/usr/bin/env python3
"""Build fresh-pilot protection from opaque prepared data and native public metadata.

Native metadata inputs reject target-bearing rows. Explicit already-inspected
native archives are projected without decoding target or outcome values. Old
prepared target shards are always opaque.
"""
import argparse
import json
from pathlib import Path

import prepare_dataset as prep


AUDIT_FIELDS = set(prep.PROVENANCE_FIELDS) | {"actual_seed", "act", "floor", "encounter", "source_kind", "native_run"}


def _value_end(text, index):
    """Skip a JSON value lexically; skipped target payloads are never decoded."""
    depth, quoted = 0, False
    while index < len(text):
        char = text[index]
        if quoted:
            if char == "\\": index += 2; continue
            if char == '"':
                quoted = False
                if depth == 0: return index + 1
        elif char == '"': quoted = True
        elif char in "[{": depth += 1
        elif char in "]}":
            if depth == 0: return index
            depth -= 1
            if depth == 0: return index + 1
        elif char == "," and depth == 0: return index
        index += 1
    raise ValueError("incomplete native archive JSON")


def _select_fields(text, fields):
    decoder = json.JSONDecoder()
    index, result = 0, {}
    def whitespace(i):
        while i < len(text) and text[i].isspace(): i += 1
        return i
    index = whitespace(index)
    if text[index] != "{": raise ValueError("native archive object required")
    index += 1
    while True:
        index = whitespace(index)
        if text[index] == "}": return result
        key, index = decoder.raw_decode(text, index)
        index = whitespace(index)
        if not isinstance(key, str) or text[index] != ":": raise ValueError("native archive field invalid")
        index = whitespace(index + 1)
        end = _value_end(text, index)
        if key in fields:
            if key in result: raise ValueError("duplicate native archive field")
            result[key] = (_select_fields(text[index:end], AUDIT_FIELDS) if key == "audit_only"
                           else json.loads(text[index:end]))
        index = whitespace(end)
        if text[index] == "}": return result
        if text[index] != ",": raise ValueError("native archive delimiter invalid")
        index += 1


def project_native_source_metadata(line):
    """Public/provenance projection for an explicitly inspected native archive."""
    return _select_fields(line, {"public_input", "audit_only"})



def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--protect-from-prepared", required=True, type=Path)
    sources = parser.add_mutually_exclusive_group(required=True)
    sources.add_argument("--native-metadata", type=Path, nargs="+")
    sources.add_argument("--native-source-archives", type=Path, nargs="+",
                         help="Explicitly inspected native archives; project public/source identity without decoding labels")
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    registry = prep.export_split_protection(args.protect_from_prepared)
    paths = args.native_metadata or args.native_source_archives
    hashes = [prep.file_hash(path) for path in paths]
    if args.native_metadata:
        metadata, _ = prep.read_jsonl(paths)
    else:
        metadata = []
        for path in paths:
            with path.open(encoding="utf-8") as handle:
                metadata.extend(project_native_source_metadata(line) for line in handle if line.strip())
    if hashes != [prep.file_hash(path) for path in paths]:
        raise ValueError("native metadata changed during read")
    registry = prep.extend_native_protection(registry, metadata)
    # Exclusive creation keeps an existing collection's bound protection immutable.
    with args.output.open("xb") as handle: handle.write(prep.registry_bytes(registry))
    print(json.dumps({"schema_version": registry["schema_version"], "sha256": prep.registry_hash(registry),
                      "native_metadata_rows": len(metadata), "old_test_targets_decoded": False}))


if __name__ == "__main__": main()
