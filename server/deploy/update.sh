#!/usr/bin/env bash
# Installs the latest published server release, and goes back to the previous
# one if the new one doesn't come up healthy. Runs from a systemd timer.
#
# Each release carries its own copy of this script, and the copy the installed
# release carries is the one that runs, so changes here arrive with releases.
# The copy install.sh puts in /usr/local/libexec only starts that, or does the
# first install.
set -euo pipefail

: "${AO_ROOT:=/opt/agent-orange}"
: "${AO_RELEASES:=https://github.com/kansai-gaijin/AgentOrange/releases}"
: "${AO_HEALTH_URL:=http://127.0.0.1:47100/v1/health}"

BINARY=agent-orange-server
ASSET=agent-orange-server-linux-x64
UPDATER=agent-orange-update

# A copy that doesn't even parse would otherwise stop every update after it, so that one is passed over.
carried="$AO_ROOT/current/$UPDATER"
if [[ -z "${AO_UPDATER_STARTED:-}" && -f "$carried" && "$(realpath "$0")" != "$(realpath "$carried")" ]] && bash -n "$carried" 2>/dev/null; then
    AO_UPDATER_STARTED=1 exec bash "$carried" "$@"
fi

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

# Only the installed release is kept. Updates are required, so nothing goes back to an older one,
# and a release is only removed once the next one has proved healthy. Only touches this updater's
# own entries in its own folder.
remove_old_releases() {
    local keep="$1" entry name
    for entry in "$AO_ROOT/releases"/* "$AO_ROOT/releases"/.stage.*; do
        [[ -e "$entry" ]] || continue
        name="$(basename "$entry")"
        [[ "$name" == "$keep" ]] && continue
        if [[ "$name" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
            rm -rf -- "$entry"
        elif [[ "$name" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.failed$ || "$name" =~ ^\.stage\.[A-Za-z0-9]+$ ]]; then
            # A stage folder still in use belongs to an updater running right now, which can't
            # happen under systemd, but one left a day is certainly from one that was stopped.
            [[ "$name" == .stage.* && -z "$(find "$entry" -maxdepth 0 -mmin +1440)" ]] && continue
            rm -rf -- "$entry"
        fi
    done
}

latest="$(fetch "$AO_RELEASES/latest/download/server-version.txt")"
if [[ ! "$latest" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "Refusing to install: unexpected version text from the latest release." >&2
    exit 1
fi

previous="$(readlink "$AO_ROOT/current" 2>/dev/null || true)"
if [[ "$previous" == "releases/$latest" ]]; then
    remove_old_releases "$latest"
    exit 0
fi

# Never backward, as clients only go forward and sync needs the two to match.
installed="${previous#releases/}"
if [[ -n "$installed" && "$(printf '%s\n%s\n' "$latest" "$installed" | sort -V | tail -n 1)" != "$latest" ]]; then
    echo "Skipping $latest: $installed is already installed, and it's newer." >&2
    exit 0
fi

# A release that wasn't healthy is tried again a few times, further apart each time, as the cause
# can pass, such as the database restarting. Clients wait for the server to have a release before
# they require it, so a release that never comes up only holds everyone on the one before.
ATTEMPTS=4
failed_marker="$AO_ROOT/releases/$latest.failed"
failures=0
if [[ -e "$failed_marker" ]]; then
    failures="$(head -c 8 "$failed_marker" | tr -cd '0-9')"
    failures="${failures:-$ATTEMPTS}" # An old marker, written before retries, has nothing in it.
    if ((failures >= ATTEMPTS)); then
        echo "Skipping $latest: it failed its health check $failures times." >&2
        exit 0
    fi
    wait_minutes=$((15 * (1 << (failures - 1)))) # 15, 30, then 60 minutes.
    if [[ -z "$(find "$failed_marker" -mmin "+$wait_minutes")" ]]; then
        echo "Waiting to try $latest again: it failed its health check $failures time(s)." >&2
        exit 0
    fi
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
    fetch --output "$stage/$UPDATER" "$AO_RELEASES/download/v$latest/$UPDATER"
    fetch --output "$stage/$UPDATER.sha256" "$AO_RELEASES/download/v$latest/$UPDATER.sha256"
    (cd "$stage" && sha256sum --check --quiet "$UPDATER.sha256")
    rm "$stage/$UPDATER.sha256"
    chmod 755 "$stage" "$stage/$BINARY" "$stage/$UPDATER"
    mv -T "$stage" "$destination"
    trap - EXIT
fi

echo "Installing $latest (was: ${previous:-nothing})."
point_current_at "releases/$latest"

# If it couldn't restart, the old release is what would answer the health check.
if restart_server && is_healthy; then
    echo "$latest is healthy."
    remove_old_releases "$latest"
    exit 0
fi

failures=$((failures + 1))
echo "$latest did not become healthy (attempt $failures of $ATTEMPTS)." >&2
echo "$failures" > "$failed_marker"
if [[ -n "$previous" && "$previous" != "releases/$latest" ]]; then
    point_current_at "$previous"
    restart_server
    echo "Went back to $previous." >&2
fi
exit 1
