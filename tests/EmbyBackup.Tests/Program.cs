using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
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

            Console.WriteLine();
            Console.WriteLine(_failures == 0
                ? "All " + _checks + " checks passed."
                : _failures + " of " + _checks + " checks FAILED.");
            Environment.Exit(_failures == 0 ? 0 : 1);
        }

        private static BackupRunResult RunBackup(
            string source,
            string pluginData,
            string destination,
            int retention,
            CancellationToken cancellationToken)
        {
            var logs = new List<string>();
            var request = new BackupRequest
            {
                ProgramDataPath = source,
                PluginDataPath = pluginData,
                DestinationPath = destination,
                EmbyVersion = "4.9.1.90",
                PluginVersion = "1.0.0",
                Compression = "Optimal",
                RetentionCount = retention,
                BusyTimeoutSeconds = 10,
                VerifyAfterCreate = true,
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
