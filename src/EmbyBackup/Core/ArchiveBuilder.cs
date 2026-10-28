using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;

namespace EmbyBackup.Core
{
    public sealed class ArchiveSource
    {
        public string RelativePath { get; set; }
        public string FullPath { get; set; }
        public DateTime LastWriteUtc { get; set; }
        public bool IsDatabase { get; set; }
        public string DatabaseSqliteVersion { get; set; }
        public string DatabaseQuickCheck { get; set; }
    }

    public sealed class ArchiveBuildResult
    {
        public string ArchivePath { get; set; }
        public string Sha256 { get; set; }
        public long Bytes { get; set; }
        public int FileCount { get; set; }
        public int DatabaseCount { get; set; }
        public bool Encrypted { get; set; }
        public BackupManifest Manifest { get; set; }
    }

    public sealed class BackupArchiveBuilder
    {
        private readonly Action<string> _log;
        private readonly Action<double, string> _progress;

        public BackupArchiveBuilder(Action<string> log, Action<double, string> progress)
        {
            _log = log ?? (_ => { });
            _progress = progress ?? ((p, s) => { });
        }

        public ArchiveBuildResult Build(
            string tempZipPath,
            string pluginVersion,
            string embyVersion,
            string sourceProgramDataPath,
            string compression,
            IEnumerable<ArchiveSource> sources,
            IEnumerable<string> excludedPaths,
            IEnumerable<string> warnings,
            string password,
            string spoolDirectory,
            CancellationToken cancellationToken)
        {
            var list = new List<ArchiveSource>(sources);
            var manifest = new BackupManifest
            {
                PluginVersion = pluginVersion,
                EmbyVersion = embyVersion,
                CreatedUtc = BackupFormat.Utc(DateTime.UtcNow),
                SourceProgramDataPath = sourceProgramDataPath,
                OperatingSystem = Environment.OSVersion.ToString(),
                Compression = compression,
                ExcludedPaths = Distinct(excludedPaths),
                ExcludedFilePatterns = ExcludedFilePatterns(),
                Warnings = new List<string>(warnings ?? new string[0]),
            };

            var totalInputBytes = 0L;
            foreach (var source in list)
            {
                totalInputBytes += SafeLength(source.FullPath);
            }

            var level = ParseCompression(compression);
            var processed = 0L;
            var encrypted = !string.IsNullOrEmpty(password);

            using (var writer = CreateWriter(tempZipPath, password, spoolDirectory))
            {
                foreach (var source in list)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    FileStream input;
                    try
                    {
                        input = new FileStream(
                            source.FullPath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete,
                            1024 * 1024,
                            FileOptions.SequentialScan);
                    }
                    catch (FileNotFoundException)
                    {
                        manifest.Warnings.Add("Skipped a file that disappeared during the backup: " + source.RelativePath);
                        continue;
                    }
                    catch (DirectoryNotFoundException)
                    {
                        manifest.Warnings.Add("Skipped a folder that disappeared during the backup: " + source.RelativePath);
                        continue;
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        manifest.Warnings.Add("Could not read " + source.RelativePath + ": " + ex.Message);
                        continue;
                    }
                    catch (IOException ex)
                    {
                        // A database snapshot that cannot be read means the backup is incomplete in a way
                        // that matters, so it must fail. Other files (config, XML) are skipped with a warning.
                        if (source.IsDatabase)
                        {
                            throw;
                        }

                        manifest.Warnings.Add("Skipped " + source.RelativePath + ": " + ex.Message);
                        continue;
                    }

                    using (input)
                    {
                        var archiveName = PathSafety.ToArchivePath(source.RelativePath);
                        var entry = writer.AddEntry(
                            archiveName,
                            source.LastWriteUtc,
                            input,
                            level,
                            cancellationToken);

                        if (source.IsDatabase)
                        {
                            manifest.Databases.Add(new BackupDatabaseEntry
                            {
                                Path = archiveName,
                                Bytes = entry.PlainBytes,
                                Sha256 = entry.Sha256,
                                SqliteVersion = source.DatabaseSqliteVersion,
                                QuickCheck = source.DatabaseQuickCheck,
                            });
                        }
                        else
                        {
                            manifest.Files.Add(new BackupFileEntry
                            {
                                Path = archiveName,
                                Bytes = entry.PlainBytes,
                                Sha256 = entry.Sha256,
                                LastWriteUtc = BackupFormat.Utc(source.LastWriteUtc),
                            });
                        }

                        processed += entry.PlainBytes;
                        var percent = totalInputBytes <= 0 ? 90 : Math.Min(90, (processed * 90.0) / totalInputBytes);
                        _progress(percent, "Archiving " + archiveName);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                manifest.TotalBytes = processed;
                writer.AddBytes(
                    BackupConstants.ManifestEntryName,
                    DateTime.UtcNow,
                    SerializeManifest(manifest),
                    CompressionLevel.Optimal,
                    cancellationToken);
                writer.Complete();
            }

            _progress(95, "Calculating archive checksum");
            var archiveHash = BackupFormat.Sha256File(tempZipPath);
            var archiveBytes = new FileInfo(tempZipPath).Length;

            _progress(100, "Archive ready");
            _log("Archive built: " + tempZipPath + " (" + archiveBytes + " bytes"
                + (encrypted ? ", password protected" : string.Empty) + ")");

            return new ArchiveBuildResult
            {
                ArchivePath = tempZipPath,
                Sha256 = archiveHash,
                Bytes = archiveBytes,
                FileCount = manifest.Files.Count,
                DatabaseCount = manifest.Databases.Count,
                Encrypted = encrypted,
                Manifest = manifest,
            };
        }

        private static IArchiveEntryWriter CreateWriter(string path, string password, string spoolDirectory)
        {
            if (string.IsNullOrEmpty(password))
            {
                return new PlainZipEntryWriter(path);
            }

            return new AesZipEntryWriter(path, password, spoolDirectory);
        }

        private static byte[] SerializeManifest(BackupManifest manifest)
        {
            using (var stream = new MemoryStream())
            {
                JsonCodec.WriteToStream(stream, manifest);
                return stream.ToArray();
            }
        }

        private interface IArchiveEntryWriter : IDisposable
        {
            ZipArchiveEntryResult AddEntry(
                string name,
                DateTime lastWriteUtc,
                Stream input,
                CompressionLevel level,
                CancellationToken cancellationToken);

            void AddBytes(
                string name,
                DateTime lastWriteUtc,
                byte[] data,
                CompressionLevel level,
                CancellationToken cancellationToken);

            void Complete();
        }

        private sealed class PlainZipEntryWriter : IArchiveEntryWriter
        {
            private readonly FileStream _output;
            private readonly ZipArchive _archive;

            public PlainZipEntryWriter(string path)
            {
                _output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024);
                _archive = new ZipArchive(_output, ZipArchiveMode.Create, true);
            }

            public ZipArchiveEntryResult AddEntry(
                string name,
                DateTime lastWriteUtc,
                Stream input,
                CompressionLevel level,
                CancellationToken cancellationToken)
            {
                var entry = _archive.CreateEntry(name, level);
                SetTimestamp(entry, lastWriteUtc);

                using (var entryStream = entry.Open())
                {
                    long bytesRead;
                    byte[] hash;
                    using (var sha = SHA256.Create())
                    {
                        bytesRead = CopyAndHash(input, entryStream, sha, out hash);
                    }

                    return new ZipArchiveEntryResult
                    {
                        PlainBytes = bytesRead,
                        Sha256 = BackupFormat.Sha256(hash),
                    };
                }
            }

            public void AddBytes(
                string name,
                DateTime lastWriteUtc,
                byte[] data,
                CompressionLevel level,
                CancellationToken cancellationToken)
            {
                using (var input = new MemoryStream(data ?? new byte[0], false))
                {
                    AddEntry(name, lastWriteUtc, input, level, cancellationToken);
                }
            }

            public void Complete()
            {
                // Disposing the ZipArchive writes the central directory.
            }

            public void Dispose()
            {
                _archive.Dispose();
                _output.Dispose();
            }
        }

