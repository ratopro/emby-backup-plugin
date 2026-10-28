using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace EmbyBackup.Core
{
    public sealed class RestoreRequest
    {
        public string ProgramDataPath { get; set; }
        public string PluginDataPath { get; set; }
        public BackupDestinationSettings Destination { get; set; }
        public string FileName { get; set; }
        public string Password { get; set; }
        public int BusyTimeoutSeconds { get; set; } = 60;
        public Action<string> Log { get; set; }
        public Action<double, string> Progress { get; set; }
    }

    public sealed class RestoreCheckResult
    {
        public bool Success { get; set; }
        public bool Valid { get; set; }
        public bool Encrypted { get; set; }
        public bool PasswordRequired { get; set; }
        public bool WrongPassword { get; set; }
        public string Error { get; set; }
        public string FileName { get; set; }
        public string CreatedUtc { get; set; }
        public string EmbyVersion { get; set; }
        public string PluginVersion { get; set; }
        public string SourceProgramDataPath { get; set; }
        public int FileCount { get; set; }
        public int DatabaseCount { get; set; }
        public long TotalBytes { get; set; }
    }

    public sealed class RestoreRunResult
    {
        public bool Success { get; set; }
        public bool Applied { get; set; }
        public bool RestartRequired { get; set; }
        public bool PasswordRequired { get; set; }
        public bool WrongPassword { get; set; }
        public string Error { get; set; }
        public string FileName { get; set; }
        public int RestoredFiles { get; set; }
        public int RestoredDatabases { get; set; }
        public long RestoredBytes { get; set; }
        public TimeSpan Duration { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>
    /// Restores files and SQLite databases from a backup archive. Everything is extracted and checked in a
    /// staging folder first, so a wrong password, a damaged archive or a path traversal attempt never touches
    /// Emby's program data. Databases are applied with SQLite's online backup API, which works while Emby is
    /// running; Emby must be restarted afterwards to reload configuration and caches.
    /// </summary>
    public sealed class RestoreService
    {
        private const string PluginConfigurationPath = "plugins/configurations/EmbyBackup.xml";

        private readonly SqliteSnapshotService _sqlite;

        public RestoreService(int busyTimeoutSeconds, Action<string> log)
        {
            _sqlite = new SqliteSnapshotService(busyTimeoutSeconds, log);
        }

        public RestoreCheckResult Check(RestoreRequest request)
        {
            var result = new RestoreCheckResult();
            if (request == null)
            {
                result.Error = "No restore request was supplied.";
                return result;
            }

            result.FileName = request.FileName;
            string localPath = null;
            IBackupDestination destination = null;
            try
            {
                ValidateRequest(request);
                destination = BackupDestinations.Create(request.Destination, request.ProgramDataPath,
                    request.Log);
                TryPrepare(destination, request.Log);
                localPath = destination.Fetch(request.FileName, "restore-check");
                if (!File.Exists(localPath))
                {
                    throw new FileNotFoundException("The backup file was not found in the destination.");
                }

                using (var reader = new ZipReader(localPath))
                {
                    result.Encrypted = reader.IsEncrypted;
                    if (result.Encrypted && string.IsNullOrEmpty(request.Password))
                    {
                        result.Success = true;
                        result.PasswordRequired = true;
                        result.Error = "This backup is protected by a password.";
                        return result;
                    }

                    BackupManifest manifest;
                    try
                    {
                        manifest = ReadManifest(reader, request.Password);
                    }
                    catch (ZipPasswordRequiredException ex)
                    {
                        result.Success = true;
                        result.PasswordRequired = true;
                        result.Error = ex.Message;
                        return result;
                    }
                    catch (ZipWrongPasswordException ex)
                    {
                        result.Success = false;
                        result.WrongPassword = true;
                        result.Error = ex.Message;
                        return result;
                    }
                    catch (ZipAuthenticationException ex)
                    {
                        result.Success = false;
                        result.WrongPassword = true;
                        result.Error = ex.Message;
                        return result;
                    }

                    ValidateManifest(manifest);
                    BuildExpectedEntries(manifest);
                    result.Success = true;
                    result.Valid = true;
                    result.CreatedUtc = manifest.CreatedUtc;
                    result.EmbyVersion = manifest.EmbyVersion;
                    result.PluginVersion = manifest.PluginVersion;
                    result.SourceProgramDataPath = manifest.SourceProgramDataPath;
                    result.FileCount = manifest.Files == null ? 0 : manifest.Files.Count;
                    result.DatabaseCount = manifest.Databases == null ? 0 : manifest.Databases.Count;
                    result.TotalBytes = manifest.TotalBytes;
                    return result;
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
                return result;
            }
            finally
            {
                if (destination != null)
                {
                    destination.Release(localPath);
                    destination.Dispose();
                }
            }
        }

        public RestoreRunResult Run(RestoreRequest request, CancellationToken cancellationToken)
        {
            var result = new RestoreRunResult();
            var stopwatch = Stopwatch.StartNew();
            var log = request == null ? (Action<string>)(_ => { }) : (request.Log ?? (_ => { }));
            var progress = request == null ? (Action<double, string>)((p, s) => { }) : (request.Progress ?? ((p, s) => { }));

            if (request == null)
            {
                result.Error = "No restore request was supplied.";
                return result;
            }

            result.FileName = request.FileName;
            string workDirectory = null;
            string localPath = null;
            IBackupDestination destination = null;
            var stagedFiles = new List<StagedFile>();
            var stagedDatabases = new List<StagedFile>();
            var manifest = (BackupManifest)null;

            try
            {
                ValidateRequest(request);
                if (string.IsNullOrWhiteSpace(request.ProgramDataPath) || !Directory.Exists(request.ProgramDataPath))
                {
                    throw new InvalidOperationException("Emby's program data folder does not exist: "
                        + request.ProgramDataPath);
                }

                workDirectory = CreateWorkDirectory(request, log);
                var stagingRoot = Path.Combine(workDirectory, "staging");
                Directory.CreateDirectory(stagingRoot);

                destination = BackupDestinations.Create(request.Destination, request.ProgramDataPath, request.Log);
                TryPrepare(destination, request.Log);

                progress(1, "Fetching the backup archive");
                localPath = destination.Fetch(request.FileName, "restore");
                if (!File.Exists(localPath))
                {
                    throw new FileNotFoundException("The backup file was not found in the destination.");
                }

                progress(4, "Reading the backup manifest");
                using (var reader = new ZipReader(localPath))
                {
                    if (reader.IsEncrypted && string.IsNullOrEmpty(request.Password))
                    {
                        throw new ZipPasswordRequiredException();
                    }

                    manifest = ReadManifest(reader, request.Password);
                    ValidateManifest(manifest);

                    var expected = BuildExpectedEntries(manifest);
                    foreach (var entry in reader.Entries)
                    {
                        if (!expected.Contains(entry.Name))
                        {
                            throw new InvalidDataException("Unexpected file in archive: " + entry.Name);
                        }
                    }

                    var totalBytes = Math.Max(1, manifest.TotalBytes);
                    var doneBytes = 0L;

                    var fileEntries = manifest.Files ?? new List<BackupFileEntry>();
                    foreach (var file in fileEntries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var entry = reader.Find(file.Path);
                        if (entry == null)
                        {
                            throw new InvalidDataException("Missing from archive: " + file.Path);
                        }

                        var staged = Path.Combine(stagingRoot, "files",
                            file.Path.Replace('/', Path.DirectorySeparatorChar));
                        ExtractEntry(reader, entry, request.Password, staged, file.Bytes, file.Sha256,
                            cancellationToken);
                        stagedFiles.Add(new StagedFile
                        {
                            RelativePath = file.Path,
                            StagedPath = staged,
                            LastWriteUtc = ParseManifestTime(file.LastWriteUtc) ?? entry.LastWriteUtc,
                        });

                        doneBytes += Math.Max(0, file.Bytes);
                        progress(4 + (46.0 * doneBytes / totalBytes),
                            "Checking " + file.Path);
                    }

                    var databaseEntries = manifest.Databases ?? new List<BackupDatabaseEntry>();
                    foreach (var database in databaseEntries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var entry = reader.Find(database.Path);
                        if (entry == null)
                        {
                            throw new InvalidDataException("Missing from archive: " + database.Path);
                        }

                        var staged = Path.Combine(stagingRoot, "databases",
                            database.Path.Replace('/', Path.DirectorySeparatorChar));
                        ExtractEntry(reader, entry, request.Password, staged, database.Bytes, database.Sha256,
                            cancellationToken);
                        stagedDatabases.Add(new StagedFile
                        {
                            RelativePath = database.Path,
                            StagedPath = staged,
                            LastWriteUtc = entry.LastWriteUtc,
                        });

                        doneBytes += Math.Max(0, database.Bytes);
                        progress(4 + (46.0 * doneBytes / totalBytes),
                            "Checking " + database.Path);
                    }
                }

                progress(51, "The archive is valid; preparing to apply it");
                result.Applied = true;
                var applyTotal = Math.Max(1, stagedFiles.Count + stagedDatabases.Count);
                var applied = 0;

                foreach (var file in stagedFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = PathSafety.NormalizeRelative(file.RelativePath);
                    if (IsPluginConfiguration(relative))
                    {
                        result.Warnings.Add(
                            "Kept the current Emby Backup settings instead of restoring " + relative + ".");
                        applied++;
                        continue;
                    }

                    var target = SafeTarget(request.ProgramDataPath, relative);
                    EnsureNoRedirectedDirectory(request.ProgramDataPath, relative);
                    ApplyFile(file.StagedPath, target);
                    TrySetTimestamp(target, file.LastWriteUtc);
                    result.RestoredFiles++;
                    applied++;
                    progress(51 + (44.0 * applied / applyTotal), "Restoring " + relative);
                }

                foreach (var database in stagedDatabases)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = PathSafety.NormalizeRelative(database.RelativePath);
                    var target = SafeTarget(request.ProgramDataPath, relative);
                    EnsureNoRedirectedDirectory(request.ProgramDataPath, relative);
                    log("Restoring database " + relative);
                    var check = _sqlite.Restore(database.StagedPath, target, cancellationToken);
                    if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Warnings.Add("SQLite quick_check after restoring " + relative + ": " + check);
                    }
                    TrySetTimestamp(target, database.LastWriteUtc);
                    result.RestoredDatabases++;
                    applied++;
                    progress(51 + (44.0 * applied / applyTotal), "Restoring database " + relative);
                }

                result.RestoredBytes = manifest.TotalBytes;
                result.Success = true;
                result.RestartRequired = true;
                progress(100, "Restore complete. Restart Emby to load the restored data.");
                log("Restore complete: " + result.RestoredFiles + " files and " + result.RestoredDatabases
                    + " databases. Emby must be restarted.");
            }
            catch (OperationCanceledException)
            {
                result.Error = "The restore was cancelled.";
                log(result.Error);
            }
            catch (ZipPasswordRequiredException ex)
            {
                result.PasswordRequired = true;
                result.Error = ex.Message;
                log(result.Error);
            }
            catch (ZipWrongPasswordException ex)
            {
                result.WrongPassword = true;
                result.Error = ex.Message;
                log(result.Error);
            }
            catch (ZipAuthenticationException ex)
            {
                result.WrongPassword = true;
                result.Error = ex.Message;
                log(result.Error);
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                log("Restore failed: " + ex);
            }
            finally
            {
                if (destination != null)
                {
                    destination.Release(localPath);
                    destination.Dispose();
                }

                if (workDirectory != null)
                {
                    TryDeleteDirectory(workDirectory, log);
                }
            }

            result.Duration = stopwatch.Elapsed;
            return result;
        }

        private static void ValidateRequest(RestoreRequest request)
        {
            if (request.Destination == null)
            {
                throw new InvalidOperationException("No backup destination is configured.");
            }

            if (string.IsNullOrWhiteSpace(request.FileName)
                || !BackupCatalog.IsManagedFileName(request.FileName))
            {
                throw new InvalidOperationException("Choose a backup archive created by this plugin.");
            }

            if (string.IsNullOrWhiteSpace(request.ProgramDataPath))
            {
                throw new InvalidOperationException("Emby's program data folder could not be resolved.");
            }
        }

        private static BackupManifest ReadManifest(ZipReader reader, string password)
        {
            var entry = reader.Find(BackupConstants.ManifestEntryName);
            if (entry == null)
            {
                throw new InvalidDataException("manifest.json is missing.");
            }

            var bytes = reader.ReadAllBytes(entry, password, 16 * 1024 * 1024);
            using (var stream = new MemoryStream(bytes, false))
            {
                var manifest = JsonCodec.ReadFromStream<BackupManifest>(stream);
                if (manifest == null)
                {
                    throw new InvalidDataException("manifest.json could not be read.");
                }
                return manifest;
            }
        }

        private static void ValidateManifest(BackupManifest manifest)
        {
            if (manifest == null)
            {
                throw new InvalidDataException("manifest.json could not be read.");
            }

            if (!string.Equals(manifest.Format, BackupConstants.FormatName, StringComparison.Ordinal))
            {
                throw new InvalidDataException("This ZIP was not created by Emby Backup.");
            }

            if (manifest.FormatVersion != BackupConstants.FormatVersion)
            {
                throw new InvalidDataException("Unsupported backup format version: " + manifest.FormatVersion);
            }

            if ((manifest.Files == null || manifest.Files.Count == 0)
                && (manifest.Databases == null || manifest.Databases.Count == 0))
            {
                throw new InvalidDataException("The backup does not contain any files or databases.");
            }
        }

        private static HashSet<string> BuildExpectedEntries(BackupManifest manifest)
        {
            var expected = new HashSet<string>(StringComparer.Ordinal);
            expected.Add(BackupConstants.ManifestEntryName);
            foreach (var file in manifest.Files ?? new List<BackupFileEntry>())
            {
                var path = PathSafety.ToArchivePath(file.Path);
                if (!expected.Add(path))
                {
                    throw new InvalidDataException("Duplicate path in manifest: " + path);
                }
            }
            foreach (var database in manifest.Databases ?? new List<BackupDatabaseEntry>())
            {
                var path = PathSafety.ToArchivePath(database.Path);
                if (!expected.Add(path))
                {
                    throw new InvalidDataException("Duplicate path in manifest: " + path);
                }
            }
            return expected;
        }

        private static void ExtractEntry(
            ZipReader reader,
            ZipEntryInfo entry,
            string password,
            string destinationPath,
            long expectedBytes,
            string expectedSha,
            CancellationToken cancellationToken)
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (entry.UncompressedSize != expectedBytes)
            {
                throw new InvalidDataException("Size mismatch for " + entry.Name + ": manifest says "
                    + expectedBytes + ", archive says " + entry.UncompressedSize);
            }

            using (var handle = reader.OpenEntry(entry, password))
            using (var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None,
                1024 * 1024))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[1024 * 1024];
                var total = 0L;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = handle.Stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    output.Write(buffer, 0, read);
                    sha.AppendData(buffer, 0, read);
                    total += read;
                }

                handle.Complete();
                if (total != expectedBytes)
                {
                    throw new InvalidDataException("Size mismatch after extracting " + entry.Name);
                }

                var actual = BackupFormat.Sha256(sha.GetHashAndReset());
                if (!string.Equals(actual, expectedSha, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("SHA-256 mismatch for " + entry.Name);
                }
            }
        }

        private static string SafeTarget(string programDataPath, string relative)
        {
            var normalized = PathSafety.ToArchivePath(relative);
            var target = Path.GetFullPath(Path.Combine(programDataPath,
                normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!PathSafety.IsSameOrSubPath(programDataPath, target)
                || string.Equals(Path.GetFullPath(programDataPath), target, PathSafety.PathComparison))
            {
                throw new InvalidDataException("Unsafe restore path: " + relative);
            }
            return target;
        }

        private static void EnsureNoRedirectedDirectory(string programDataPath, string relative)
        {
            var segments = PathSafety.NormalizeRelative(relative).Split(new[] { '/' },
                StringSplitOptions.RemoveEmptyEntries);
            var current = Path.GetFullPath(programDataPath);
            for (var i = 0; i < segments.Length - 1; i++)
            {
                current = Path.Combine(current, segments[i]);
                if (Directory.Exists(current) && PathSafety.IsReparsePoint(new DirectoryInfo(current)))
                {
                    throw new InvalidDataException(
                        "Refusing to restore through a symbolic link or junction: " + current);
                }
            }
        }

        private static void ApplyFile(string stagedPath, string targetPath)
        {
            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = targetPath + ".embybackup-restore";
            try
            {
                File.Copy(stagedPath, temporary, true);
                if (File.Exists(targetPath))
                {
                    try
                    {
                        File.Replace(temporary, targetPath, null);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Delete(targetPath);
                        File.Move(temporary, targetPath);
                    }
                    catch (IOException)
                    {
                        File.Delete(targetPath);
                        File.Move(temporary, targetPath);
                    }
                }
                else
                {
                    File.Move(temporary, targetPath);
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }
                catch
                {
                    // A leftover temporary file is harmless and will be overwritten next time.
                }
            }
        }

        private static bool IsPluginConfiguration(string relative)
        {
            return string.Equals(relative, PluginConfigurationPath, PathSafety.PathComparison);
        }

        private static DateTime? ParseManifestTime(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            DateTime parsed;
            if (DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal
                | System.Globalization.DateTimeStyles.AssumeUniversal, out parsed))
            {
                return parsed;
            }
            return null;
        }

        private static void TrySetTimestamp(string path, DateTime utc)
        {
            try
            {
                if (utc.Year >= 1980 && utc.Year <= 2107)
                {
                    File.SetLastWriteTimeUtc(path, utc);
                }
            }
            catch
            {
                // Timestamps are cosmetic; a restore must not fail because of them.
            }
        }

        private static void TryPrepare(IBackupDestination destination, Action<string> log)
        {
            try
            {
                destination.Prepare();
            }
            catch (Exception ex)
            {
                // Restoring only needs read access. A read-only folder (or a network share whose write
                // probe fails) must still be restorable; Fetch will report the real error if it cannot read.
                if (log != null)
                {
                    log("The destination could not be prepared for writing; trying to read it anyway: "
                        + ex.Message);
                }
            }
        }

        private static string CreateWorkDirectory(RestoreRequest request, Action<string> log)
        {
            var root = !string.IsNullOrWhiteSpace(request.PluginDataPath)
                ? Path.Combine(request.PluginDataPath, "restore-work")
                : Path.Combine(Path.GetTempPath(), BackupConstants.WorkDirectoryName + "-restore");

            try
            {
                Directory.CreateDirectory(root);
                CleanupStale(root, log);
                var path = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss",
                    System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(path);
                return path;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Could not create a temporary folder for the restore in " + root + ": " + ex.Message, ex);
            }
        }

        private static void CleanupStale(string root, Action<string> log)
        {
            try
            {
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
                        if (log != null)
                        {
                            log("Removed an old temporary restore folder: " + info.Name);
                        }
                    }
                    catch
                    {
                        // Best effort.
                    }
                }
            }
            catch
            {
                // Best effort.
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
                if (log != null)
                {
                    log("Could not remove the temporary restore folder " + path + ": " + ex.Message);
                }
            }
        }

        private sealed class StagedFile
        {
            public string RelativePath { get; set; }
            public string StagedPath { get; set; }
            public DateTime LastWriteUtc { get; set; }
        }
    }
}
