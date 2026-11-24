#!/bin/bash
# Builds and runs Kapeyn's macOS tests (needs only the Swift compiler: xcode-select --install).
# Exit code 0 = all tests passed.
set -euo pipefail
cd "$(dirname "$0")/.."   # the mac/ folder

SRC=()
for f in src/*.swift; do
    [ "$(basename "$f")" = "main.swift" ] || SRC+=("$f")   # the tests have their own entry point
done

mkdir -p obj
swiftc -swift-version 5 -module-name Kapeyn -o obj/KapeynTests "${SRC[@]}" tests/*.swift
obj/KapeynTests ..
