# LS Monitoring Agent Instructions

## Project Context

LS Monitoring is a desktop monitoring tool for LoadSensing / Worldsensing LS-G6 tilt sensor data.
The active implementation is the C#/.NET 9 + Avalonia app in the root solution.

## Working Rules

- Prefer the existing C# solution and patterns in `LsMonitoring.Core/`, `LsMonitoring.Avalonia/`, and `LsMonitoring.Core.Tests/` for new behavior.
- Keep gateway credentials, private sensor logs, and exported customer data out of Git.
- Do not commit local runtime output: `config.json`, `data/`, `logs/`, `dist/`, `.dotnet-cli-home/`, test results, or cache directories. `build/` is local output only.
- Treat `.claude/`, `.codex/`, and other AI-tool state directories as local tool state, not project source.
- Make focused changes in the current .NET solution.

## Commands

Use these from the repository root:

```powershell
dotnet restore .\LsMonitoring.sln
dotnet build .\LsMonitoring.sln
dotnet test .\LsMonitoring.sln
dotnet run --project .\LsMonitoring.Avalonia\LsMonitoring.Avalonia.csproj
```

## Architecture, Release & Gateway Context (from CLAUDE.md + live probe)

- **Distribution & licensing (0.9, ГФ 402):** personal link via `gf-dist`, `GfLicense("ls", "LsMonitoring", …)` (copy of `GfDistribution/client/GfLicense.cs` — never edit here), Inno installer in `Geofundament/GfReleases`, tag `ls-v*`. Velopack removed (`LegacyInstall` cleans old copies). Without a license key: monitoring only (no alerts/Gotify/tunnel/bot; Debug builds count as licensed). Helpers live in `ChildProcessJob`. SMS hidden and off.
- **Alerts architecture:**
  - Telegram runs in companion process `LsMonitoring.TelegramBot/BotHost.cs` (single `getUpdates` poller, localhost HTTP API via `TelegramCompanionService`); shared bot token embedded in Release via CI `TelegramSecrets.Local.cs` (gitignored). Known gap: multi-PC polling race deferred until >1 gateway/user.
  - Email default mode `service` uses embedded SMTP creds (`EmailSecrets.Local.cs`, gitignored); old `AlertRelay` deleted; UI option «Альтернативный SMTP» for custom SMTP.
  - Gotify pinned to v2.9.1 with SHA-256 verification.
- **CMT Edge gateway (`169.254.0.1`, Worldsensing FW 1.5.2, live-confirmed):**
  - HTTPS-only with self-signed cert (TOFU cert pinning in `CsvGatewaySource`), stateless HTTP Basic Auth (no CSRF/login).
  - **No JSON API** (`/dataserver/api/*` → 404). Primary data endpoint: `/dataserver/current/reading/<node_id>` (CSV, `application/octet-stream`; inactive nodes return HTML 404/500 — filter by content-type). `/dataserver/node/view/<node_id>` has latest values, RSSI/SF/Frequency table and authoritative last-contact time.
  - CSV quirk: 9 metadata lines then `"Date-and-time"` header; data rows can have fewer columns than header; `abs(value) > 15.0°` (sentinels ~±18.74°/±18.59°) = sensor overflow → INVALID. `If-Modified-Since` is ignored by server (always 200), but `Last-Modified` header accurately reflects last packet time.
- Подробная история фаз FIX_PLAN — в `CLAUDE.md` и `FIX_PLAN.md`.
