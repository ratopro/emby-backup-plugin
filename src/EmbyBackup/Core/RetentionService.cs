using System;
using System.Collections.Generic;
using System.Linq;

namespace EmbyBackup.Core
{
    public sealed class RetentionResult
    {
        public int Kept { get; set; }
        public List<string> Deleted { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>
    /// Removes old backups created by this plugin. Files that cannot be proven to belong to the plugin are
    /// never deleted: a valid manifest in the archive (folders) or a valid .sha256 sidecar (FTP).
    /// </summary>
    public sealed class RetentionService
    {
        public RetentionResult Apply(IBackupDestination destination, int keep, Action<string> log)
        {
            var result = new RetentionResult();
            log = log ?? (_ => { });

            if (destination == null)
            {
                return result;
            }

            keep = Math.Max(1, Math.Min(365, keep));

            List<BackupDestinationFile> archives;
            try
            {
                archives = destination.ListArchives()
                    .Where(destination.IsOwned)
                    .OrderByDescending(GetStamp)
                    .ThenByDescending(a => a.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                result.Warnings.Add("Could not inspect the backup destination for retention: " + ex.Message);
                return result;
            }

            result.Kept = Math.Min(keep, archives.Count);
            foreach (var archive in archives.Skip(keep))
            {
                try
                {
                    destination.Delete(archive.Name, log);
                    result.Deleted.Add(archive.Name);
                }
                catch (Exception ex)
                {
                    result.Warnings.Add("Could not remove old backup " + archive.Name + ": " + ex.Message);
                }
            }

            DeleteStalePartials(destination, result, log);
            return result;
        }

        private static DateTime GetStamp(BackupDestinationFile file)
        {
            DateTime stamp;
            if (BackupFormat.TryParseArchiveStamp(file.Name, out stamp))
            {
                return stamp;
            }

            return file.ModifiedUtc == DateTime.MinValue ? DateTime.MaxValue : file.ModifiedUtc;
        }

        private static void DeleteStalePartials(IBackupDestination destination, RetentionResult result, Action<string> log)
        {
            try
            {
                foreach (var file in destination.ListAll())
                {
                    if (string.IsNullOrEmpty(file.Name)
                        || !file.Name.StartsWith(BackupConstants.ArchivePrefix, StringComparison.OrdinalIgnoreCase)
                        || !file.Name.EndsWith(
                            BackupConstants.ArchiveSuffix + BackupConstants.PartialSuffix,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var stamp = GetStamp(file);
                    if (stamp == DateTime.MaxValue || (DateTime.UtcNow - stamp).TotalHours < 24)
                    {
                        continue;
                    }

                    destination.Delete(file.Name, log);
                    result.Deleted.Add(file.Name);
                    log("Removed an unfinished backup: " + file.Name);
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add("Could not clean unfinished backups: " + ex.Message);
            }
        }
    }
}
