using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace EmbyBackup.Core
{
    /// <summary>
    /// Runs one complete backup: inventory, consistent SQLite snapshots, ZIP, verification, atomic publish,
    /// history and retention. It does not depend on Emby types so it can be tested without a server.
    /// </summary>
    public sealed class BackupService
    {
        private readonly BackupVerifier _verifier = new BackupVerifier();
        private readonly RetentionService _retention = new RetentionService();

        public BackupRunResult Run(BackupRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var startedUtc = DateTime.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            var warnings = new List<string>();
            var result = new BackupRunResult();
            var log = request.Log ?? (_ => { });
            var progress = request.Progress ?? ((p, s) => { });

            string workDirectory = null;
            string partialPath = null;
            string finalPath = null;
            string finalName = null;
            string destination = null;
            var published = false;

            try
            {
                Validate(request);
                destination = Path.GetFullPath(request.DestinationPath);
                progress(1, "Preparing");

                var stamp = BackupFormat.FileStamp(startedUtc);
                var baseName = BackupConstants.ArchivePrefix + stamp;
                finalName = baseName + BackupConstants.ArchiveSuffix;
                var collision = 1;
                while (File.Exists(Path.Combine(destination, finalName))
                    || File.Exists(Path.Combine(destination, finalName + BackupConstants.PartialSuffix)))
                {
                    finalName = baseName + "-" + collision.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + BackupConstants.ArchiveSuffix;
                    collision++;
                }

                finalPath = Path.Combine(destination, finalName);
                partialPath = finalPath + BackupConstants.PartialSuffix;

                workDirectory = CreateWorkDirectory(request, warnings, log);
                CleanupStaleWorkDirectories(request, log);

                log("Starting backup of " + request.ProgramDataPath + " to " + destination);

                var exclusions = AdditionalExclusions(request);
                var scanner = new BackupInventoryScanner(request.ProgramDataPath, exclusions);
                progress(4, "Scanning Emby data");
                var inventory = scanner.Scan();
                warnings.AddRange(inventory.Warnings);
                log("Found " + inventory.Files.Count + " files and " + inventory.Databases.Count + " SQLite databases.");

                if (inventory.Files.Count == 0 && inventory.Databases.Count == 0)
                {
                    throw new InvalidOperationException(
                        "There is nothing to back up. Check that the Emby program data folder is correct and not excluded.");
                }

                var estimatedBytes = inventory.Files.Sum(f => f.Length) + inventory.Databases.Sum(f => f.Length);
                CheckFreeSpace(destination, estimatedBytes, warnings);

                var sources = new List<ArchiveSource>();
                foreach (var file in inventory.Files)
                {
                    sources.Add(new ArchiveSource
                    {
                        RelativePath = file.RelativePath,
                        FullPath = file.FullPath,
                        LastWriteUtc = file.LastWriteUtc,
                        IsDatabase = false,
                    });
                }

                var snapshots = new SqliteSnapshotService(request.BusyTimeoutSeconds, log);
                var databaseFiles = inventory.Databases.Count;
                for (var i = 0; i < databaseFiles; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var database = inventory.Databases[i];
                    var databaseProgress = databaseFiles == 0 ? 30 : 10 + (20.0 * i / databaseFiles);
                    progress(databaseProgress, "Snapshotting " + database.RelativePath);

                    if (!File.Exists(database.FullPath))
                    {
                        warnings.Add("Skipped a database that disappeared during the backup: " + database.RelativePath);
                        continue;
                    }

                    var snapshotPath = Path.Combine(
                        workDirectory,
                        "db",
                        database.RelativePath.Replace('/', Path.DirectorySeparatorChar));

                    var snapshot = snapshots.Create(database.FullPath, snapshotPath, cancellationToken);
                    sources.Add(new ArchiveSource
                    {
                        RelativePath = database.RelativePath,
                        FullPath = snapshot.Path,
                        LastWriteUtc = database.LastWriteUtc,
                        IsDatabase = true,
                        DatabaseSqliteVersion = snapshot.SqliteVersion,
                        DatabaseQuickCheck = snapshot.QuickCheck,
                    });
                }

                cancellationToken.ThrowIfCancellationRequested();
                progress(33, "Building archive");

                var builder = new BackupArchiveBuilder(log, (percent, message) =>
                {
                    var mapped = 33 + (percent * 0.55);
                    progress(Math.Min(88, mapped), message);
                });

                var build = builder.Build(
                    partialPath,
                    request.PluginVersion,
                    request.EmbyVersion,
                    request.ProgramDataPath,
                    request.Compression,
                    sources,
                    inventory.ExcludedPaths.Concat(exclusions),
                    warnings,
                    cancellationToken);

                warnings = build.Manifest.Warnings == null
                    ? warnings
                    : new List<string>(build.Manifest.Warnings);

                if (request.VerifyAfterCreate)
                {
                    progress(90, "Verifying archive");
                    var verification = _verifier.Verify(build.ArchivePath, false);
                    if (!verification.Valid)
                    {
                        throw new InvalidDataException("The new archive did not pass verification: " + verification.Error);
                    }

                    result.Verified = true;
                    log("Archive verification passed: " + verification.CheckedFiles + " files, "
                        + verification.CheckedDatabases + " databases.");
                }

                progress(96, "Publishing archive");
                File.Move(partialPath, finalPath);
                partialPath = null;
                published = true;
                WriteSidecar(finalPath, build.Sha256, warnings, log);

                result.Success = true;
                result.ArchivePath = finalPath;
                result.Sha256 = build.Sha256;
                result.Bytes = build.Bytes;
                result.FileCount = build.FileCount;
                result.DatabaseCount = build.DatabaseCount;

                progress(98, "Applying retention");
                var retention = _retention.Apply(destination, request.RetentionCount, log);
                warnings.AddRange(retention.Warnings);
                if (retention.Deleted.Count > 0)
                {
                    log("Retention removed " + retention.Deleted.Count + " old file(s).");
                }

                progress(100, "Backup complete");
                log("Backup complete: " + finalPath + " (" + build.Bytes + " bytes).");
            }
            catch (OperationCanceledException)
            {
                result.Error = "The backup was cancelled.";
                log(result.Error);
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                log("Backup failed: " + ex);
            }
            finally
            {
                if (!published && partialPath != null)
                {
                    TryDeleteFile(partialPath, log);
                }

                if (workDirectory != null)
                {
                    TryDeleteDirectory(workDirectory, log);
                }
            }

            result.Duration = stopwatch.Elapsed;
            result.Warnings = warnings;

            if (destination != null)
            {
                var record = new BackupRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    FileName = published ? finalName : null,
                    Destination = destination,
                    CreatedUtc = BackupFormat.Utc(startedUtc),
                    Status = result.Success
                        ? "success"
                        : (result.Error == "The backup was cancelled." ? "cancelled" : "failed"),
                    Verified = result.Verified,
                    Bytes = result.Bytes,
                    FileCount = result.FileCount,
                    DatabaseCount = result.DatabaseCount,
                    Sha256 = result.Sha256,
                    DurationSeconds = Math.Round(result.Duration.TotalSeconds, 1),
                    Error = result.Error,
                    Warnings = new List<string>(warnings),
                };

                BackupHistoryStore.Append(request.PluginDataPath, record);
            }

            return result;
        }

        private static void Validate(BackupRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.ProgramDataPath))
            {
                throw new InvalidOperationException("Emby's program data folder could not be resolved.");
            }

            if (!Directory.Exists(request.ProgramDataPath))
            {
                throw new InvalidOperationException("Emby's program data folder does not exist: " + request.ProgramDataPath);
            }

            PathSafety.ValidateBackupDestination(request.ProgramDataPath, request.DestinationPath);
            PathSafety.EnsureDestinationWritable(request.DestinationPath);
        }

        private static List<string> AdditionalExclusions(BackupRequest request)
        {
            var exclusions = new List<string> { BackupConstants.WorkDirectoryName };

            if (!string.IsNullOrWhiteSpace(request.PluginDataPath)
                && PathSafety.IsSameOrSubPath(request.ProgramDataPath, request.PluginDataPath))
            {
                try
                {
                    var relative = PathSafety.RelativeTo(request.ProgramDataPath, request.PluginDataPath);
                    // Skip this plugin's own data folder (history and temporary snapshots). The settings live
                    // in plugins/configurations and are still archived; a stale history is useless in a restore.
                    exclusions.Add(relative);
                }
                catch
                {
                    // The plugin data folder is not below ProgramData; nothing to exclude.
                }
            }

            return exclusions;
        }

        private static string CreateWorkDirectory(BackupRequest request, List<string> warnings, Action<string> log)
        {
            var root = WorkRoot(request);
            var name = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            try
            {
                Directory.CreateDirectory(root);
                var path = Path.Combine(root, name);
                Directory.CreateDirectory(path);
                return path;
            }
            catch (Exception ex)
            {
                warnings.Add("Could not create the temporary work folder: " + ex.Message);
                throw new InvalidOperationException(
                    "Could not create a temporary folder for database snapshots in " + root + ": " + ex.Message, ex);
            }
        }

        private static string WorkRoot(BackupRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.PluginDataPath))
            {
                return Path.Combine(request.PluginDataPath, "backup-work");
            }

            return Path.Combine(Path.GetTempPath(), BackupConstants.WorkDirectoryName);
        }

        private static void CleanupStaleWorkDirectories(BackupRequest request, Action<string> log)
        {
            try
            {
                var root = WorkRoot(request);
                if (!Directory.Exists(root))
                {
                    return;
                }

                foreach (var directory in Directory.GetDirectories(root))
                {
                    try
                    {
                        var info = new DirectoryInfo(directory);
                        if ((DateTime.UtcNow - info.LastWriteTimeUtc).TotalHours < 24)
                        {
                            continue;
                        }

                        info.Delete(true);
                        log("Removed an old temporary backup folder: " + info.Name);
                    }
                    catch
                    {
                        // A folder still in use by another instance is not worth failing this backup over.
                    }
                }
            }
            catch
            {
                // Cleanup is best effort.
            }
        }

        private static void CheckFreeSpace(string destination, long estimatedBytes, List<string> warnings)
        {
            try
            {
                var root = Path.GetPathRoot(destination);
                if (string.IsNullOrEmpty(root))
                {
                    return;
                }

                var drive = new DriveInfo(root);
                if (!drive.IsReady)
                {
                    return;
                }

                const long reserve = 128L * 1024 * 1024;
                if (drive.AvailableFreeSpace < estimatedBytes + reserve)
                {
                    warnings.Add(
                        "The backup destination may not have enough free space. Estimated input: "
                        + (estimatedBytes / (1024 * 1024)) + " MB, available: "
                        + (drive.AvailableFreeSpace / (1024 * 1024)) + " MB.");
                }
            }
            catch
            {
                // UNC paths and some network shares do not support DriveInfo.
            }
        }

        private static void WriteSidecar(string archivePath, string sha256, List<string> warnings, Action<string> log)
        {
            try
            {
                var sidecarPath = archivePath + BackupConstants.SidecarSuffix;
                var temp = sidecarPath + ".tmp";
                File.WriteAllText(temp, sha256 + "  " + Path.GetFileName(archivePath) + Environment.NewLine);
                if (File.Exists(sidecarPath))
                {
                    File.Delete(sidecarPath);
                }
                File.Move(temp, sidecarPath);
            }
            catch (Exception ex)
            {
                warnings.Add("The archive was created but its .sha256 file could not be written: " + ex.Message);
                log("Could not write the .sha256 file: " + ex);
            }
        }

        private static void TryDeleteFile(string path, Action<string> log)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                log("Could not remove the unfinished archive " + path + ": " + ex.Message);
            }
        }

        private static void TryDeleteDirectory(string path, Action<string> log)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch (Exception ex)
            {
                log("Could not remove the temporary folder " + path + ": " + ex.Message);
            }
        }
    }
}
