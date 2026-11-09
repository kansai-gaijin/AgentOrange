#!/usr/bin/env bash
# One-time server setup. Run with sudo:
#
#   sudo ./install.sh <public-hostname> <tunnel-credentials.json>
#
# Once installed, `sudo ./install.sh` on its own refreshes the service files and
# the updater, and leaves the public route as it is.
#
# It only adds things named agent-orange / agentorange, and refuses to run if
# any of them already exist from something else. It never edits or restarts
# anything that belongs to another service: the public route is its own tunnel,
# and the database and role are new. It deletes nothing.
#
# Safe to run again after a failure, or to refresh the service files.
set -euo pipefail

if [[ $EUID -ne 0 ]]; then
    echo "Run this with sudo." >&2
    exit 1
fi
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ACCOUNT=agentorange
DATABASE=agentorange
PORT=47100
PUBLIC_PORT=47101 # Only the Cloudflare tunnel uses this, so only there is its caller header trusted.
ROOT=/opt/agent-orange
CONFIG=/etc/agent-orange
MARKER="$CONFIG/.installed-by-agent-orange"

REFRESH_ONLY=
if [[ $# -eq 0 && -e "$MARKER" ]]; then
    REFRESH_ONLY=1
elif [[ $# -ne 2 ]]; then
    echo "Usage: sudo $0 <public-hostname> <tunnel-credentials.json>" >&2
    echo "Once installed, sudo $0 on its own refreshes it." >&2
    exit 1
else
    HOSTNAME_PUBLIC="$1"
    TUNNEL_CREDENTIALS="$2"
    if [[ ! "$HOSTNAME_PUBLIC" =~ ^[a-z0-9.-]+$ ]]; then
        echo "That doesn't look like a hostname: $HOSTNAME_PUBLIC" >&2
        exit 1
    fi
    if [[ ! -f "$TUNNEL_CREDENTIALS" ]]; then
        echo "Tunnel credentials not found: $TUNNEL_CREDENTIALS" >&2
        exit 1
    fi
fi

pg() { sudo -u postgres psql --no-psqlrc --tuples-only --no-align --set ON_ERROR_STOP=1 "$@"; }
role_exists() { [[ "$(pg -c "select 1 from pg_roles where rolname = '$ACCOUNT'")" == "1" ]]; }
database_exists() { [[ "$(pg -c "select 1 from pg_database where datname = '$DATABASE'")" == "1" ]]; }

if [[ ! -e "$MARKER" ]]; then
    problems=()
    id "$ACCOUNT" >/dev/null 2>&1 && problems+=("Unix account $ACCOUNT")
    [[ -e "$ROOT" ]] && problems+=("$ROOT")
    [[ -e "$CONFIG" ]] && problems+=("$CONFIG")
    role_exists && problems+=("Postgres role $ACCOUNT")
    database_exists && problems+=("Postgres database $DATABASE")
    ss -tln | grep -q ":$PORT " && problems+=("something listening on port $PORT")
    ss -tln | grep -q ":$PUBLIC_PORT " && problems+=("something listening on port $PUBLIC_PORT")
    for unit in agent-orange-server.service agent-orange-update.service agent-orange-update.timer agent-orange-tunnel.service; do
        [[ -e "/etc/systemd/system/$unit" ]] && problems+=("/etc/systemd/system/$unit")
    done
    if ((${#problems[@]})); then
        echo "Nothing changed. These already exist and aren't from this installer:" >&2
        printf '  - %s\n' "${problems[@]}" >&2
        exit 1
    fi
fi

echo "== Account and folders"
id "$ACCOUNT" >/dev/null 2>&1 || useradd --system --user-group --home-dir "$ROOT" --no-create-home --shell /usr/sbin/nologin "$ACCOUNT"
install -d -o root -g "$ACCOUNT" -m 750 "$CONFIG"
touch "$MARKER"
install -d -o "$ACCOUNT" -g "$ACCOUNT" -m 755 "$ROOT" "$ROOT/releases"

echo "== Server settings"
if [[ ! -e "$CONFIG/server.env" ]]; then
    password="$(openssl rand -hex 32)"
    umask 027
    {
        echo "AGENT_ORANGE_BIND=127.0.0.1:$PORT"
        echo "AGENT_ORANGE_PUBLIC_BIND=127.0.0.1:$PUBLIC_PORT"
        echo "DATABASE_URL=postgres://$ACCOUNT:$password@127.0.0.1:5432/$DATABASE"
    } > "$CONFIG/server.env"
    umask 022
    chown root:"$ACCOUNT" "$CONFIG/server.env"
    chmod 640 "$CONFIG/server.env"
fi
# Installs from before the public route had its own address.
if ! grep -q '^AGENT_ORANGE_PUBLIC_BIND=' "$CONFIG/server.env"; then
    echo "AGENT_ORANGE_PUBLIC_BIND=127.0.0.1:$PUBLIC_PORT" >> "$CONFIG/server.env"
fi
password="$(sed -n "s|^DATABASE_URL=postgres://$ACCOUNT:\([0-9a-f]*\)@.*|\1|p" "$CONFIG/server.env")"

echo "== New database"
if ! role_exists; then
    # Sent over stdin so the password never shows up in the process list.
    pg <<< "create role $ACCOUNT login password '$password';" >/dev/null
fi
if ! database_exists; then
    pg -c "create database $DATABASE owner $ACCOUNT" >/dev/null
    pg -c "revoke connect on database $DATABASE from public" >/dev/null
fi
PGPASSWORD="$password" psql --no-psqlrc --host 127.0.0.1 --username "$ACCOUNT" --dbname "$DATABASE" --tuples-only --no-align --command "select 'Database login works.'"

echo "== Updater and services"
install -d /usr/local/libexec
install -o root -g root -m 755 "$HERE/update.sh" /usr/local/libexec/agent-orange-update
for unit in agent-orange-server.service agent-orange-update.service agent-orange-update.timer agent-orange-tunnel.service; do
    install -o root -g root -m 644 "$HERE/$unit" "/etc/systemd/system/$unit"
done

rule="$(mktemp)"
echo "$ACCOUNT ALL=(root) NOPASSWD: /usr/bin/systemctl restart agent-orange-server.service" > "$rule"
visudo --check --file "$rule" >/dev/null
install -o root -g root -m 440 "$rule" /etc/sudoers.d/agent-orange-update
rm -f "$rule"

systemctl daemon-reload
systemctl enable agent-orange-server.service >/dev/null

echo "== First install of the latest release"
systemctl start agent-orange-update.service
systemctl enable --now agent-orange-update.timer >/dev/null
systemctl restart agent-orange-server.service
sleep 2
echo "Server says: $(curl --fail --silent --show-error --retry 5 --retry-connrefused "http://127.0.0.1:$PORT/v1/health")"

if [[ -n "$REFRESH_ONLY" ]]; then
    # Moves an existing public route onto its own address, once the running release listens there.
    if grep -q "service: http://127.0.0.1:$PORT\$" "$CONFIG/tunnel.yml" 2>/dev/null; then
        if curl --fail --silent --retry 5 --retry-connrefused "http://127.0.0.1:$PUBLIC_PORT/v1/health" >/dev/null; then
            sed -i "s|service: http://127.0.0.1:$PORT\$|service: http://127.0.0.1:$PUBLIC_PORT|" "$CONFIG/tunnel.yml"
            /usr/local/bin/cloudflared --config "$CONFIG/tunnel.yml" tunnel ingress validate
            systemctl restart agent-orange-tunnel.service
            echo "The public route now has its own address."
        else
            echo "The public route stays where it is, as this release doesn't listen on port $PUBLIC_PORT yet. Run this again after the next update." >&2
        fi
    fi
    echo
    echo "Done. The services and updater are refreshed."
    exit 0
fi

echo "== Public route"
install -o root -g "$ACCOUNT" -m 640 "$TUNNEL_CREDENTIALS" "$CONFIG/tunnel.json"
tunnel_id="$(sed -n 's/.*"TunnelID":"\([^"]*\)".*/\1/p' "$CONFIG/tunnel.json")"
if [[ -z "$tunnel_id" ]]; then
    echo "Couldn't read the tunnel ID from $TUNNEL_CREDENTIALS." >&2
    exit 1
fi
cat > "$CONFIG/tunnel.yml" <<EOF
tunnel: $tunnel_id
credentials-file: $CONFIG/tunnel.json
ingress:
  - hostname: $HOSTNAME_PUBLIC
    service: http://127.0.0.1:$PUBLIC_PORT
  - service: http_status:404
EOF
chown root:"$ACCOUNT" "$CONFIG/tunnel.yml"
chmod 640 "$CONFIG/tunnel.yml"
/usr/local/bin/cloudflared --config "$CONFIG/tunnel.yml" tunnel ingress validate
systemctl enable agent-orange-tunnel.service >/dev/null
systemctl restart agent-orange-tunnel.service

echo
echo "Done. The server is running and will update itself."
