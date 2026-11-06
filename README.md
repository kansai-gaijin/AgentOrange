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

  The client's address is then `https://<machine>.<tailnet>.ts.net:8443`.
