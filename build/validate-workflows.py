#!/usr/bin/env python3
"""Validates workflow YAML locally so malformed pipelines never consume a CI cycle."""
import sys, pathlib, yaml

ok = True
for path in sorted(pathlib.Path('.github/workflows').glob('*.yml')):
    try:
        doc = yaml.safe_load(path.read_text())
        jobs = doc.get('jobs') or {}
        if not jobs:
            print(f"FAIL {path}: no jobs parsed"); ok = False; continue
        for jn, jb in jobs.items():
            steps = jb.get('steps') or []
            if not steps:
                print(f"FAIL {path}: job '{jn}' has no steps"); ok = False
        print(f"OK   {path}: jobs={list(jobs)}")
    except Exception as exc:
        print(f"FAIL {path}: {exc}"); ok = False
sys.exit(0 if ok else 1)
