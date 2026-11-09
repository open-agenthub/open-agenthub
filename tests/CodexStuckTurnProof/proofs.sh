#!/bin/bash
# #562 real-CLI proofs, one after another (they share the fixture HOME).
cd "$(dirname "$0")"
run() { name=$1; shift; rm -rf tmp-run-*; env "$@" OUT=$PWD/out-final-$name timeout 900 node rig562.mjs >/dev/null 2>&1; echo "== $name"; grep -E "RESULT|STALLED|Turn interrupted|AUTO|exited|STALL NOTICE" out-final-$name/rig.log | grep -v "title" | tail -8; }
run build NOTICE_MIN=1 SCENARIO=active ACTIVE_PROMPT='Please run the ACTIVE build' ACTIVE_MS=240000
run text WS=1 NOTICE_MIN=1 SCENARIO=active ACTIVE_PROMPT='Please answer with STREAMTEXT' STREAM_MS=240000 STREAM_EVERY_MS=40000
run reason WS=1 NOTICE_MIN=1 SCENARIO=active ACTIVE_PROMPT='Please think STREAMREASON' STREAM_MS=240000 STREAM_EVERY_MS=40000
run auto MODE=autonomous PROMPT='Please do the STALLTOOL task' WS=1 KEEPALIVE_MS=5000 STALL_CMD='sleep infinity' NOTICE_MIN=1 INTERRUPT_MIN=2 WATCH_MS=200000
run interactive-no-auto WS=1 KEEPALIVE_MS=5000 STALL_CMD='sleep infinity' NOTICE_MIN=1 INTERRUPT_MIN=1 SCENARIO=stalltool WATCH_MS=180000
run reconnecting NOTICE_MIN=1 SCENARIO=down WATCH_MS=120000
echo ALL DONE
