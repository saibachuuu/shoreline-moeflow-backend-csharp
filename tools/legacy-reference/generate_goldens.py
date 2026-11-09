#!/usr/bin/env python3
"""Generate or verify the stateless Python reference golden fixtures.

This script does not import the legacy application package and does not run
the legacy test suite. It reads only whitelisted source files and evaluates
the extracted definitions under the guards in reference_harness.py.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Optional

from reference_harness import DEFAULT_PYTHON_ROOT, build_goldens


HERE = Path(__file__).resolve().parent
DEFAULT_OUT = HERE / "fixtures" / "goldens.json"


def render(goldens: dict) -> str:
    return json.dumps(goldens, ensure_ascii=False, indent=2, sort_keys=False) + "\n"


def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(
        description="Generate Python reference fixtures from AST-extracted source"
    )
    parser.add_argument(
        "--python-root",
        type=Path,
        default=DEFAULT_PYTHON_ROOT,
        help="read-only legacy Python repository root",
    )
    parser.add_argument(
        "--out",
        type=Path,
        default=DEFAULT_OUT,
        help="fixture output path (default: fixtures/goldens.json)",
    )
    parser.add_argument(
        "--check",
        action="store_true",
        help="verify the checked-in fixture instead of writing it",
    )
    parser.add_argument("--quiet", action="store_true")
    args = parser.parse_args(argv)

    goldens = build_goldens(args.python_root)
    rendered = render(goldens)
    if args.check:
        if not args.out.exists():
            print(f"missing fixture: {args.out}", file=sys.stderr)
            return 2
        existing = args.out.read_text(encoding="utf-8")
        if existing != rendered:
            print(f"fixture is stale: {args.out}", file=sys.stderr)
            return 1
        if not args.quiet:
            print(f"OK {args.out} ({len(rendered.encode('utf-8'))} bytes)")
        return 0

    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(rendered, encoding="utf-8", newline="\n")
    if not args.quiet:
        fixtures = goldens["fixtures"]
        print(
            "wrote "
            f"{args.out} ({len(rendered.encode('utf-8'))} bytes), "
            f"{len(goldens['provenance'])} source files, "
            f"{len(fixtures['filename']['cases'])} filename cases, "
            f"{len(fixtures['notification']['valid'])} valid BBCode cases, "
            f"{fixtures['label_snapshot']['chunk_count']} snapshot chunks"
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
