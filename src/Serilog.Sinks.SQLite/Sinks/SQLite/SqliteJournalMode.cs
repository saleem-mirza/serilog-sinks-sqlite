// Copyright 2016 Serilog Contributors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

namespace Serilog.Sinks.SQLite
{
    /// <summary>
    /// SQLite journal mode passed through to a <c>PRAGMA journal_mode</c> statement.
    /// Selecting a value other than <see cref="Wal"/> trades crash safety, concurrency,
    /// or storage characteristics for raw write throughput.
    /// </summary>
    public enum SqliteJournalMode
    {
        /// <summary>
        /// Rollback journal is deleted after each commit. Safe; default SQLite behaviour
        /// before WAL. Writers block readers.
        /// </summary>
        Delete,

        /// <summary>
        /// Rollback journal is truncated rather than deleted after each commit.
        /// Slightly faster than <see cref="Delete"/> on file systems where truncate is cheap.
        /// </summary>
        Truncate,

        /// <summary>
        /// Rollback journal header is overwritten (rather than the file deleted) after each commit.
        /// </summary>
        Persist,

        /// <summary>
        /// Rollback journal is held in memory. Fastest of the rollback modes but the database
        /// can be left corrupt if the process or host crashes mid-transaction.
        /// </summary>
        Memory,

        /// <summary>
        /// Write-Ahead Log mode (recommended default). Crash-safe with concurrent readers
        /// (readers do not block writers and vice versa). Persisted in the database file.
        /// </summary>
        Wal,

        /// <summary>
        /// No journal kept. Commits cannot be rolled back; a crash mid-transaction will corrupt the database.
        /// Only appropriate for ephemeral databases that can be recreated.
        /// </summary>
        Off
    }
}
