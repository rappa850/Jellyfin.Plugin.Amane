# Emby / Jellyfin integration containers

This compose project runs separate disposable servers for the Phase 0 checks in `docs/EMBY_COMPATIBILITY_RESEARCH.md`. It uses non-default host ports and named data volumes, so it does not replace or bind the normal 8096/8920 ports. The Emby target is 4.10.0.40 (the .NET Core build/runtime investigated by the report); Jellyfin 10.11.10 and 12.1 are optional regression targets.

Run from this directory:

```sh
docker compose --profile emby up -d
docker compose --profile emby ps
docker compose --profile emby logs -f emby
```

## Control from Windows 11

Docker is installed as static binaries in Ubuntu-26.04 WSL and the daemon runs in the background. From PowerShell at the repository root, start all three servers with:

```powershell
wsl.exe -d Ubuntu-26.04 -u root -- docker compose -f /mnt/c/Users/84422/Documents/Projects/Jellyfin.Plugin.Amane/scripts/integration/compose.yaml --profile emby --profile jellyfin --profile jellyfin12 up -d
```

Docker is available only through WSL and its socket is root-owned, so include `-u root` in Windows commands. If WSL has been shut down, restart the daemon with:

```powershell
wsl.exe -d Ubuntu-26.04 -u root -- /bin/bash --noprofile --norc -c 'export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin; export LD_LIBRARY_PATH=/opt/iptables-runtime/usr/lib/x86_64-linux-gnu; export XTABLES_LIBDIR=/opt/iptables-runtime/usr/lib/x86_64-linux-gnu/xtables; nohup /usr/local/bin/dockerd --host=unix:///var/run/docker.sock --data-root=/var/lib/docker >/var/log/dockerd.log 2>&1 </dev/null &'
```

The Emby adapter currently loads from the root of `/config/plugins`; install its DLL and restart after building:

```powershell
wsl.exe -d Ubuntu-26.04 -u root -- docker cp /mnt/c/Users/84422/Documents/Projects/Jellyfin.Plugin.Amane/src/Amane.Emby/bin/Release/net8.0/Amane.Emby.dll amane-emby-integration:/config/plugins/Amane.Emby.dll
wsl.exe -d Ubuntu-26.04 -u root -- docker restart amane-emby-integration
```

For Jellyfin, install only the plugin and Core assemblies. Do not copy the `MediaBrowser.*` or `Jellyfin.*` host SDK assemblies into the plugin folder:

```powershell
wsl.exe -d Ubuntu-26.04 -u root -- docker exec -u root amane-jellyfin-integration mkdir -p /config/plugins/Amane
wsl.exe -d Ubuntu-26.04 -u root -- docker cp /mnt/c/Users/84422/Documents/Projects/Jellyfin.Plugin.Amane/src/Amane.Jellyfin/bin/Release/net9.0/Jellyfin.Plugin.Amane.dll amane-jellyfin-integration:/config/plugins/Amane/Jellyfin.Plugin.Amane.dll
wsl.exe -d Ubuntu-26.04 -u root -- docker restart amane-jellyfin-integration
```

For Jellyfin 12, create `/config/plugins/Amane` in `amane-jellyfin12-integration`, copy only the matching `Jellyfin.Plugin.Amane.dll` from `src/Amane.Jellyfin12/bin/Release/net10.0/`, then restart the container. All three adapters compile shared Core source into their single plugin DLL. When upgrading the earlier two-DLL development package, remove the old `/config/plugins/Amane/Amane.Core.dll` before restarting.

The current Amane test service is available to containers as `http://host.docker.internal:18102`; the PNG image fixture is `http://host.docker.internal:18103`. Both names map to the Docker bridge gateway `172.17.0.1`. Run these commands in WSL to forward the bridge-only ports to Win11 loopback services `127.0.0.1:18100` and `127.0.0.1:18101` respectively:

```sh
nohup python3 /mnt/c/Users/84422/Documents/Projects/Jellyfin.Plugin.Amane/scripts/integration/bridge_amane.py --listen 172.17.0.1 --port 18102 --upstream 127.0.0.1 --upstream-port 18100 >/tmp/amane-api-relay.log 2>&1 &
nohup python3 /mnt/c/Users/84422/Documents/Projects/Jellyfin.Plugin.Amane/scripts/integration/bridge_amane.py --listen 172.17.0.1 --port 18103 --upstream 127.0.0.1 --upstream-port 18101 >/tmp/amane-image-relay.log 2>&1 &
```

Open Emby at `http://localhost:18096`. Complete the first-run wizard, then install the Emby adapter package from the dashboard or copy the built plugin files into `/config/plugins` with `docker cp` and restart the container. Put test media under `scripts/integration/library/` (not committed). For Jellyfin 10 regression, use `docker compose --profile jellyfin up -d`; its UI is `http://localhost:18097`. For Jellyfin 12.1, use `docker compose --profile jellyfin12 up -d`; its UI is `http://localhost:18098`. The profiles do not start all servers unless all profiles are specified.

To stop a server while keeping its configuration and libraries:

```sh
docker compose --profile emby down
```

To remove the isolated server data as well:

```sh
docker compose --profile emby down -v
```

The Emby container image is maintained by Emby and may require acceptance of Emby's terms or a license key for playback. This setup is intended for local plugin loading, dashboard/configuration, metadata, and image-path checks. Create `library/` and `plugin/` beside this file before using bind mounts; do not put private media or credentials into Git. Container versions and runtime can be checked with:

```sh
docker exec amane-emby-integration /system/EmbyServer --version
docker exec amane-emby-integration dotnet --list-runtimes
```

The host-port mapping is `18096 -> 8096` for Emby, `18097 -> 8096` for Jellyfin 10, and `18098 -> 8096` for Jellyfin 12.1. Data remains in separate Compose named volumes per server version. The official Jellyfin image publishes version `12.1` under the Docker tag `12.1` (there is no `12.1.0` tag).

Final verification uses plugin ServerUrl `http://172.17.0.1:18102` (the hostname alias produced premature-response errors in the Emby .NET client despite curl succeeding). Emby movie libraries must enable `DownloadImagesInAdvance`; `CacheImages` is not required. The actor fixture uses `http://192.168.1.13:18104/actor.png` via a separate WSL relay; the movie preview fixture remains on Win loopback so that Jellyfin Web can reach it. These addresses belong only to this machine's test setup and must be replaced on other networks.

The initialized sessions and temporary Amane state live under `%LOCALAPPDATA%/Temp/AmaneCompatRuntime`. With all services running, `python scripts/integration/verify_hosts.py` verifies fields, binding, actor details, actual image bytes, search previews and cache operations, then writes a credential-free summary. It also records the expected manual-image-download failure and restores the automatic image afterward. See `docs/image-url-evaluation.md` before changing image paths.
