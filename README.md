# Agent Orange

This project is just getting started. Details will be added here as it takes shape.

After cloning, turn on the check that keeps databases and keys out of this public repo:

```
git config core.hooksPath .githooks
```

The same checks run on GitHub for every push, as the Private text workflow, for clones where the hooks weren't turned on. Only the list of private words in `.git/private-words` can't be checked there.

## Restoring the server's database

The server notices a restored backup by itself. It notes how far its data has got in `/var/lib/agent-orange`, and when the data has gone back, every PC sends what it has again and the newest copy of each record wins. If that folder was lost or restored too, run this against the database to do the same:

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

## Credits

Agent Orange is built on other people's work. Thank you to everyone below.

**Skills that come with Agent Orange** (`client/Skills/`; each keeps its licence beside it, and says what Agent Orange changed):

- [OpenDCAI/GameFactory-3A](https://github.com/OpenDCAI/GameFactory-3A) (Apache-2.0): play-and-review, model-facing, game-feel and plan-the-next-slice were adapted from its agent skills.
- [achimala/dream-loop](https://github.com/achimala/dream-loop) (MIT), by Anshu Chimala: the dream-loop skill.
- [EL4CTEO/blender-skills](https://github.com/EL4CTEO/blender-skills) (MIT): Agent Orange's main Blender skills and their scripts.
- [arjun988/blender-skills](https://github.com/arjun988/blender-skills) (MIT): the Blender art-direction, style and asset-role skills, and the shared references.
- [ahujasid/mcp-for-blender](https://github.com/ahujasid/mcp-for-blender) (MIT), by Siddharth Ahuja: the level design guide behind blender-level-design.
- [apekshik/anymachine](https://github.com/apekshik/anymachine), [anyscene](https://github.com/apekshik/anyscene), [anykit](https://github.com/apekshik/anykit) and [anyshot](https://github.com/apekshik/anyshot) (MIT), by Apekshik Panigrahi: the reference-first, kit cohesion, placement QA, shot rig and motion verify Blender skills, and the camrig shot rig.

**Tools Agent Orange sets up or works with:**

- [Blender MCP](https://projects.blender.org/lab/blender_mcp), by Blender Lab: how agents use Blender.
- Unreal Engine's own MCP plugin and agent skills, from the engine installed on the PC.
- Agent Orange's forks with built-in MCP servers: [Material Maker](https://github.com/RodZill4/material-maker) by Rodolphe Suescun (its fork also ships the material-maker skill), [ArmorPaint](https://github.com/armory3d/armortools) and [Aseprite](https://github.com/aseprite/aseprite).

**Ideas and design:**

- [T3 Code](https://github.com/pingdotgg/t3code): the agents window follows how it works and looks.

**Libraries and fonts:**

- [Velopack](https://github.com/velopack/velopack) for installing and updating, and [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) for the local database.
- [Inter](https://github.com/rsms/inter), under the SIL Open Font License (`client/Assets/Fonts/Inter-LICENSE.txt`).
- The server: [axum](https://github.com/tokio-rs/axum), [tokio](https://github.com/tokio-rs/tokio), [tokio-postgres](https://github.com/sfackler/rust-postgres), [serde](https://github.com/serde-rs/serde) and the [RustCrypto](https://github.com/RustCrypto) hashes, on [PostgreSQL](https://www.postgresql.org/), with [Cloudflare Tunnel](https://github.com/cloudflare/cloudflared) as one way in.
