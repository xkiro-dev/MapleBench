#!/usr/bin/env bash
# Syntax-checks every module. `node --check file.js` parses as CommonJS and
# silently accepts things the browser rejects, so pipe through stdin with
# --input-type=module instead -- that is the parser the browser actually uses.
fail=0
for f in "$(dirname "$0")"/*.js; do
  out=$(node --input-type=module --check < "$f" 2>&1) || { echo "FAIL $(basename "$f")"; echo "$out" | head -6; fail=1; }
done
[ $fail -eq 0 ] && echo "all modules parse cleanly"
exit $fail
