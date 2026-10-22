#!/usr/bin/env python3
"""Block gh commands that would upload an empty --body-file (or -F body=@file).

A failed `gh issue view > file` leaves an empty file; uploading it blanks the issue.
"""
import json, os, re, shlex, sys

cmd = json.load(sys.stdin).get("tool_input", {}).get("command", "")
if "gh " not in cmd:
    sys.exit(0)
try:
    tokens = shlex.split(cmd, posix=True)
except ValueError:
    sys.exit(0)

paths = []
for i, tok in enumerate(tokens):
    if tok in ("--body-file", "-F") and i + 1 < len(tokens):
        nxt = tokens[i + 1]
        paths.append(nxt[len("body=@"):] if nxt.startswith("body=@") else nxt if tok == "--body-file" else None)
    elif tok.startswith("--body-file="):
        paths.append(tok.split("=", 1)[1])
cwd = os.getcwd()
for m in re.finditer(r"\bcd\s+(\S+)", cmd):
    cwd = os.path.expanduser(m.group(1))
for p in filter(None, paths):
    if p == "-":
        continue
    full = p if os.path.isabs(p) else os.path.join(cwd, p)
    if os.path.isfile(full) and os.path.getsize(full) == 0:
        print(f"Refusing to upload an empty body file: {p}. Re-fetch it and check it's non-empty.", file=sys.stderr)
        sys.exit(2)
sys.exit(0)
