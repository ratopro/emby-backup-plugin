using System;
using System.Collections.Generic;
using System.IO;
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
    /// Removes old backups created by this plugin. Files without a readable Emby Backup manifest are never
    /// deleted, and neither are files whose name does not match this plugin's pattern.
    /// </summary>
    public sealed class RetentionService
    {
        public RetentionResult Apply(string destinationPath, int keep, Action<string> log)
        {
            var result = new RetentionResult();
            log = log ?? (_ => { });

            if (string.IsNullOrWhiteSpace(destinationPath) || !Directory.Exists(destinationPath))
            {
                return result;
            }

            keep = Math.Max(1, Math.Min(365, keep));

            List<BackupArchiveInfo> archives;
            try
            {
                archives = BackupCatalog.List(destinationPath)
                    .Where(a => a.IsEmbyBackup && a.HasManifest)
                    .OrderByDescending(a => a.CreatedUtcValue)
                    .ThenByDescending(a => a.FileName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                result.Warnings.Add("Could not inspect the backup folder for retention: " + ex.Message);
                return result;
            }

            result.Kept = Math.Min(keep, archives.Count);
            foreach (var archive in archives.Skip(keep))
            {
                try
                {
                    if (File.Exists(archive.FullPath))
                    {
                        File.Delete(archive.FullPath);
                        result.Deleted.Add(archive.FileName);
                        log("Retention removed old backup: " + archive.FileName);
                    }

                    DeleteSidecar(archive.FullPath);
                }
                catch (Exception ex)
                {
                    result.Warnings.Add("Could not remove old backup " + archive.FileName + ": " + ex.Message);
                }
            }

            DeleteStalePartials(destinationPath, result, log);
            return result;
        }

        public static void DeleteSidecar(string archivePath)
        {
            try
            {
                var sidecar = archivePath + BackupConstants.SidecarSuffix;
                if (File.Exists(sidecar))
                {
                    File.Delete(sidecar);
                }
            }
            catch
            {
                // The ZIP is already gone; a stray .sha256 file is harmless.
            }
        }

        private static void DeleteStalePartials(string destinationPath, RetentionResult result, Action<string> log)
        {
            try
            {
                foreach (var path in Directory.GetFiles(
                    destinationPath,
                    BackupConstants.ArchivePrefix + "*" + BackupConstants.ArchiveSuffix + BackupConstants.PartialSuffix,
                    SearchOption.TopDirectoryOnly))
                {
                    var file = new FileInfo(path);
                    if ((DateTime.UtcNow - file.LastWriteTimeUtc).TotalHours < 24)
                    {
                        continue;
                    }

                    file.Delete();
                    result.Deleted.Add(file.Name);
                    log("Retention removed an unfinished backup: " + file.Name);
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add("Could not clean unfinished backups: " + ex.Message);
            }
        }
    }
}
