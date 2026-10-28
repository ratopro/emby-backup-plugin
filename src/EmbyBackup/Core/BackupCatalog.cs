using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

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
        public bool Encrypted { get; set; }
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

                using (var reader = new ZipReader(archivePath))
                {
                    info.Encrypted = reader.IsEncrypted;
                    if (reader.IsEncrypted)
                    {
                        // The manifest is encrypted too, so its contents cannot be shown without the
                        // password. The .sha256 sidecar is the ownership marker in this case.
                        info.IsEmbyBackup = info.HasChecksumFile;
                        info.CreatedUtcValue = TimestampFromName(file.Name, file.LastWriteTimeUtc);
                        return info;
                    }

                    var manifest = ReadManifest(reader, out var error);
                    if (manifest != null)
                    {
                        ApplyManifest(info, manifest);
                    }
                    else
                    {
                        info.Error = error;
                    }
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

        public static BackupManifest ReadManifest(string archivePath, out string error, string password = null)
        {
            error = null;
            try
            {
                using (var reader = new ZipReader(archivePath))
                {
                    if (reader.IsEncrypted && string.IsNullOrEmpty(password))
                    {
                        error = "This backup is protected by a password.";
                        return null;
                    }

                    return ReadManifest(reader, out error, password);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        private static BackupManifest ReadManifest(ZipReader reader, out string error, string password = null)
        {
            error = null;
            var entry = reader.Find(BackupConstants.ManifestEntryName);
            if (entry == null)
            {
                error = "manifest.json is missing.";
                return null;
            }

            try
            {
                var bytes = reader.ReadAllBytes(entry, password, 16 * 1024 * 1024);
                using (var stream = new MemoryStream(bytes, false))
                {
                    var manifest = JsonCodec.ReadFromStream<BackupManifest>(stream);
                    if (manifest == null)
                    {
                        error = "manifest.json could not be read.";
                    }
                    return manifest;
                }
            }
            catch (ZipPasswordRequiredException ex)
            {
                error = ex.Message;
                return null;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        private static void ApplyManifest(BackupArchiveInfo info, BackupManifest manifest)
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

        private static DateTime TimestampFromName(string fileName, DateTime fallback)
        {
            DateTime stamp;
            return BackupFormat.TryParseArchiveStamp(fileName, out stamp) ? stamp : fallback;
        }

        public static bool IsManagedFileName(string fileName)
        {
            return !string.IsNullOrEmpty(fileName)
                && fileName.IndexOf('/') < 0
                && fileName.IndexOf('\\') < 0
                && fileName.StartsWith(BackupConstants.ArchivePrefix, StringComparison.OrdinalIgnoreCase)
                && fileName.EndsWith(BackupConstants.ArchiveSuffix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when a foreign file proves it belongs to this plugin: a valid manifest, or the AES
        /// container plus the .sha256 sidecar written by the plugin (the manifest cannot be read without
        /// the password).
        /// </summary>
        public static bool IsOwnedArchive(string archivePath)
        {
            var manifest = ReadManifest(archivePath, out _);
            if (manifest != null)
            {
                return string.Equals(manifest.Format, BackupConstants.FormatName, StringComparison.Ordinal);
            }

            var sidecar = archivePath + BackupConstants.SidecarSuffix;
            if (!File.Exists(sidecar))
            {
                return false;
            }

            try
            {
                var text = File.ReadAllText(sidecar);
                string ignored;
                if (!BackupFormat.IsValidSidecar(text, Path.GetFileName(archivePath), out ignored))
                {
                    return false;
                }

                bool encrypted;
                string error;
                return ZipReader.TryInspect(archivePath, out encrypted, out error) && encrypted;
            }
            catch
            {
                return false;
            }
        }
    }
}
