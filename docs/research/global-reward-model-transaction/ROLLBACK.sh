#!/usr/bin/env bash
set -euo pipefail
# Restore a transaction copy to its baseline fixture.
cp "$1.baseline" "$1"
