using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;

namespace EmbyBackup.Core
{
    public sealed class BackupArchiveInfo
    {
        public string FileName { get; set; }
        public string FullPath { get; set; }
        public long Bytes { get; set; }
        public DateTime LastWriteUtc { get; set; }
        public string CreatedUtc { get; set; }
        public DateTime CreatedUtcValue { get; set; }
        public bool IsEmbyBackup { get; set; }
        public bool HasManifest { get; set; }
        public string Error { get; set; }
        public int FileCount { get; set; }
        public int DatabaseCount { get; set; }
        public long TotalBytes { get; set; }
        public bool HasChecksumFile { get; set; }
    }

    public static class BackupCatalog
    {
        public static List<BackupArchiveInfo> List(string destinationPath)
        {
            var result = new List<BackupArchiveInfo>();
            if (string.IsNullOrWhiteSpace(destinationPath) || !Directory.Exists(destinationPath))
            {
                return result;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(destinationPath, BackupConstants.ArchivePrefix + "*" + BackupConstants.ArchiveSuffix, SearchOption.TopDirectoryOnly);
            }
            catch
            {
                return result;
            }

            foreach (var path in files)
            {
                var name = Path.GetFileName(path);
                if (!name.StartsWith(BackupConstants.ArchivePrefix, StringComparison.OrdinalIgnoreCase)
                    || !name.EndsWith(BackupConstants.ArchiveSuffix, StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(BackupConstants.SidecarSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var info = Inspect(path);
                if (info != null)
                {
                    result.Add(info);
                }
            }

            result.Sort((a, b) => b.CreatedUtcValue.CompareTo(a.CreatedUtcValue));
            return result;
        }

        public static BackupArchiveInfo Inspect(string archivePath)
        {
            try
            {
                var file = new FileInfo(archivePath);
                if (!file.Exists)
                {
                    return null;
                }

                if ((file.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                {
                    return null;
                }

                var info = new BackupArchiveInfo
                {
                    FileName = file.Name,
                    FullPath = file.FullName,
                    Bytes = file.Length,
                    LastWriteUtc = file.LastWriteTimeUtc,
                    CreatedUtcValue = file.LastWriteTimeUtc,
                    HasChecksumFile = File.Exists(archivePath + BackupConstants.SidecarSuffix),
                };

                var manifest = ReadManifest(archivePath, out var error);
                if (manifest != null)
                {
                    info.HasManifest = true;
                    info.IsEmbyBackup = string.Equals(manifest.Format, BackupConstants.FormatName, StringComparison.Ordinal);
                    info.CreatedUtc = manifest.CreatedUtc;
                    info.FileCount = manifest.Files == null ? 0 : manifest.Files.Count;
                    info.DatabaseCount = manifest.Databases == null ? 0 : manifest.Databases.Count;
                    info.TotalBytes = manifest.TotalBytes;

                    if (DateTime.TryParse(
                        manifest.CreatedUtc,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                        out var created))
                    {
                        info.CreatedUtcValue = created;
                    }
                }
                else
                {
                    info.Error = error;
                }

                return info;
            }
            catch (Exception ex)
            {
                return new BackupArchiveInfo
                {
                    FileName = Path.GetFileName(archivePath),
                    FullPath = archivePath,
                    Error = ex.Message,
                };
            }
        }

        public static BackupManifest ReadManifest(string archivePath, out string error)
        {
            error = null;
            try
            {
                using (var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, false))
                {
                    var entry = archive.GetEntry(BackupConstants.ManifestEntryName);
                    if (entry == null)
                    {
                        error = "manifest.json is missing.";
                        return null;
                    }

                    using (var manifestStream = entry.Open())
                    {
                        var manifest = JsonCodec.ReadFromStream<BackupManifest>(manifestStream);
                        if (manifest == null)
                        {
                            error = "manifest.json could not be read.";
                        }
                        return manifest;
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        public static bool IsManagedFileName(string fileName)
        {
            return !string.IsNullOrEmpty(fileName)
                && fileName.IndexOf('/') < 0
                && fileName.IndexOf('\\') < 0
                && fileName.StartsWith(BackupConstants.ArchivePrefix, StringComparison.OrdinalIgnoreCase)
                && fileName.EndsWith(BackupConstants.ArchiveSuffix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
