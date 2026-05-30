using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Serilog;
using Serilog.Sinks.SQLite;

namespace SmokeTest;

internal static class Program
{
    private const int EventCount = 10000;

    private static int Main()
    {
        var scenarios = new (string Name, Func<bool> Run)[]
        {
            ("happy-path", HappyPath),
            ("rollover", Rollover),
            ("retention-index", RetentionIndex),
        };

        var allOk = true;
        foreach (var (name, run) in scenarios)
        {
            Console.WriteLine($"=== {name} ===");
            try
            {
                if (!run())
                {
                    allOk = false;
                    Console.Error.WriteLine($"--- {name} FAILED ---");
                }
            }
            catch (Exception ex)
            {
                allOk = false;
                Console.Error.WriteLine($"--- {name} THREW: {ex} ---");
            }
        }

        Console.WriteLine(allOk ? "SMOKE PASS" : "SMOKE FAIL");
        return allOk ? 0 : 1;
    }

    private static bool HappyPath()
    {
        var dbPath = Path.Combine(AppContext.BaseDirectory, $"serilog-sqlite-smoke-{Guid.NewGuid():N}.db");
        Console.WriteLine($"db: {dbPath}");
        try
        {
            var logger = new LoggerConfiguration()
                .WriteTo.SQLite(dbPath, batchSize: 50)
                .CreateLogger();

            for (var i = 0; i < EventCount; i++)
            {
                logger.Information("Smoke event {Index} with {@Payload}", i, new { A = i, B = $"text-{i}" });
            }

            ((IDisposable)logger).Dispose();

            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();

            return
                Check(conn, "PRAGMA journal_mode;", v =>
                    string.Equals(v?.ToString(), "wal", StringComparison.OrdinalIgnoreCase)
                        ? null : $"expected wal, got {v}") &&
                Check(conn, "SELECT COUNT(*) FROM Logs;", v =>
                {
                    var n = Convert.ToInt32(v);
                    return n == EventCount ? null : $"expected {EventCount} rows, got {n}";
                }) &&
                Check(conn, "SELECT Properties FROM Logs WHERE Properties IS NOT NULL LIMIT 1;", v =>
                {
                    var s = v as string;
                    if (string.IsNullOrEmpty(s)) return "properties JSON empty";
                    if (!s.Contains("\"Index\"")) return $"missing Index field in: {s}";
                    if (!s.Contains("\"Payload\"")) return $"missing Payload field in: {s}";
                    return null;
                }) &&
                Check(conn, "SELECT RenderedMessage FROM Logs LIMIT 1;", v =>
                    string.IsNullOrEmpty(v as string) ? "rendered message empty" : null);
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    private static bool Rollover()
    {
        // maxDatabaseSize: 1 MB, paired with long messages, triggers SQLITE_FULL → VACUUM INTO roll-over.
        var dbPath = Path.Combine(AppContext.BaseDirectory, $"serilog-sqlite-roll-{Guid.NewGuid():N}.db");
        var dbDir = Path.GetDirectoryName(dbPath);
        var dbStem = Path.GetFileNameWithoutExtension(dbPath);
        Console.WriteLine($"db: {dbPath}");
        try
        {
            var logger = new LoggerConfiguration()
                .WriteTo.SQLite(dbPath, batchSize: 100, maxDatabaseSize: 1)
                .CreateLogger();

            var bigPayload = new string('x', 500);
            for (var i = 0; i < 20_000; i++)
            {
                logger.Information("Roll event {Index} {Payload}", i, bigPayload);
            }

            ((IDisposable)logger).Dispose();
            SqliteConnection.ClearAllPools();

            var siblings = Directory.GetFiles(dbDir, $"{dbStem}-*.db");
            if (siblings.Length == 0)
            {
                Console.Error.WriteLine("FAIL: no rolled sibling DB created");
                return false;
            }
            Console.WriteLine($"OK   (rollover triggered) -> {siblings.Length} sibling file(s)");

            // Verify each sibling is a valid SQLite DB with rows.
            foreach (var sibling in siblings)
            {
                using var conn = new SqliteConnection($"Data Source={sibling};Mode=ReadOnly");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM Logs;";
                var count = Convert.ToInt32(cmd.ExecuteScalar());
                if (count == 0)
                {
                    Console.Error.WriteLine($"FAIL: rolled sibling {Path.GetFileName(sibling)} has 0 rows");
                    return false;
                }
                Console.WriteLine($"OK   (sibling content) -> {count} rows in {Path.GetFileName(sibling)}");
            }

            // Cleanup siblings.
            foreach (var sibling in siblings)
            {
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    var p = sibling + suffix;
                    try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
                }
            }
            return true;
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    private static bool RetentionIndex()
    {
        // Configuring retention should create IX_<table>_Timestamp on the table.
        var dbPath = Path.Combine(AppContext.BaseDirectory, $"serilog-sqlite-retidx-{Guid.NewGuid():N}.db");
        Console.WriteLine($"db: {dbPath}");
        try
        {
            var logger = new LoggerConfiguration()
                .WriteTo.SQLite(dbPath, retentionPeriod: TimeSpan.FromHours(1))
                .CreateLogger();

            ((IDisposable)logger).Dispose();
            SqliteConnection.ClearAllPools();

            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();

            return Check(conn,
                "SELECT name FROM sqlite_master WHERE type='index' AND name='IX_Logs_Timestamp';",
                v => v is string name && name == "IX_Logs_Timestamp" ? null : $"index missing, got {v ?? "null"}");
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    private static bool Check(SqliteConnection conn, string sql, Func<object, string> validator)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        var msg = validator(result);
        if (msg != null)
        {
            Console.Error.WriteLine($"FAIL ({sql}): {msg}");
            return false;
        }
        Console.WriteLine($"OK   ({sql}) -> {result}");
        return true;
    }

    private static void Cleanup(string dbPath)
    {
        try
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var path = dbPath + suffix;
                if (File.Exists(path)) File.Delete(path);
            }
        }
        catch
        {
            // best-effort
        }
    }
}
