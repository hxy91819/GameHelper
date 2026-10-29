#!/usr/bin/env bash
# Agent-only Linux search experiment. Data stays outside the indexed checkout.
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
bin=${XERJ_BIN:-$HOME/.local/bin/xerj}
cache=${XERJ_CACHE:-$HOME/.cache/gamehelper-xerj}
export XERJ_URL=${XERJ_URL:-http://127.0.0.1:19200}
export XERJ_DISABLE_FEEDBACK=true
index() {
  local rc=0
  "$bin" autoindex "$1" --url "$XERJ_URL" --prefix "$2" --workers 2 --fresh || rc=$?
  # 3 means searchable, but some files were skipped: keep the printed diagnostics.
  [[ $rc == 0 || $rc == 3 ]]
}
case ${1:-help} in
  start) exec "$bin" --insecure --bind 127.0.0.1 --port 19200 --data-dir "$cache/data" ;;
  mcp) exec "$bin" mcp ;;
  index) index "$root" gamehelper ;;
  references)
    mkdir -p "$cache/repos"
    for spec in JosefNemec/Playnite mdqinc/SDL_GameControllerDB; do
      name=${spec##*/}
      if [[ ! -d "$cache/repos/$name/.git" ]]; then
        git clone --depth 1 "https://github.com/$spec" "$cache/repos/$name"
      fi
      git -C "$cache/repos/$name" rev-parse HEAD
    done
    index "$cache/repos/Playnite" playnite
    index "$cache/repos/SDL_GameControllerDB" sdl
    ;;
  search) shift; exec "$bin" search "$@" ;;
  *) echo 'Usage: bash scripts/xerj-search.sh {start|mcp|index|references|search --prefix PREFIX QUERY}' ;;
esac
