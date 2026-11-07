#!/usr/bin/env bash
# Runs the published one-command quickstart inside the guest exactly as the website shows it,
# with a timestamp on every output line, so the audit can quote per-step durations.
set -u
cd "$(dirname "$0")"
bash ssh.sh 'rm -f ~/qs.log; (date -u +START\ %FT%TZ; time (curl -fsSL https://open-agenthub.github.io/install.sh | sh); echo EXIT=$?) 2>&1 | while IFS= read -r l; do printf "%s %s\n" "$(date -u +%T)" "$l"; done > ~/qs.log'
bash ssh.sh 'cat ~/qs.log'
