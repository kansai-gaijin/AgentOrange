#!/usr/bin/env bash
# Shows this server's pairing key, creating one the first time. Run with sudo:
#
#   sudo bash pairing-key.sh
#
# The key stays on this server. Paste it into Agent Orange once on each new PC.
set -euo pipefail

SETTINGS=/etc/agent-orange/server.env
NAME=AGENT_ORANGE_PAIRING_KEY

if [[ $EUID -ne 0 ]]; then
    echo "Run this with sudo." >&2
    exit 1
fi

if ! grep -q "^$NAME=" "$SETTINGS"; then
    echo "$NAME=$(openssl rand -hex 32)" >> "$SETTINGS"
    systemctl restart agent-orange-server.service
    echo "Created a new pairing key and restarted the server."
fi

echo
echo "Pairing key:"
sed -n "s/^$NAME=//p" "$SETTINGS"
