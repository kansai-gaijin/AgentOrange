# Agent Orange

This project is just getting started. Details will be added here as it takes shape.

After cloning, turn on the check that keeps databases and keys out of this public repo:

```
git config core.hooksPath .githooks
```

## Restoring the server's database

After restoring a backup, run this against the database, so every PC sends what it has again and the newest copy of each record wins:

```
UPDATE sync_meta SET epoch = md5(random()::text || clock_timestamp()::text);
```

## Reaching the server

The server only listens on its own machine, and the client only uses HTTPS, except for an address on the same PC. So it needs an HTTPS front for each way in.

- **Public:** `server/deploy/install.sh` sets up a Cloudflare tunnel.
- **Tailscale:** the install script leaves this alone, because a server's Tailscale routes can belong to other services. On the server, list what's already there, then add a route on a free port:

  ```
  tailscale serve status
  sudo tailscale serve --bg --https=8443 http://127.0.0.1:47100
  ```

  The client's address is then `https://<machine>.<tailnet>.ts.net:8443`. If Tailscale runs in user-space mode with its own socket, add `--socket=<path>` to both commands.

## Server updates

The server installs each release by itself, a few minutes after it's published, and keeps only the one it's running. GitHub keeps only the newest release too, as updates are required and nothing goes back.

A release that doesn't come up healthy is put back to the one before and tried again up to three more times, 15, 30 and 60 minutes apart. PCs wait for the server to be on a release before they require it, so a release the server never takes leaves everyone on the one before.

Each release carries the updater, so after the first install, `sudo ./install.sh` on its own is only needed to change the service files.
