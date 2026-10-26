# RouteBase

A self-hosted web app for recording, visualizing and managing your rides, runs and hikes — for yourself, or
for a small group of friends on one server. GPS tracks on interactive 3D maps, elevation/speed/HR charts,
segments with per-effort replay and heatmaps, race replay against simulated finishers, photos, multi-day
tours, share links, and a companion Android recorder with live tracking.

Imports GPX, FIT and TCX files, and can sync from Strava.

## Ways to run it

### 1. On your own PC, no login (Windows, Linux, macOS)

Single-user "localhost mode": no server, no identity provider, all data on your machine. Download the package for
your system from the [latest release](https://github.com/Tech-Jan/RouteBase/releases/latest) — it is self-contained,
no .NET install needed — and start it; the browser opens at `http://localhost:5054`, already signed in.

- **Windows:** `RouteBase-<version>-windows.zip` — unzip, double-click `Start RouteBase.bat`. Windows may warn
  about an unknown publisher (the package is not code-signed).
- **Linux:** `RouteBase-<version>-linux-x64.tar.gz` — `tar -xzf RouteBase-*-linux-x64.tar.gz && ./RouteBase/start.sh`
- **macOS:** `…-macos-apple-silicon.tar.gz` (M1 and newer) or `…-macos-intel.tar.gz` — unpack, then once in
  Terminal: `xattr -dr com.apple.quarantine RouteBase` (the app is not notarized by Apple, so macOS blocks
  downloaded copies until this flag is removed). Then double-click `Start RouteBase.command`.

To build a package yourself: `pwsh ./scripts/publish-local-win.ps1` (Windows) or
`scripts/publish-local.sh <linux-x64|osx-arm64|osx-x64>` (Linux/macOS; macOS packages must be built on a Mac).

- Your data lives in the `database/` folder next to the app. Back it up by copying that folder.
- Localhost mode refuses to start if it is bound to anything but loopback, so it is never reachable from
  the network. It is switched on only by the `appsettings.Local.json` file the package ships with.

### 2. On a server, for you and your friends (Docker)

Server mode is multi-user. Sign-in is delegated to an OpenID Connect provider — [Authentik](https://goauthentik.io)
is what it is developed against; any standard OIDC provider should work.

1. **Create an OIDC application** in your provider (confidential client, authorization-code flow) with
   redirect URI `https://<your-host>/signin-oidc`, scopes `openid profile email`.
2. **Run it:** fill in the values in [docker-compose.yml](docker-compose.yml), then `docker compose up -d`. It uses
   the prebuilt image `ghcr.io/tech-jan/routebase` (pin a version tag instead of `latest` if you prefer);
   `docker build -t routebase .` builds it yourself.

3. **Put it behind a reverse proxy with HTTPS** (Traefik, Caddy, nginx, Coolify, …). This is required, not
   optional: the app trusts `X-Forwarded-For` / `X-Forwarded-Proto` from its upstream, which is only safe when
   a proxy in front of it sets those headers and the container is not reachable directly.

Notes:

- **`/app/database` must be a volume.** It holds the SQLite database, photos, heatmap tiles and the
  encryption keys for login cookies and stored Strava credentials. Losing it loses all data.
- **Admin:** `Admin__Users__0` is your user id, which is the `sub` claim your OIDC provider issues (after your
  first login it is also your row in the `Users` table). Admins see the error log at `/Admin/Errors`. Leave it
  unset and nobody is admin. More admins: `Admin__Users__1`, …
- **Health check:** `GET /healthz` answers `ok` (or 503 when the database is unreachable).
- If your proxy puts password protection (e.g. Basic Auth) in front of the whole site, exclude `/s/`,
  `/api/recording/`, `/css/`, `/js/` and `/map-styles/` from it, or share links and the Android app stop working.
  The app's own login still protects everything else.

### 3. For development

Needs the .NET 10 SDK and Node.js 22.

```bash
cd RouteBaseWeb
npm ci
npm run build      # TypeScript → wwwroot/js (Vite)
dotnet run         # https://localhost:7013, http://localhost:5054
```

Without OIDC settings, use localhost mode for development: copy `scripts/appsettings.Local.json` next to the
built executable (e.g. `RouteBaseWeb/bin/Debug/net10.0/`). Or set the `Authentik:*` values with
`dotnet user-secrets` to develop against a real provider.

Before committing, run **[scripts/gates.sh](scripts/gates.sh)** (`--android` adds the Android gates). It runs
every build/test/lint step in the right order and directory — the order has traps (`dotnet test` from the
wrong directory silently runs 0 tests; a `dotnet build` straight after a TypeScript change fails MSB3030
once). The script's comments explain each one.

## Android recorder

`GpsRecorder/` is a native Android app (Kotlin, minSdk 26) that records GPS in the background, uploads to your
server and can share a live-tracking link. It talks to the server with a per-user API key, generated under
**Mobile API Key** on the web app's **API Keys & Database** page (`/KeysAndData`).

Install `RouteBase-recorder-<version>.apk` from the [latest release](https://github.com/Tech-Jan/RouteBase/releases/latest)
(allow "install unknown apps" for your browser). Every release is signed with the same key, so newer versions
install as updates.

Or build it with JDK 21 and the Android SDK (platform 36, build-tools 36.0.0):

```bash
echo "sdk.dir=$HOME/Android/Sdk" > GpsRecorder/local.properties   # gitignored, once per checkout
cd GpsRecorder
chmod +x gradlew
./gradlew assembleDebug        # → app/build/outputs/apk/debug/app-debug.apk
./gradlew testDebugUnitTest
```

## Maps and external services

Maps and routing load directly in the browser from public services: OpenFreeMap and OpenStreetMap
(basemaps), Esri World Imagery (satellite), AWS Terrarium tiles (3D terrain), BRouter and OSRM (snap-to-road
when drawing segments). These are free services with fair-use policies — fine for personal and small-group
use; a busy installation should run its own tiles/router. MapTiler styles and Strava sync are optional and use
each user's own keys, entered on the API Keys & Database page.

## Stack

ASP.NET Core 10 (Razor Pages) + Entity Framework Core over SQLite · TypeScript built with Vite, tested with
vitest · MapLibre GL JS maps · Chart.js charts · Kotlin Android app.

## Security

Found a vulnerability? Please report it privately via GitHub's **Report a vulnerability** button (Security
tab) rather than in a public issue.

## License

[MIT](LICENSE). The Garmin FIT SDK (NuGet `Garmin.FIT.Sdk`) is a separate dependency under Garmin's own
FIT Protocol License, not MIT.
