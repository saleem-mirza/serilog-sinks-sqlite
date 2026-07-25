# Serilog.Sinks.SQLite

[![CI](https://github.com/saleem-mirza/serilog-sinks-sqlite/actions/workflows/ci.yml/badge.svg)](https://github.com/saleem-mirza/serilog-sinks-sqlite/actions/workflows/ci.yml)

A lightweight, high-performance Serilog sink that writes to a SQLite database.

## Getting started

Install [Serilog.Sinks.SQLite](https://www.nuget.org/packages/Serilog.Sinks.SQLite) from NuGet:

```PowerShell
Install-Package Serilog.Sinks.SQLite
```

Configure the logger with `WriteTo.SQLite()`:

```csharp
var logger = new LoggerConfiguration()
    .WriteTo.SQLite(@"Logs\log.db")
    .CreateLogger();

logger.Information("This informational message will be written to SQLite database");
```

## Configuration

```csharp
.WriteTo.SQLite(
    sqliteDbPath:       "Logs/log.db",
    tableName:          "Logs",
    storeTimestampInUtc: false,
    retentionPeriod:    TimeSpan.FromDays(7),     // default null (no expiry)
    batchSize:          100,                       // 1..1000
    maxDatabaseSize:    10,                        // MB, capped at ~20 GB
    rollOver:           true,                      // create sibling DB when full
    journalMode:        SqliteJournalMode.Wal)     // see notes below
```

### Journal mode

The default is **WAL** (write-ahead log), which is crash-safe with concurrent readers. SQLite persists WAL in the database file, so the first run against an older database converts it. To opt out of WAL, set `journalMode: SqliteJournalMode.Memory` (fast, but corrupts on crash) or `Delete` (legacy default).

### Buffer overflow

The sink holds events in a 100,000-entry in-memory queue while it flushes batches. When the queue fills (slow disk, stuck writer), the sink drops additional events and reports the running drop count through Serilog's `SelfLog` at 1, 1000, 2000, … events.

### Roll-over

When the database reaches `maxDatabaseSize`, the sink uses `VACUUM INTO` to produce an atomic sibling backup named `<name>-yyyyMMdd_HHmmss.ff-<guid>.db`, then truncates and reuses the original file. The backup captures sidecar WAL contents. Set `rollOver: false` to drop overflowing batches instead.

## XML `<appSettings>` configuration

To use the SQLite sink with the [Serilog.Settings.AppSettings](https://www.nuget.org/packages/Serilog.Settings.AppSettings) package:

```PowerShell
Install-Package Serilog.Settings.AppSettings
```

In your code:

```csharp
var logger = new LoggerConfiguration()
    .ReadFrom.AppSettings()
    .CreateLogger();
```

In `App.config` / `Web.config`:

```XML
<appSettings>
    <add key="serilog:using:SQLite" value="Serilog.Sinks.SQLite"/>
    <add key="serilog:write-to:SQLite.sqliteDbPath" value="Logs\log.db"/>
    <add key="serilog:write-to:SQLite.tableName" value="Logs"/>
    <add key="serilog:write-to:SQLite.storeTimestampInUtc" value="true"/>
</appSettings>
```

## Performance

The sink buffers events internally and flushes to SQLite in batches on a dedicated thread. It serialises properties once outside the database transaction, so I/O bounds commit latency rather than JSON work. When you configure a `retentionPeriod`, the sink creates `IX_<table>_Timestamp` so the periodic delete runs in O(log n) instead of a full scan.

## Breaking changes in 7.0

- Targets `netstandard2.0` + `net8.0`; `net7.0` dropped.
- `Serilog` 4.x required.
- Storage layer switched to **`Microsoft.Data.Sqlite`** (was `System.Data.SQLite`), cross-platform with no native interop quirks on Linux/macOS.
- **`System.Text.Json`** now produces properties JSON (was `Newtonsoft.Json`). Output uses the relaxed encoder to keep payloads human-readable, so it no longer escapes `<`, `>`, `&`, `'` to `\u00xx`.
- `Exception` and `Properties` columns store `NULL` when the event has none (previously empty strings).
