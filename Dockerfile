# 1. Build stage
# PINNED to an exact patch, not the floating 10.0 tag (changed 2026-08-16). The float silently
# upgraded the server's compiler whenever Microsoft shipped: 10.0.400 landed 2026-08-11 with a
# stricter Razor parser and failed a deploy on a .cshtml that built fine against the dev SDK's
# 10.0.300. Keep this equal to the SDK you develop against — bump both together, deliberately.
FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
WORKDIR /source

# Node.js 22.x — required for npm ci + Vite TypeScript build
RUN apt-get update && apt-get install -y curl && \
    curl -fsSL https://deb.nodesource.com/setup_22.x | bash - && \
    apt-get install -y nodejs && \
    rm -rf /var/lib/apt/lists/*

# Cached layers: nuget restore + npm ci re-run only when manifests change.
# No --locked-mode: packages.lock.json was removed (see Directory.Build.props for why —
# VS rewrote it empty and failed deploys closed). NuGetAudit still reports known CVEs here.
# RID-agnostic here: the csproj declares <RuntimeIdentifiers> incl. linux-x64, so this
# restore resolves the linux-x64 assets the publish below consumes via --no-restore.
COPY Directory.Build.props ./
COPY RouteBaseWeb/*.csproj ./RouteBaseWeb/
RUN dotnet restore RouteBaseWeb/RouteBaseWeb.csproj
# .npmrc too — it carries legacy-peer-deps (see the file); without it `npm ci` ERESOLVEs here while
# the local build, which runs inside RouteBaseWeb/, still passes.
COPY RouteBaseWeb/package.json RouteBaseWeb/package-lock.json RouteBaseWeb/.npmrc ./RouteBaseWeb/
RUN cd RouteBaseWeb && npm ci

COPY . .

# Cap Node heap so a Vite spike fails the build instead of OOM-killing the server
ENV NODE_OPTIONS=--max-old-space-size=1024

# -r linux-x64 + --self-contained false override the csproj's publish-only
# win-x64/self-contained block; ReadyToRun off (RAM-heavy, JIT is fine here)
RUN dotnet publish RouteBaseWeb/RouteBaseWeb.csproj -c Release -o /app \
    --no-restore --self-contained false -r linux-x64 \
    -p:PublishReadyToRun=false -m:1

# Ship no source maps. vite.config.esm.ts keeps sourcemap:true on purpose (file:line still resolves
# when debugging locally), but /js must be reachable WITHOUT the reverse proxy's Basic Auth for share
# links to work at all — and an anonymous /js is an anonymous /js/**/*.map, i.e. the whole TypeScript
# source. Stripping here keeps local debugging intact and publishes only minified bundles.
# Cost: with devtools open a browser 404s on each sourceMappingURL. That is the intended trade.
RUN find /app/wwwroot -name '*.map' -delete

# The desktop-only appsettings.Local.json must NEVER reach the server image: it is the localhost-mode
# switch, and a file that made it here would ship the "auth off" flag alongside the deployment. The
# csproj doesn't publish it today — this fails the image build loudly if that ever changes.
RUN [ ! -e /app/appsettings.Local.json ] || { echo "ERROR: appsettings.Local.json must not be published into the server image" >&2; exit 1; }

# 2. Runtime stage — pinned for the same reason as the SDK above. 10.0.11 is the runtime that
# ships with SDK 10.0.400, so the app runs on exactly what it was compiled against.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.11 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:5054
# Workstation GC: lower memory footprint for a 2 vCPU / low-RAM server
ENV DOTNET_gcServer=0
EXPOSE 5054
ENTRYPOINT ["dotnet", "RouteBaseWeb.dll"]