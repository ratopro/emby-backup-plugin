using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EmbyBackup.Core
{
    /// <summary>
    /// Small JSON history kept in the plugin's data folder. The ZIP files are the source of truth; this file only
    /// makes the settings page useful after a restart and records failures as well as successes.
    /// </summary>
    public static class BackupHistoryStore
    {
        public const int MaximumRecords = 100;

        private static readonly object Sync = new object();

        public static string PathFor(string pluginDataPath)
        {
            return System.IO.Path.Combine(pluginDataPath, "history.json");
        }

        public static List<BackupRecord> Read(string pluginDataPath)
        {
            if (string.IsNullOrWhiteSpace(pluginDataPath))
            {
                return new List<BackupRecord>();
            }

            lock (Sync)
            {
                var history = JsonCodec.Read<BackupHistory>(PathFor(pluginDataPath));
                if (history == null || history.Backups == null)
                {
                    return new List<BackupRecord>();
                }

                return history.Backups;
            }
        }

        public static BackupRecord MostRecent(string pluginDataPath)
        {
            return Read(pluginDataPath)
                .OrderByDescending(r => ParseCreated(r.CreatedUtc))
                .FirstOrDefault();
        }

        public static void Append(string pluginDataPath, BackupRecord record)
        {
            if (string.IsNullOrWhiteSpace(pluginDataPath) || record == null)
            {
                return;
            }

            lock (Sync)
            {
                var history = JsonCodec.Read<BackupHistory>(PathFor(pluginDataPath));
                if (history == null)
                {
                    history = new BackupHistory();
                }
                if (history.Backups == null)
                {
                    history.Backups = new List<BackupRecord>();
                }

                history.Backups.Add(record);
                history.Backups = history.Backups
                    .OrderByDescending(r => ParseCreated(r.CreatedUtc))
                    .Take(MaximumRecords)
                    .ToList();

                try
                {
                    JsonCodec.Write(PathFor(pluginDataPath), history);
                }
                catch
                {
                    // A backup that was written correctly must not be reported as failed because its history
                    // entry could not be saved.
                }
            }
        }

        public static DateTime ParseCreated(string value)
        {
            if (DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed))
            {
                return parsed;
            }

            return DateTime.MinValue;
        }
    }
}
