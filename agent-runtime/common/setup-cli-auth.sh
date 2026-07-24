#!/usr/bin/env bash
# Authorize the gh/glab CLIs from the git credential store so agents can push
# and open PRs/MRs with the same tokens git uses. Rewrites the CLI config
# files on every start (tokens are refreshed per session spawn).
set -euo pipefail

CRED_FILE="${1:-$HOME/.git-credentials}"
[ -f "$CRED_FILE" ] || exit 0

url_decode() { printf '%b' "$(printf '%s' "$1" | sed 's/%\([0-9a-fA-F][0-9a-fA-F]\)/\\x\1/g')"; }

gh_cfg="$HOME/.config/gh/hosts.yml"
glab_cfg="$HOME/.config/glab-cli/config.yml"
gh_written=0
glab_written=0

while IFS= read -r line; do
  case "$line" in https://*:*@*) ;; *) continue ;; esac
  rest="${line#https://}"
  userinfo="${rest%%@*}"
  host="${rest#*@}"
  user="$(url_decode "${userinfo%%:*}")"
  token="$(url_decode "${userinfo#*:}")"
  [ -n "$host" ] && [ -n "$token" ] || continue
  case "$user" in
    x-access-token) # GitHub-style provider (GitProviderConfig.GitCredUser)
      if [ "$gh_written" = 0 ]; then
        mkdir -p "${gh_cfg%/*}"
        : > "$gh_cfg"
        chmod 600 "$gh_cfg"
        gh_written=1
      fi
      {
        printf '%s:\n' "$host"
        printf '    oauth_token: %s\n' "$token"
        printf '    git_protocol: https\n'
        printf '    user: x-access-token\n'
        printf '    users:\n'
        printf '        x-access-token:\n'
        printf '            oauth_token: %s\n' "$token"
      } >> "$gh_cfg"
      ;;
    oauth2) # GitLab-style provider
      if [ "$glab_written" = 0 ]; then
        mkdir -p "${glab_cfg%/*}"
        printf 'git_protocol: https\nhosts:\n' > "$glab_cfg"
        chmod 600 "$glab_cfg"
        glab_written=1
      fi
      {
        printf '    %s:\n' "$host"
        printf '        token: %s\n' "$token"
        printf '        api_protocol: https\n'
        printf '        git_protocol: https\n'
      } >> "$glab_cfg"
      ;;
  esac
done < "$CRED_FILE"
