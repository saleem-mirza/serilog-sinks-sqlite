# Serilog.Sinks.SQLite

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

Default is **WAL** (write-ahead log) — crash-safe with concurrent readers. WAL is persisted in the database file, so the first run against an older database converts it. To opt out of WAL, set
`journalMode: SqliteJournalMode.Memory` (fast, but corrupts on crash) or `Delete` (legacy default).

### Buffer overflow
The sink holds events in a 100 000-entry in-memory queue while batches are being flushed. When the
queue fills (slow disk, stuck writer), additional events are dropped and the running drop count is
reported through Serilog's `SelfLog` at 1, 1000, 2000, … events.

### Roll-over

When the database reaches `maxDatabaseSize`, the sink uses `VACUUM INTO` to produce an atomic
sibling backup named `<name>-yyyyMMdd_HHmmss.ff-<guid>.db`, then truncates and reuses the
original file. Sidecar WAL contents are captured. Disable with `rollOver: false` to instead drop
overflowing batches.

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

The sink buffers events internally and flushes to SQLite in batches on a dedicated thread.
Properties are serialised once outside the database transaction, so commit latency is bounded by I/O rather than JSON work. When a `retentionPeriod` is configured, the sink also creates
`IX_<table>_Timestamp` so the periodic delete is O(log n) instead of a full scan.

## Breaking changes in 7.0

- Targets `netstandard2.0` + `net8.0`; `net7.0` dropped.
- `Serilog` 4.x required.
- Storage layer switched to **`Microsoft.Data.Sqlite`** (was `System.Data.SQLite`). True cross-platform — no native interop quirks on Linux/macOS.
- Properties JSON now produced by **`System.Text.Json`** (was `Newtonsoft.Json`). Output uses the relaxed encoder so payloads stay human-readable, but `<`, `>`, `&`, `'` will not be escaped to `\u00xx` anymore.
- `Exception` and `Properties` columns store `NULL` when the event has none (previously empty strings).