        private sealed class AesZipEntryWriter : IArchiveEntryWriter
        {
            private readonly AesZipWriter _writer;

            public AesZipEntryWriter(string path, string password, string spoolDirectory)
            {
                _writer = new AesZipWriter(path, password, spoolDirectory);
            }

            public ZipArchiveEntryResult AddEntry(
                string name,
                DateTime lastWriteUtc,
                Stream input,
                CompressionLevel level,
                CancellationToken cancellationToken)
            {
                return _writer.AddEntry(name, lastWriteUtc, input, level, cancellationToken);
            }

            public void AddBytes(
                string name,
                DateTime lastWriteUtc,
                byte[] data,
                CompressionLevel level,
                CancellationToken cancellationToken)
            {
                _writer.AddBytes(name, lastWriteUtc, data, level, cancellationToken);
            }

            public void Complete()
            {
                _writer.Complete();
            }

            public void Dispose()
            {
                _writer.Dispose();
            }
        }

        private static List<string> Distinct(IEnumerable<string> paths)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<string>();
            foreach (var path in paths ?? new string[0])
            {
                if (!string.IsNullOrEmpty(path) && seen.Add(path))
                {
                    result.Add(path);
                }
            }
            return result;
        }

        private static List<string> ExcludedFilePatterns()
        {
            var patterns = new List<string>();
            foreach (var extension in BackupConstants.ExcludedFileExtensions)
            {
                patterns.Add("*" + extension);
            }
            foreach (var name in BackupConstants.ExcludedFileNames)
            {
                patterns.Add(name);
            }
            return patterns;
        }

        public static void SetTimestamp(ZipArchiveEntry entry, DateTime lastWriteUtc)
        {
            if (lastWriteUtc.Year < 1980 || lastWriteUtc.Year > 2107)
            {
                return;
            }

            entry.LastWriteTime = new DateTimeOffset(DateTime.SpecifyKind(lastWriteUtc, DateTimeKind.Utc));
        }

        private static long SafeLength(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch
            {
                return 0;
            }
        }

        private static CompressionLevel ParseCompression(string value)
        {
            if (string.Equals(value, "Fastest", StringComparison.OrdinalIgnoreCase))
            {
                return CompressionLevel.Fastest;
            }
            if (string.Equals(value, "NoCompression", StringComparison.OrdinalIgnoreCase))
            {
                return CompressionLevel.NoCompression;
            }
            return CompressionLevel.Optimal;
        }

        private static long CopyAndHash(Stream input, Stream output, SHA256 sha, out byte[] hash)
        {
            var buffer = new byte[1024 * 1024];
            var total = 0L;
            while (true)
            {
                var read = input.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                output.Write(buffer, 0, read);
                sha.TransformBlock(buffer, 0, read, buffer, 0);
                total += read;
            }

            sha.TransformFinalBlock(new byte[0], 0, 0);
            hash = sha.Hash;
            sha.Initialize();
            return total;
        }
    }
}
