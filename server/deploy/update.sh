#!/usr/bin/env bash
# Installs the latest published server release, and goes back to the previous
# one if the new one doesn't come up healthy. Runs from a systemd timer.
set -euo pipefail

: "${AO_ROOT:=/opt/agent-orange}"
: "${AO_RELEASES:=https://github.com/kansai-gaijin/AgentOrange/releases}"
: "${AO_HEALTH_URL:=http://127.0.0.1:47100/v1/health}"

BINARY=agent-orange-server
ASSET=agent-orange-server-linux-x64

fetch() { curl --fail --silent --show-error --location --max-time 300 "$@"; }

restart_server() { sudo --non-interactive systemctl restart agent-orange-server.service; }

is_healthy() {
    local body
    for _ in $(seq 1 15); do
        if body="$(curl --fail --silent --max-time 2 "$AO_HEALTH_URL")" && [[ "$body" == "🍊" ]]; then
            return 0
        fi
        sleep 1
    done
    return 1
}

point_current_at() {
    ln -sfn "$1" "$AO_ROOT/current.new"
    mv -T "$AO_ROOT/current.new" "$AO_ROOT/current"
}

latest="$(fetch "$AO_RELEASES/latest/download/server-version.txt")"
if [[ ! "$latest" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "Refusing to install: unexpected version text from the latest release." >&2
    exit 1
fi

previous="$(readlink "$AO_ROOT/current" 2>/dev/null || true)"
if [[ "$previous" == "releases/$latest" ]]; then
    exit 0
fi

# Never backward, as clients only go forward and sync needs the two to match.
installed="${previous#releases/}"
if [[ -n "$installed" && "$(printf '%s\n%s\n' "$latest" "$installed" | sort -V | tail -n 1)" != "$latest" ]]; then
    echo "Skipping $latest: $installed is already installed, and it's newer." >&2
    exit 0
fi

failed_marker="$AO_ROOT/releases/$latest.failed"
if [[ -e "$failed_marker" ]]; then
    echo "Skipping $latest: it failed its health check before." >&2
    exit 0
fi

destination="$AO_ROOT/releases/$latest"
if [[ ! -d "$destination" ]]; then
    stage="$(mktemp --directory "$AO_ROOT/releases/.stage.XXXXXX")"
    trap 'rm -rf "$stage"' EXIT
    fetch --output "$stage/$ASSET" "$AO_RELEASES/download/v$latest/$ASSET"
    fetch --output "$stage/$ASSET.sha256" "$AO_RELEASES/download/v$latest/$ASSET.sha256"
    (cd "$stage" && sha256sum --check --quiet "$ASSET.sha256")
    mv "$stage/$ASSET" "$stage/$BINARY"
    rm "$stage/$ASSET.sha256"
    chmod 755 "$stage" "$stage/$BINARY"
    mv -T "$stage" "$destination"
    trap - EXIT
fi

echo "Installing $latest (was: ${previous:-nothing})."
point_current_at "releases/$latest"

# If it couldn't restart, the old release is what would answer the health check.
if restart_server && is_healthy; then
    echo "$latest is healthy."
    exit 0
fi

echo "$latest did not become healthy." >&2
touch "$failed_marker"
if [[ -n "$previous" && "$previous" != "releases/$latest" ]]; then
    point_current_at "$previous"
    restart_server
    echo "Went back to $previous." >&2
fi
exit 1
