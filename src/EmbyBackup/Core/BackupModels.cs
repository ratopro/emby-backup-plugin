using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace EmbyBackup.Core
{
    public static class BackupConstants
    {
        public const string FormatName = "emby-server-backup";
        public const int FormatVersion = 1;
        public const string ArchivePrefix = "emby-backup-";
        public const string ArchiveSuffix = ".zip";
        public const string ManifestEntryName = "manifest.json";
        public const string PartialSuffix = ".partial";
        public const string SidecarSuffix = ".sha256";
        public const string WorkDirectoryName = ".embybackup-work";

        /// <summary>
        /// Directories relative to the Emby program data folder that are never archived.
        /// Metadata is excluded completely, including NFO and artwork.
        /// </summary>
        public static readonly string[] ExcludedDirectories =
        {
            "metadata",
            "cache",
            "logs",
            "transcoding-temp",
            "data/camerauploads",
            "data/livetv/recordings",
            "data/subtitles",
            "data/sync",
        };

        /// <summary>
        /// File extensions that are never archived. NFO and log files are metadata/diagnostics, not server data.
        /// Database sidecars are handled separately: the databases themselves are replaced by consistent snapshots.
        /// </summary>
        public static readonly string[] ExcludedFileExtensions =
        {
            ".nfo",
            ".log",
            ".db-wal",
            ".db-shm",
            ".db-journal",
            ".sqlite-wal",
            ".sqlite-shm",
            ".sqlite-journal",
            ".sqlite3-wal",
            ".sqlite3-shm",
            ".sqlite3-journal",
            ".tmp",
            // Executables and debug symbols are not server data. Emby's own program files live outside
            // ProgramData, but plugin DLLs are copied into programdata/plugins and must not be archived.
            ".dll",
            ".exe",
            ".pdb",
            ".so",
            ".dylib",
        };

        public static readonly string[] ExcludedFileNames =
        {
            ".DS_Store",
            "Thumbs.db",
            "desktop.ini",
        };
    }

    [DataContract]
    public sealed class BackupManifest
    {
        [DataMember(Name = "format", Order = 0)]
        public string Format { get; set; } = BackupConstants.FormatName;

        [DataMember(Name = "formatVersion", Order = 1)]
        public int FormatVersion { get; set; } = BackupConstants.FormatVersion;

        [DataMember(Name = "pluginVersion", Order = 2)]
        public string PluginVersion { get; set; }

        [DataMember(Name = "embyVersion", Order = 3)]
        public string EmbyVersion { get; set; }

        [DataMember(Name = "createdUtc", Order = 4)]
        public string CreatedUtc { get; set; }

        [DataMember(Name = "sourceProgramDataPath", Order = 5)]
        public string SourceProgramDataPath { get; set; }

        [DataMember(Name = "operatingSystem", Order = 6)]
        public string OperatingSystem { get; set; }

        [DataMember(Name = "compression", Order = 7)]
        public string Compression { get; set; }

        [DataMember(Name = "databases", Order = 8)]
        public List<BackupDatabaseEntry> Databases { get; set; } = new List<BackupDatabaseEntry>();

        [DataMember(Name = "files", Order = 9)]
        public List<BackupFileEntry> Files { get; set; } = new List<BackupFileEntry>();

        [DataMember(Name = "excludedPaths", Order = 10)]
        public List<string> ExcludedPaths { get; set; } = new List<string>();

        [DataMember(Name = "warnings", Order = 11)]
        public List<string> Warnings { get; set; } = new List<string>();

        [DataMember(Name = "totalBytes", Order = 12)]
        public long TotalBytes { get; set; }

        [DataMember(Name = "excludedFilePatterns", Order = 13)]
        public List<string> ExcludedFilePatterns { get; set; } = new List<string>();
    }

    [DataContract]
    public sealed class BackupDatabaseEntry
    {
        [DataMember(Name = "path", Order = 0)]
        public string Path { get; set; }

        [DataMember(Name = "bytes", Order = 1)]
        public long Bytes { get; set; }

        [DataMember(Name = "sha256", Order = 2)]
        public string Sha256 { get; set; }

        [DataMember(Name = "sqliteVersion", Order = 3)]
        public string SqliteVersion { get; set; }

        [DataMember(Name = "quickCheck", Order = 4)]
        public string QuickCheck { get; set; }
    }

    [DataContract]
    public sealed class BackupFileEntry
    {
        [DataMember(Name = "path", Order = 0)]
        public string Path { get; set; }

        [DataMember(Name = "bytes", Order = 1)]
        public long Bytes { get; set; }

        [DataMember(Name = "sha256", Order = 2)]
        public string Sha256 { get; set; }

        [DataMember(Name = "lastWriteUtc", Order = 3)]
        public string LastWriteUtc { get; set; }
    }

    [DataContract]
    public sealed class BackupHistory
    {
        [DataMember(Name = "backups", Order = 0)]
        public List<BackupRecord> Backups { get; set; } = new List<BackupRecord>();
    }

    [DataContract]
    public sealed class BackupRecord
    {
        [DataMember(Name = "id", Order = 0)]
        public string Id { get; set; }

        [DataMember(Name = "fileName", Order = 1)]
        public string FileName { get; set; }

        [DataMember(Name = "destination", Order = 2)]
        public string Destination { get; set; }

        [DataMember(Name = "createdUtc", Order = 3)]
        public string CreatedUtc { get; set; }

        [DataMember(Name = "status", Order = 4)]
        public string Status { get; set; }

        [DataMember(Name = "verified", Order = 5)]
        public bool Verified { get; set; }

        [DataMember(Name = "bytes", Order = 6)]
        public long Bytes { get; set; }

        [DataMember(Name = "fileCount", Order = 7)]
        public int FileCount { get; set; }

        [DataMember(Name = "databaseCount", Order = 8)]
        public int DatabaseCount { get; set; }

        [DataMember(Name = "sha256", Order = 9)]
        public string Sha256 { get; set; }

        [DataMember(Name = "durationSeconds", Order = 10)]
        public double DurationSeconds { get; set; }

        [DataMember(Name = "error", Order = 11)]
        public string Error { get; set; }

        [DataMember(Name = "warnings", Order = 12)]
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public sealed class BackupRequest
    {
        public string ProgramDataPath { get; set; }
        public string PluginDataPath { get; set; }
        public string DestinationPath { get; set; }
        public string EmbyVersion { get; set; }
        public string PluginVersion { get; set; }
        public string Compression { get; set; }
        public int RetentionCount { get; set; }
        public int BusyTimeoutSeconds { get; set; }
        public bool VerifyAfterCreate { get; set; }
        public Action<string> Log { get; set; }
        public Action<double, string> Progress { get; set; }
    }

    public sealed class BackupRunResult
    {
        public bool Success { get; set; }
        public bool Verified { get; set; }
        public string ArchivePath { get; set; }
        public string Sha256 { get; set; }
        public long Bytes { get; set; }
        public int FileCount { get; set; }
        public int DatabaseCount { get; set; }
        public TimeSpan Duration { get; set; }
        public string Error { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public static class BackupFormat
    {
        public static string Utc(DateTime value)
        {
            return value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        public static string FileStamp(DateTime value)
        {
            return value.ToUniversalTime().ToString("yyyyMMdd-HHmmss'Z'", CultureInfo.InvariantCulture);
        }

        public static string Sha256(byte[] hash)
        {
            var text = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
            {
                text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        public static string Sha256File(string path)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan))
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                return Sha256(sha.ComputeHash(input));
            }
        }
    }

    public static class JsonCodec
    {
        public static void Write<T>(string path, T value)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = path + ".tmp";
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var serializer = new DataContractJsonSerializer(typeof(T));
                serializer.WriteObject(output, value);
                output.Flush(true);
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(temp, path);
        }

        public static T Read<T>(string path) where T : class
        {
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var serializer = new DataContractJsonSerializer(typeof(T));
                    return serializer.ReadObject(input) as T;
                }
            }
            catch
            {
                return null;
            }
        }

        public static T ReadFromStream<T>(Stream input) where T : class
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            return serializer.ReadObject(input) as T;
        }

        public static void WriteToStream<T>(Stream output, T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            serializer.WriteObject(output, value);
        }
    }
}
