using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using EmbyBackup.Core;

namespace EmbyBackup.Tests
{
    internal static class Program
    {
        private static int _failures;
        private static int _checks;

        private static void Main()
        {
            var root = Path.Combine(Path.GetTempPath(), "embybackup-tests", Guid.NewGuid().ToString("N"));
            var source = Path.Combine(root, "programdata");
            var pluginData = Path.Combine(root, "plugindata");
            var destination = Path.Combine(root, "backups");
            var outside = Path.Combine(root, "outside-secret.txt");

            try
            {
                Directory.CreateDirectory(root);
                BuildProgramData(source);
                Directory.CreateDirectory(destination);
                File.WriteAllText(outside, "this file is outside ProgramData");

                if (!OperatingSystem.IsWindows())
                {
                    File.CreateSymbolicLink(Path.Combine(source, "config", "link-secret.txt"), outside);
                }

                using (var liveDatabase = Sqlite.Open(Path.Combine(source, "data", "library.db")))
                {
                    Sqlite.Exec(liveDatabase, "PRAGMA journal_mode=WAL;");
                    Sqlite.Exec(liveDatabase, "PRAGMA wal_autocheckpoint=0;");
                    Sqlite.Exec(liveDatabase, "CREATE TABLE items (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL);");
                    Sqlite.Exec(liveDatabase, "INSERT INTO items (Id, Name) VALUES (1, 'Live WAL row');");

                    Check(File.Exists(Path.Combine(source, "data", "library.db-wal")),
                        "the test database has an uncheckpointed WAL file");

                    var first = RunBackup(source, pluginData, destination, 2, CancellationToken.None);
                    Check(first.Success, "first backup succeeds: " + first.Error);
                    Check(first.Verified, "first backup is verified");
                    Check(first.DatabaseCount == 1, "one database was archived");
                    Check(first.FileCount > 0, "regular files were archived");
                    Check(!string.IsNullOrEmpty(first.Sha256), "archive SHA-256 is reported");

                    var archives = RealArchives(destination);
                    Check(archives.Count == 1, "one archive exists after the first run");
                    var archive = archives[0];

                    var verifier = new BackupVerifier().Verify(archive, true);
                    Check(verifier.Valid, "archive and .sha256 verify: " + verifier.Error);
                    Check(File.Exists(archive + ".sha256"), ".sha256 sidecar exists");
                    Check(File.ReadAllText(archive + ".sha256").StartsWith(first.Sha256, StringComparison.OrdinalIgnoreCase),
                        "sidecar contains the archive hash");

                    var manifest = ReadManifest(archive);
                    Check(manifest != null, "manifest can be read");
                    Check(manifest.Format == BackupConstants.FormatName, "manifest has the expected format name");
                    Check(manifest.ExcludedPaths.Contains("metadata"), "manifest records the metadata exclusion");
                    Check(manifest.Databases.Any(d => d.Path == "data/library.db"), "library.db is in the manifest");
                    Check(manifest.Databases.All(d => string.Equals(d.QuickCheck, "ok", StringComparison.OrdinalIgnoreCase)),
                        "every database passed quick_check");
                    Check(manifest.Files.Any(f => f.Path == "config/system.xml"), "config/system.xml is present");
                    Check(manifest.Files.Any(f => f.Path == "data/playlists/favs.xml"), "playlists are present");
                    Check(!manifest.Files.Any(f => f.Path.StartsWith("metadata/", StringComparison.Ordinal)),
                        "metadata is completely excluded");
                    Check(!manifest.Files.Any(f => f.Path.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase)),
                        "NFO files are excluded");
                    Check(!manifest.Files.Any(f => f.Path.StartsWith("cache/", StringComparison.Ordinal)),
                        "cache is excluded");
                    Check(!manifest.Files.Any(f => f.Path.Contains("recordings")),
                        "Live TV recordings are excluded");
                    Check(!manifest.Files.Any(f => f.Path.Contains("link-secret")),
                        "symbolic links are excluded");
                    Check(!manifest.Files.Any(f => f.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)),
                        "plugin binaries are excluded");
                    Check(manifest.Files.Any(f => f.Path == "plugins/configurations/EmbyBackup.xml"),
                        "plugin settings are archived");
                    Check(manifest.ExcludedFilePatterns.Contains("*.dll"),
                        "manifest records the binary exclusion patterns");
                    Check(manifest.ExcludedPaths.Distinct().Count() == manifest.ExcludedPaths.Count,
                        "manifest lists each excluded path once");
                    Check(manifest.TotalBytes > 0, "manifest reports the uncompressed size");

                    var extracted = Path.Combine(root, "extracted.db");
                    Extract(archive, "data/library.db", extracted);
                    using (var check = Sqlite.Open(extracted, readOnly: true))
                    {
                        Check(Sqlite.Scalar(check, "PRAGMA quick_check") == "ok", "extracted snapshot passes quick_check");
                        Check(Sqlite.Scalar(check, "SELECT Name FROM items WHERE Id=1") == "Live WAL row",
                            "snapshot contains the row that was only in the live WAL");
                    }

                    Thread.Sleep(1100);
                    var second = RunBackup(source, pluginData, destination, 2, CancellationToken.None);
                    Check(second.Success, "second backup succeeds: " + second.Error);

                    File.WriteAllText(Path.Combine(destination, "notes.zip"), "a foreign zip file");
                    File.WriteAllText(Path.Combine(destination, "emby-backup-notours.zip"), "not created by the plugin");

                    Thread.Sleep(1100);
                    var third = RunBackup(source, pluginData, destination, 2, CancellationToken.None);
                    Check(third.Success, "third backup succeeds: " + third.Error);
                    Check(RealArchives(destination).Count == 2, "retention keeps two plugin backups");
                    Check(File.Exists(Path.Combine(destination, "notes.zip")), "retention never deletes foreign files");
                    Check(File.Exists(Path.Combine(destination, "emby-backup-notours.zip")),
                        "retention never deletes archives without a valid manifest");

                    var beforeCancel = RealArchives(destination).Count;
                    using (var cancelled = new CancellationTokenSource())
                    {
                        cancelled.Cancel();
                        var result = RunBackup(source, pluginData, destination, 2, cancelled.Token);
                        Check(!result.Success, "a cancelled backup does not report success");
                        Check(result.Error != null && result.Error.Contains("cancelled", StringComparison.OrdinalIgnoreCase),
                            "cancellation is reported clearly");
                        Check(RealArchives(destination).Count == beforeCancel, "cancellation leaves no archive behind");
                    }

                    var inside = Path.Combine(source, "data", "backups");
                    var insideResult = RunBackup(source, pluginData, inside, 2, CancellationToken.None);
                    Check(!insideResult.Success, "a destination inside ProgramData is refused");
                    Check(insideResult.Error != null && insideResult.Error.Contains("inside Emby", StringComparison.OrdinalIgnoreCase),
                        "the refusal explains the destination rule");
                    Check(!Directory.Exists(inside), "the refused destination is not created");

                    var readOnly = Path.Combine(root, "readonly-destination");
                    Directory.CreateDirectory(readOnly);
                    if (!OperatingSystem.IsWindows() && Environment.UserName != "root")
                    {
                        File.SetUnixFileMode(readOnly, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                        var denied = RunBackup(source, pluginData, readOnly, 2, CancellationToken.None);
                        File.SetUnixFileMode(readOnly,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                        Check(!denied.Success, "a read-only destination fails");
                        Check(denied.Error != null
                            && denied.Error.Contains("write access", StringComparison.OrdinalIgnoreCase),
                            "the read-only failure explains how to fix permissions");
                        Check(!Directory.EnumerateFiles(readOnly).Any(), "the write probe leaves nothing behind");
                        Check(RealArchives(readOnly).Count == 0, "a failed destination keeps no archive");
                    }

                    CheckThrows(() => PathSafety.ToArchivePath("../evil"),
                        "archive paths with .. are rejected");
                    CheckThrows(() => PathSafety.ToArchivePath("a/../../b"),
                        "nested archive path traversal is rejected");
                    CheckThrows(() => PathSafety.ToArchivePath("C:/Windows/file"),
                        "archive paths with drive letters are rejected");
                    Check(!BackupCatalog.IsManagedFileName("../emby-backup-x.zip"),
                        "path traversal file names are rejected by the catalog");
                    Check(BackupCatalog.IsManagedFileName("emby-backup-20260101-000000Z.zip"),
                        "normal managed file names are accepted");

                    DateTime stamp;
                    Check(BackupFormat.TryParseArchiveStamp("emby-backup-20260101-010203Z.zip", out stamp)
                        && stamp == new DateTime(2026, 1, 1, 1, 2, 3, DateTimeKind.Utc),
                        "archive timestamps are parsed from the file name");
                    Check(!BackupFormat.TryParseArchiveStamp("notes.zip", out stamp),
                        "foreign file names have no archive timestamp");

                    const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
                    const string archiveName = "emby-backup-20260101-010203Z.zip";
                    string parsedHash;
                    Check(BackupFormat.IsValidSidecar(
                            BackupFormat.SidecarText(hash, archiveName), archiveName, out parsedHash)
                        && parsedHash == hash,
                        "the .sha256 sidecar is recognized as an ownership marker");
                    Check(!BackupFormat.IsValidSidecar(
                            BackupFormat.SidecarText(hash, "other.zip"), archiveName, out parsedHash),
                        "a sidecar for another file is rejected");

                    var unixListing = FtpBackupDestination.ParseListing(
                        "-rw-r--r--    1 emby     emby        23815 Oct 28 13:52 emby-backup-20260101-010203Z.zip");
                    Check(unixListing != null && unixListing.Name == "emby-backup-20260101-010203Z.zip"
                        && unixListing.Size == 23815,
                        "Unix FTP listings are parsed with their size");
                    Check(FtpBackupDestination.ParseListing(
                            "drwxr-xr-x    2 emby     emby         4096 Oct 28 13:52 emby") == null,
                        "FTP directories are ignored");
                    var iisListing = FtpBackupDestination.ParseListing(
                        "10-28-26  01:52PM               23815 emby-backup-20260101-010203Z.zip");
                    Check(iisListing != null && iisListing.Name == "emby-backup-20260101-010203Z.zip"
                        && iisListing.Size == 23815,
                        "IIS FTP listings are parsed with their size");
                    Check(FtpBackupDestination.ParseListing(
                            "10-28-26  01:52PM       <DIR>          emby") == null,
                        "IIS FTP directories are ignored");

                    var remote = new MemoryDestination();
                    remote.Add("emby-backup-20260101-000000Z.zip", true);                    remote.Add("emby-backup-20260102-000000Z.zip", true);
                    remote.Add("emby-backup-20260103-000000Z.zip", true);
                    remote.Add("emby-backup-20260104-000000Z.zip", false);
                    remote.Add("notes.zip", false);
                    var remoteRetention = new RetentionService().Apply(remote, 2, _ => { });
                    Check(remoteRetention.Kept == 2, "remote retention keeps the requested number of backups");
                    Check(remoteRetention.Deleted.Count == 1
                        && remoteRetention.Deleted[0] == "emby-backup-20260101-000000Z.zip",
                        "remote retention deletes the oldest owned archive");
                    Check(remote.Has("emby-backup-20260104-000000Z.zip"),
                        "remote retention keeps archives it cannot prove are its own");
                    Check(remote.Has("notes.zip"), "remote retention never deletes foreign files");

                    CheckThrows(() => BackupDestinations.Create(null, source, null),
                        "a missing destination is refused");
                    CheckThrows(() => BackupDestinations.Create(
                            new BackupDestinationSettings { Type = BackupDestinationKinds.Local, Path = "" }, source, null),
                        "an empty local destination is refused");
                    CheckThrows(() =>
                        {
                            using (var ftp = BackupDestinations.Create(
                                new BackupDestinationSettings { Type = BackupDestinationKinds.Ftp, FtpHost = "" },
                                source,
                                null))
                            {
                                ftp.Prepare();
                            }
                        },
                        "FTP without a server address is refused");

                    var mountedPath = Path.Combine(root, "mounted-share");
                    Directory.CreateDirectory(mountedPath);
                    var networkLogs = new List<string>();
                    using (var mounted = BackupDestinations.Create(
                        new BackupDestinationSettings
                        {
                            Type = BackupDestinationKinds.Network,
                            Path = mountedPath,
                            NetworkUsername = "user",
                            NetworkPassword = "secret",
                        },
                        source,
                        networkLogs.Add))
                    {
                        mounted.Prepare();
                        Check(mounted.Kind == BackupDestinationKinds.Network,
                            "a network destination uses the mounted folder");
                        if (!OperatingSystem.IsWindows())
                        {
                            Check(networkLogs.Any(l => l.IndexOf("ignored", StringComparison.OrdinalIgnoreCase) >= 0),
                                "network credentials are ignored on Unix instead of failing");
                        }
                    }

                    var corrupt = Path.Combine(root, "corrupt.zip");
                    File.Copy(RealArchives(destination)[0], corrupt);
                    using (var stream = new FileStream(corrupt, FileMode.Open, FileAccess.ReadWrite))
                    {
                        stream.SetLength(stream.Length - 20);
                    }
                    var corruptVerification = new BackupVerifier().Verify(corrupt, false);
                    Check(!corruptVerification.Valid, "a truncated archive does not verify");

                    var history = BackupHistoryStore.Read(pluginData);
                    Check(history.Count >= 4, "history records every run, including the cancelled one");
                    Check(history.Any(r => r.Status == "success" && r.Verified), "history records verified successes");
                    Check(history.Any(r => r.Status == "cancelled"), "history records the cancelled run");
                    Check(BackupHistoryStore.MostRecent(pluginData) != null, "history returns the most recent record");
                }
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, true);
                    }
                }
                catch
                {
                    // Best effort.
                }
            }

            ScheduleRuleChecks();
            ZipChecks();
            EncryptedBackupChecks();
            RestoreChecks();

            Console.WriteLine();
            Console.WriteLine(_failures == 0
                ? "All " + _checks + " checks passed."
                : _failures + " of " + _checks + " checks FAILED.");
            Environment.Exit(_failures == 0 ? 0 : 1);
        }

        private static void RestoreChecks()
        {
            var root = Path.Combine(Path.GetTempPath(), "embybackup-restore-tests", Guid.NewGuid().ToString("N"));
            var source = Path.Combine(root, "programdata");
            var pluginData = Path.Combine(root, "plugindata");
            var destination = Path.Combine(root, "backups");
            const string password = "restore-pass";

            try
            {
                Directory.CreateDirectory(root);
                BuildProgramData(source);
                Directory.CreateDirectory(destination);
                var dbPath = Path.Combine(source, "data", "library.db");
                using (var db = Sqlite.Open(dbPath))
                {
                    Sqlite.Exec(db, "CREATE TABLE items (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL);");
                    Sqlite.Exec(db, "INSERT INTO items (Id, Name) VALUES (1, 'Original row');");
                }

                var backup = RunBackup(source, pluginData, destination, 2, CancellationToken.None, password);
                Check(backup.Success, "the backup used by the restore test succeeds: " + backup.Error);
                var archive = BackupCatalog.List(destination).First(a => a.Encrypted).FullPath;

                var restoreDestination = new BackupDestinationSettings
                {
                    Type = BackupDestinationKinds.Local,
                    Path = destination,
                };
                var check = new RestoreService(10, _ => { }).Check(new RestoreRequest
                {
                    ProgramDataPath = source,
                    PluginDataPath = pluginData,
                    Destination = restoreDestination,
                    FileName = Path.GetFileName(archive),
                    Password = password,
                });
                Check(check.Success && check.Valid, "the restore check accepts the encrypted archive: " + check.Error);
                Check(check.Encrypted, "the restore check reports the archive as encrypted");
                Check(check.DatabaseCount == 1, "the restore check reports the database");

                var noPassword = new RestoreService(10, _ => { }).Check(new RestoreRequest
                {
                    ProgramDataPath = source,
                    PluginDataPath = pluginData,
                    Destination = restoreDestination,
                    FileName = Path.GetFileName(archive),
                });
                Check(noPassword.PasswordRequired, "the restore check asks for the password");

                var wrong = new RestoreService(10, _ => { }).Check(new RestoreRequest
                {
                    ProgramDataPath = source,
                    PluginDataPath = pluginData,
                    Destination = restoreDestination,
                    FileName = Path.GetFileName(archive),
                    Password = "nope",
                });
                Check(wrong.WrongPassword, "the restore check rejects a wrong password");

                // Change everything after the backup. A failed restore must leave it untouched.
                File.WriteAllText(Path.Combine(source, "config", "system.xml"),
                    "<ServerConfiguration><Changed>true</Changed></ServerConfiguration>");
                File.WriteAllText(Path.Combine(source, "plugins", "configurations", "EmbyBackup.xml"),
                    "<PluginConfiguration><Current>true</Current></PluginConfiguration>");
                using (var db = Sqlite.Open(dbPath))
                {
                    Sqlite.Exec(db, "INSERT INTO items (Id, Name) VALUES (2, 'After backup');");
                }
                File.WriteAllText(Path.Combine(source, "config", "created-after-backup.txt"), "keep me");

                var failed = new RestoreService(10, _ => { }).Run(new RestoreRequest
                {
                    ProgramDataPath = source,
                    PluginDataPath = pluginData,
                    Destination = restoreDestination,
                    FileName = Path.GetFileName(archive),
                    Password = "nope",
                }, CancellationToken.None);
                Check(!failed.Success, "a restore with the wrong password fails");
                Check(!failed.Applied, "a restore that fails while checking does not touch the data");
                Check(File.ReadAllText(Path.Combine(source, "config", "system.xml")).Contains("Changed"),
                    "the failed restore leaves config/system.xml untouched");
                using (var db = Sqlite.Open(dbPath, readOnly: true))
                {
                    Check(Sqlite.Scalar(db, "SELECT COUNT(*) FROM items") == "2",
                        "the failed restore leaves the database untouched");
                }

                var restored = new RestoreService(10, _ => { }).Run(new RestoreRequest
                {
                    ProgramDataPath = source,
                    PluginDataPath = pluginData,
                    Destination = restoreDestination,
                    FileName = Path.GetFileName(archive),
                    Password = password,
                }, CancellationToken.None);
                Check(restored.Success, "the restore succeeds: " + restored.Error);
                Check(restored.Applied, "the restore reports that it applied data");
                Check(restored.RestartRequired, "the restore requires an Emby restart");
                Check(restored.RestoredDatabases == 1, "the restore applies the database");

                Check(File.ReadAllText(Path.Combine(source, "config", "system.xml")).Contains("ServerConfiguration")
                    && !File.ReadAllText(Path.Combine(source, "config", "system.xml")).Contains("Changed"),
                    "the restore reverts config/system.xml to the backup");
                Check(File.ReadAllText(Path.Combine(source, "plugins", "configurations", "EmbyBackup.xml"))
                        .Contains("Current"),
                    "the restore keeps the current plugin settings");
                Check(File.Exists(Path.Combine(source, "config", "created-after-backup.txt")),
                    "the restore does not delete files that were created after the backup");
                using (var db = Sqlite.Open(dbPath, readOnly: true))
                {
                    Check(Sqlite.Scalar(db, "SELECT COUNT(*) FROM items") == "1",
                        "the restored database has the backup contents");
                    Check(Sqlite.Scalar(db, "SELECT Name FROM items WHERE Id=1") == "Original row",
                        "the restored database contains the original row");
                }

                // A malicious manifest with a path traversal entry must be rejected before applying anything.
                var malicious = Path.Combine(destination, "emby-backup-20260101-000000Z.zip");
                var escapeTarget = Path.Combine(root, "escape.txt");
                var evilBytes = Encoding.UTF8.GetBytes("escaped");
                var evilSha = BackupFormat.Sha256(
                    System.Security.Cryptography.SHA256.Create().ComputeHash(evilBytes));
                var evilManifest = new BackupManifest
                {
                    CreatedUtc = BackupFormat.Utc(DateTime.UtcNow),
                    Files = new List<BackupFileEntry>
                    {
                        new BackupFileEntry
                        {
                            Path = "../escape.txt",
                            Bytes = evilBytes.Length,
                            Sha256 = evilSha,
                            LastWriteUtc = BackupFormat.Utc(DateTime.UtcNow),
                        },
                    },
                };
                using (var output = new FileStream(malicious, FileMode.Create, FileAccess.Write))
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
                {
                    var manifestEntry = zip.CreateEntry(BackupConstants.ManifestEntryName);
                    using (var stream = manifestEntry.Open())
                    {
                        JsonCodec.WriteToStream(stream, evilManifest);
                    }
                    var evil = zip.CreateEntry("../escape.txt");
                    using (var stream = evil.Open())
                    {
                        stream.Write(evilBytes, 0, evilBytes.Length);
                    }
                }

                var evilCheck = new RestoreService(10, _ => { }).Check(new RestoreRequest
                {
                    ProgramDataPath = source,
                    PluginDataPath = pluginData,
                    Destination = restoreDestination,
                    FileName = Path.GetFileName(malicious),
                });
                Check(!evilCheck.Success, "a manifest with path traversal is rejected by the check");

                var evilRun = new RestoreService(10, _ => { }).Run(new RestoreRequest
                {
                    ProgramDataPath = source,
                    PluginDataPath = pluginData,
                    Destination = restoreDestination,
                    FileName = Path.GetFileName(malicious),
                }, CancellationToken.None);
                Check(!evilRun.Success, "a manifest with path traversal cannot be restored");
                Check(!File.Exists(escapeTarget), "the path traversal entry never reaches the filesystem");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, true);
                    }
                }
                catch
                {
                    // Best effort.
                }
            }
        }

        private static void EncryptedBackupChecks()
        {
            var root = Path.Combine(Path.GetTempPath(), "embybackup-encrypted-tests", Guid.NewGuid().ToString("N"));
            var source = Path.Combine(root, "programdata");
            var pluginData = Path.Combine(root, "plugindata");
            var destination = Path.Combine(root, "backups");
            const string password = "correct horse battery staple";

            try
            {
                Directory.CreateDirectory(root);
                BuildProgramData(source);
                Directory.CreateDirectory(destination);
                using (var db = Sqlite.Open(Path.Combine(source, "data", "library.db")))
                {
                    Sqlite.Exec(db, "CREATE TABLE items (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL);");
                    Sqlite.Exec(db, "INSERT INTO items (Id, Name) VALUES (1, 'Encrypted backup row');");
                }

                var result = RunBackup(source, pluginData, destination, 2, CancellationToken.None, password);
                Check(result.Success, "an encrypted backup succeeds: " + result.Error);
                Check(result.Encrypted, "the backup result reports encryption");
                Check(result.Verified, "the encrypted backup verifies during creation");

                var encryptedArchives = BackupCatalog.List(destination).Where(a => a.Encrypted).ToList();
                Check(encryptedArchives.Count == 1, "the destination lists one encrypted archive");
                Check(!encryptedArchives[0].HasManifest,
                    "the encrypted archive does not expose its manifest without the password");
                Check(encryptedArchives[0].IsEmbyBackup,
                    "the .sha256 sidecar identifies the encrypted archive as ours");

                bool encrypted;
                string inspectError;
                Check(ZipReader.TryInspect(encryptedArchives[0].FullPath, out encrypted, out inspectError) && encrypted,
                    "the archive is an AES ZIP: " + inspectError);

                var noPassword = new BackupVerifier().Verify(encryptedArchives[0].FullPath, true);
                Check(!noPassword.Valid, "verifying an encrypted backup without a password fails");
                Check(noPassword.PasswordRequired, "the verifier asks for the password");

                var wrongPassword = new BackupVerifier().Verify(encryptedArchives[0].FullPath, true, "wrong");
                Check(!wrongPassword.Valid, "verifying an encrypted backup with a wrong password fails");
                Check(wrongPassword.WrongPassword, "the verifier reports the wrong password clearly");

                var verified = new BackupVerifier().Verify(encryptedArchives[0].FullPath, true, password);
                Check(verified.Valid, "the encrypted backup verifies with its password: " + verified.Error);
                Check(verified.Encrypted, "verification reports the archive as encrypted");
                Check(verified.CheckedDatabases == 1, "the encrypted backup contains the database");

                var manifest = BackupCatalog.ReadManifest(encryptedArchives[0].FullPath, out var manifestError, password);
                Check(manifest != null, "the encrypted manifest can be read with the password: " + manifestError);
                Check(manifest != null && manifest.Databases.Any(d => d.Path == "data/library.db"),
                    "the decrypted manifest lists the database");
                Check(BackupCatalog.ReadManifest(encryptedArchives[0].FullPath, out _) == null,
                    "the encrypted manifest cannot be read without the password");

                Thread.Sleep(1100);
                var second = RunBackup(source, pluginData, destination, 1, CancellationToken.None, password);
                Check(second.Success, "a second encrypted backup succeeds: " + second.Error);
                var remaining = BackupCatalog.List(destination).Where(a => a.Encrypted).ToList();
                Check(remaining.Count == 1, "retention keeps one encrypted backup");
                Check(remaining[0].FileName == Path.GetFileName(second.ArchivePath),
                    "retention keeps the newest encrypted backup");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, true);
                    }
                }
                catch
                {
                    // Best effort.
                }
            }
        }

        private static void ZipChecks()
        {
            var root = Path.Combine(Path.GetTempPath(), "embybackup-zip-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var payload = new byte[300 * 1024 + 7];
                new Random(42).NextBytes(payload);
                var text = Encoding.UTF8.GetBytes("Hola, Emby Backup. Acentos: áéíóú ñ.");
                var stamp = new DateTime(2026, 10, 28, 12, 34, 56, DateTimeKind.Utc);

                var archivePath = Path.Combine(root, "aes.zip");
                using (var writer = new AesZipWriter(archivePath, "s3cret", root))
                {
                    using (var input = new MemoryStream(payload, false))
                    {
                        var result = writer.AddEntry("data/big.bin", stamp, input, CompressionLevel.Optimal,
                            CancellationToken.None);
                        Check(result.PlainBytes == payload.Length, "AES writer reports the plaintext size");
                        Check(result.Sha256 == BackupFormat.Sha256(
                                System.Security.Cryptography.SHA256.Create().ComputeHash(payload)),
                            "AES writer reports the plaintext SHA-256");
                    }

                    writer.AddBytes("config/system.xml", stamp, text, CompressionLevel.Optimal,
                        CancellationToken.None);
                    writer.AddBytes("data/empty.txt", stamp, new byte[0], CompressionLevel.NoCompression,
                        CancellationToken.None);
                    writer.Complete();
                }

                Check(File.Exists(archivePath), "AES writer creates the archive");
                bool encrypted;
                string inspectError;
                Check(ZipReader.TryInspect(archivePath, out encrypted, out inspectError) && encrypted,
                    "the reader recognizes the AES archive: " + inspectError);

                using (var reader = new ZipReader(archivePath))
                {
                    Check(reader.IsEncrypted, "the reader reports the archive as encrypted");
                    Check(reader.Entries.Count == 3, "the reader lists every AES entry");
                    Check(reader.Find("data/big.bin") != null, "the reader finds entries by name");

                    var big = reader.ReadAllBytes(reader.Find("data/big.bin"), "s3cret", 1024 * 1024);
                    Check(big.SequenceEqual(payload), "AES round-trip returns the exact plaintext");
                    var xml = reader.ReadAllBytes(reader.Find("config/system.xml"), "s3cret", 1024 * 1024);
                    Check(xml.SequenceEqual(text), "AES round-trip keeps UTF-8 names and content");
                    var empty = reader.ReadAllBytes(reader.Find("data/empty.txt"), "s3cret", 1024 * 1024);
                    Check(empty.Length == 0, "an empty stored entry round-trips");

                    CheckThrows(() => reader.ReadAllBytes(reader.Find("data/big.bin"), null, 1024 * 1024),
                        "reading an encrypted entry without a password is refused");
                    CheckThrows(() => reader.ReadAllBytes(reader.Find("data/big.bin"), "wrong", 1024 * 1024),
                        "reading an encrypted entry with a wrong password is refused");
                }

                // Corrupt one ciphertext byte: the HMAC must reject the entry even if deflate still
                // manages to produce some output.
                var tamperedPath = Path.Combine(root, "tampered.zip");
                File.Copy(archivePath, tamperedPath);
                long tamperOffset;
                using (var reader = new ZipReader(tamperedPath))
                {
                    var entry = reader.Find("data/big.bin");
                    using (var file = new FileStream(tamperedPath, FileMode.Open, FileAccess.Read))
                    {
                        file.Seek(entry.LocalHeaderOffset, SeekOrigin.Begin);
                        var header = new byte[30];
                        file.Read(header, 0, header.Length);
                        var nameLength = ZipEncryption.ReadUInt16(header, 26);
                        var extraLength = ZipEncryption.ReadUInt16(header, 28);
                        tamperOffset = entry.LocalHeaderOffset + 30 + nameLength + extraLength
                            + ZipEncryption.SaltLength + ZipEncryption.PasswordVerifierLength + 100;
                    }
                }
                using (var file = new FileStream(tamperedPath, FileMode.Open, FileAccess.ReadWrite))
                {
                    file.Seek(tamperOffset, SeekOrigin.Begin);
                    var value = file.ReadByte();
                    file.Seek(tamperOffset, SeekOrigin.Begin);
                    file.WriteByte((byte)(value ^ 0x5A));
                }
                CheckThrows(() =>
                    {
                        using (var reader = new ZipReader(tamperedPath))
                        {
                            reader.ReadAllBytes(reader.Find("data/big.bin"), "s3cret", 1024 * 1024);
                        }
                    },
                    "a modified AES ciphertext fails authentication");

                var sevenZip = FindSevenZip();
                if (sevenZip == null)
                {
                    Console.WriteLine("SKIP  7-Zip interoperability checks (7z not installed)");
                    return;
                }

                var test = RunProcess(sevenZip, "t -p" + "s3cret \"" + archivePath + "\"", root);
                Check(test.ExitCode == 0, "7-Zip validates the archive we created: " + test.Output.Trim());

                var plainName = "interop.txt";
                File.WriteAllBytes(Path.Combine(root, plainName), payload);
                var sevenArchive = Path.Combine(root, "seven.zip");
                var create = RunProcess(
                    sevenZip,
                    "a -tzip -mem=AES256 -p" + "sevenpass \"" + sevenArchive + "\" \"" + plainName + "\"",
                    root);
                Check(create.ExitCode == 0, "7-Zip creates an AES archive: " + create.Output.Trim());

                using (var reader = new ZipReader(sevenArchive))
                {
                    Check(reader.IsEncrypted, "the reader recognizes an archive created by 7-Zip");
                    var data = reader.ReadAllBytes(reader.Find(plainName), "sevenpass", 2 * 1024 * 1024);
                    Check(data.SequenceEqual(payload), "the reader decrypts an archive created by 7-Zip");
                    CheckThrows(() => reader.ReadAllBytes(reader.Find(plainName), "nope", 2 * 1024 * 1024),
                        "a 7-Zip archive with a wrong password is refused");
                }

                // A normal, unencrypted ZIP produced by .NET must also be readable by the same reader.
                var plainZip = Path.Combine(root, "plain.zip");
                using (var output = new FileStream(plainZip, FileMode.Create, FileAccess.Write))
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
                {
                    var entry = archive.CreateEntry("config/system.xml", CompressionLevel.Optimal);
                    using (var stream = entry.Open())
                    {
                        stream.Write(text, 0, text.Length);
                    }
                }
                using (var reader = new ZipReader(plainZip))
                {
                    Check(!reader.IsEncrypted, "a normal ZIP is not reported as encrypted");
                    var data = reader.ReadAllBytes(reader.Find("config/system.xml"), null, 1024 * 1024);
                    Check(data.SequenceEqual(text), "the reader also reads unencrypted ZIPs");
                }
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, true);
                    }
                }
                catch
                {
                    // Best effort.
                }
            }
        }

        private static string FindSevenZip()
        {
            foreach (var candidate in new[] { "7z", "/usr/bin/7z", "/usr/local/bin/7z" })
            {
                try
                {
                    var result = RunProcess(candidate, "", Path.GetTempPath());
                    if (result.ExitCode == 0 || result.Output.Length > 0)
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // Try the next candidate.
                }
            }
            return null;
        }

        private static ProcessResult RunProcess(string fileName, string arguments, string workingDirectory)
        {
            var info = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (var process = Process.Start(info))
            {
                var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                process.WaitForExit();
                return new ProcessResult { ExitCode = process.ExitCode, Output = output };
            }
        }

        private sealed class ProcessResult
        {
            public int ExitCode { get; set; }
            public string Output { get; set; }
        }

        private static void ScheduleRuleChecks()
        {
            Check(BackupSchedulePeriods.Normalize(null) == BackupSchedulePeriods.Daily,
                "an empty schedule period means daily");
            Check(BackupSchedulePeriods.Normalize("weekly") == BackupSchedulePeriods.Weekly,
                "a schedule period is case-insensitive");
            Check(BackupSchedulePeriods.Normalize("something") == BackupSchedulePeriods.Daily,
                "an unknown schedule period falls back to daily");

            Check(BackupScheduleRules.NormalizeTime("3:15") == "03:15", "a short time is padded to HH:mm");
            Check(BackupScheduleRules.NormalizeTime("23:59") == "23:59", "a valid time is kept");
            Check(BackupScheduleRules.NormalizeTime("25:00") == "03:00", "an invalid time falls back to 03:00");
            Check(BackupScheduleRules.NormalizeTime(null) == "03:00", "a missing time falls back to 03:00");

            Check(BackupScheduleRules.NormalizeDayOfWeek("friday") == "Friday",
                "a weekday is normalized case-insensitively");
            Check(BackupScheduleRules.NormalizeDayOfWeek("nope") == "Monday",
                "an unknown weekday falls back to Monday");

            var today = new DateTime(2026, 10, 28, 3, 0, 0, DateTimeKind.Local);

            Check(BackupScheduleRules.IsDue("Daily", today, null), "daily is due when there is no history");
            Check(BackupScheduleRules.IsDue("Daily", today, today.AddDays(-1)), "daily is due the next day");
            Check(!BackupScheduleRules.IsDue("Daily", today, today.AddHours(-1)),
                "daily is not due twice on the same day");

            Check(BackupScheduleRules.IsDue("AlternateDays", today, today.AddDays(-2)),
                "alternate days is due after two days");
            Check(!BackupScheduleRules.IsDue("AlternateDays", today, today.AddDays(-1)),
                "alternate days is not due after one day");
            Check(BackupScheduleRules.IsDue("AlternateDays", today, today.AddDays(-5)),
                "alternate days catches up after a missed run");

            Check(!BackupScheduleRules.IsDue("Monthly", today, new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Local)),
                "monthly is not due twice in the same month");
            Check(BackupScheduleRules.IsDue("Monthly", today, new DateTime(2026, 9, 1, 3, 0, 0, DateTimeKind.Local)),
                "monthly is due in a new month");
            Check(BackupScheduleRules.IsDue("Monthly", today, new DateTime(2025, 10, 1, 3, 0, 0, DateTimeKind.Local)),
                "monthly is due in a new year");

            Check(BackupScheduleRules.IsDue("Weekly", today, today.AddDays(-7)),
                "weekly is due a week later");
            Check(!BackupScheduleRules.IsDue("Weekly", today, today.AddHours(-1)),
                "weekly is not due twice on the same day");

            Check(BackupScheduleRules.Describe("Daily", "04:30", "Monday") == "every day at 04:30",
                "the daily description includes the time");
            Check(BackupScheduleRules.Describe("AlternateDays", "04:30", "Monday") == "every other day at 04:30",
                "the alternate-day description is explicit");
            Check(BackupScheduleRules.Describe("Weekly", "04:30", "Friday") == "every Friday at 04:30",
                "the weekly description includes the weekday");
            Check(BackupScheduleRules.Describe("Monthly", "04:30", "Friday")
                    == "on the first day of each month at 04:30",
                "the monthly description explains the first day of the month");
        }

        private static BackupRunResult RunBackup(
            string source,
            string pluginData,
            string destination,
            int retention,
            CancellationToken cancellationToken,
            string password = null)
        {
            var logs = new List<string>();
            var request = new BackupRequest
            {
                ProgramDataPath = source,
                PluginDataPath = pluginData,
                Destination = new BackupDestinationSettings
                {
                    Type = BackupDestinationKinds.Local,
                    Path = destination,
                },
                EmbyVersion = "4.9.1.90",
                PluginVersion = "1.0.0",
                Compression = "Optimal",
                RetentionCount = retention,
                BusyTimeoutSeconds = 10,
                VerifyAfterCreate = true,
                Password = password,
                Log = logs.Add,
                Progress = (percent, message) => { },
            };

            var result = new BackupService().Run(request, cancellationToken);
            if (!result.Success)
            {
                Console.WriteLine("    backup log:");
                foreach (var line in logs)
                {
                    Console.WriteLine("      " + line);
                }
            }
            return result;
        }

        private static void BuildProgramData(string source)
        {
            Directory.CreateDirectory(Path.Combine(source, "config", "users", "abc"));
            Directory.CreateDirectory(Path.Combine(source, "data", "playlists"));
            Directory.CreateDirectory(Path.Combine(source, "data", "livetv", "recordings"));
            Directory.CreateDirectory(Path.Combine(source, "data", "subtitles"));
            Directory.CreateDirectory(Path.Combine(source, "metadata", "movies", "movie"));
            Directory.CreateDirectory(Path.Combine(source, "cache"));
            Directory.CreateDirectory(Path.Combine(source, "logs"));
            Directory.CreateDirectory(Path.Combine(source, "plugins", "configurations"));

            File.WriteAllText(Path.Combine(source, "plugins", "configurations", "EmbyBackup.xml"), "<PluginConfiguration />");
            File.WriteAllBytes(Path.Combine(source, "plugins", "SomePlugin.dll"), new byte[] { 0x4D, 0x5A, 1, 2, 3 });

            File.WriteAllText(Path.Combine(source, "config", "system.xml"), "<ServerConfiguration />");
            File.WriteAllText(Path.Combine(source, "config", "users", "abc", "policy.xml"), "<UserPolicy />");
            File.WriteAllText(Path.Combine(source, "data", "playlists", "favs.xml"), "<Playlist />");
            File.WriteAllBytes(Path.Combine(source, "metadata", "movies", "movie", "poster.jpg"), new byte[] { 1, 2, 3 });
            File.WriteAllText(Path.Combine(source, "metadata", "movies", "movie", "movie.nfo"), "<movie />");
            File.WriteAllText(Path.Combine(source, "cache", "image.jpg"), "cache");
            File.WriteAllText(Path.Combine(source, "logs", "embyserver.txt"), "log");
            File.WriteAllText(Path.Combine(source, "data", "livetv", "recordings", "show.ts"), "recording");
            File.WriteAllText(Path.Combine(source, "data", "subtitles", "sub.srt"), "subtitle");
        }

        private static List<string> RealArchives(string destination)
        {
            return BackupCatalog.List(destination)
                .Where(a => a.IsEmbyBackup && a.HasManifest)
                .Select(a => a.FullPath)
                .ToList();
        }

        private static BackupManifest ReadManifest(string archivePath)
        {
            var manifest = BackupCatalog.ReadManifest(archivePath, out var error);
            if (manifest == null)
            {
                Console.WriteLine("    manifest error: " + error);
            }
            return manifest;
        }

        private static void Extract(string archivePath, string entryName, string destination)
        {
            using (var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                var entry = archive.GetEntry(entryName);
                if (entry == null)
                {
                    throw new InvalidOperationException("Missing archive entry: " + entryName);
                }
                entry.ExtractToFile(destination, true);
            }
        }

        private static void Check(bool condition, string description)
        {
            _checks++;
            if (condition)
            {
                Console.WriteLine("PASS  " + description);
                return;
            }

            _failures++;
            Console.WriteLine("FAIL  " + description);
        }

        private static void CheckThrows(Action action, string description)
        {
            try
            {
                action();
                Check(false, description);
            }
            catch
            {
                Check(true, description);
            }
        }
    }

    /// <summary>Small in-memory destination used to exercise retention on FTP-like listings.</summary>
    internal sealed class MemoryDestination : IBackupDestination
    {
        private readonly Dictionary<string, bool> _files = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public void Add(string name, bool owned)
        {
            _files[name] = owned;
        }

        public bool Has(string name)
        {
            return _files.ContainsKey(name);
        }

        public string Kind
        {
            get { return BackupDestinationKinds.Ftp; }
        }

        public string Location
        {
            get { return "memory://backups"; }
        }

        public string LocalRootPath
        {
            get { return null; }
        }

        public bool CanCheckFreeSpace
        {
            get { return false; }
        }

        public void Prepare()
        {
        }

        public string CreateStagingPath(string fileName)
        {
            return Path.Combine(Path.GetTempPath(), fileName);
        }

        public BackupCommitResult Commit(string stagingPath, string fileName, string sha256, Action<string> log)
        {
            _files[fileName] = true;
            return new BackupCommitResult { Location = Location + "/" + fileName };
        }

        public IReadOnlyList<BackupDestinationFile> ListArchives()
        {
            return _files.Keys
                .Where(name => name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .Select(name =>
                {
                    DateTime stamp;
                    BackupFormat.TryParseArchiveStamp(name, out stamp);
                    return new BackupDestinationFile
                    {
                        Name = name,
                        Bytes = 1024,
                        ModifiedUtc = stamp,
                        HasSidecar = _files.ContainsKey(name + BackupConstants.SidecarSuffix),
                    };
                })
                .ToList();
        }

        public IReadOnlyList<BackupDestinationFile> ListAll()
        {
            return _files.Keys
                .Select(name => new BackupDestinationFile { Name = name, ModifiedUtc = DateTime.UtcNow })
                .ToList();
        }

        public bool IsOwned(BackupDestinationFile file)
        {
            return _files.TryGetValue(file.Name, out var owned) && owned;
        }

        public void Delete(string fileName, Action<string> log)
        {
            _files.Remove(fileName);
            _files.Remove(fileName + BackupConstants.SidecarSuffix);
        }

        public string Fetch(string fileName, string purpose)
        {
            throw new NotSupportedException();
        }

        public void Release(string localPath)
        {
        }

        public void Discard(string stagingPath)
        {
        }

        public void Dispose()
        {
        }
    }

    internal sealed class SqliteHandle : IDisposable
    {
        public IntPtr Db { get; private set; }

        internal SqliteHandle(IntPtr db)
        {
            Db = db;
        }

        public void Dispose()
        {
            Sqlite.Close(Db);
            Db = IntPtr.Zero;
        }

        public static implicit operator IntPtr(SqliteHandle handle)
        {
            return handle.Db;
        }
    }

    internal static class Sqlite
    {
        private const int Ok = 0;
        private const int Row = 100;
        private const int ReadOnly = 1;
        private const int ReadWrite = 2;
        private const int Create = 4;

        public static SqliteHandle Open(string path, bool readOnly = false)
        {
            var db = IntPtr.Zero;
            var rc = sqlite3_open_v2(path, out db, readOnly ? ReadOnly : (ReadWrite | Create), IntPtr.Zero);
            if (rc != Ok)
            {
                throw new InvalidOperationException("sqlite3_open_v2 failed for " + path + ": " + Error(db));
            }
            return new SqliteHandle(db);
        }

        public static void Exec(IntPtr db, string sql)
        {
            IntPtr error;
            if (sqlite3_exec(db, sql, IntPtr.Zero, IntPtr.Zero, out error) != Ok)
            {
                var message = error == IntPtr.Zero ? Error(db) : Marshal.PtrToStringAnsi(error);
                throw new InvalidOperationException("SQL failed: " + sql + " -> " + message);
            }
        }

        public static string Scalar(IntPtr db, string sql)
        {
            IntPtr statement;
            IntPtr tail;
            if (sqlite3_prepare_v2(db, sql, -1, out statement, out tail) != Ok)
            {
                throw new InvalidOperationException("SQL prepare failed: " + Error(db));
            }

            try
            {
                var rc = sqlite3_step(statement);
                if (rc == Row)
                {
                    var pointer = sqlite3_column_text(statement, 0);
                    return pointer == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(pointer);
                }
                if (rc == 101)
                {
                    return null;
                }
                throw new InvalidOperationException("SQL step failed (" + rc + "): " + Error(db));
            }
            finally
            {
                sqlite3_finalize(statement);
            }
        }

        public static void Close(IntPtr db)
        {
            if (db != IntPtr.Zero)
            {
                sqlite3_close_v2(db);
            }
        }

        private static string Error(IntPtr db)
        {
            var pointer = db == IntPtr.Zero ? IntPtr.Zero : sqlite3_errmsg(db);
            return pointer == IntPtr.Zero ? "unknown sqlite error" : Marshal.PtrToStringAnsi(pointer);
        }

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_open_v2")]
        private static extern int sqlite3_open_v2(
            [MarshalAs(UnmanagedType.LPStr)] string filename,
            out IntPtr db,
            int flags,
            IntPtr vfs);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_close_v2")]
        private static extern int sqlite3_close_v2(IntPtr db);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_exec")]
        private static extern int sqlite3_exec(
            IntPtr db,
            [MarshalAs(UnmanagedType.LPStr)] string sql,
            IntPtr callback,
            IntPtr callbackArgument,
            out IntPtr errorMessage);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_prepare_v2")]
        private static extern int sqlite3_prepare_v2(
            IntPtr db,
            [MarshalAs(UnmanagedType.LPStr)] string sql,
            int bytes,
            out IntPtr statement,
            out IntPtr tail);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_step")]
        private static extern int sqlite3_step(IntPtr statement);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_text")]
        private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_finalize")]
        private static extern int sqlite3_finalize(IntPtr statement);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_errmsg")]
        private static extern IntPtr sqlite3_errmsg(IntPtr db);
    }
}
