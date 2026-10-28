using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace EmbyBackup.Core
{
    public sealed class BackupVerificationResult
    {
        public bool Valid { get; set; }
        public string Error { get; set; }
        public BackupManifest Manifest { get; set; }
        public int CheckedFiles { get; set; }
        public int CheckedDatabases { get; set; }
    }

    public sealed class BackupVerifier
    {
        public BackupVerificationResult Verify(string archivePath, bool compareSidecar)
        {
            var result = new BackupVerificationResult();
            if (!File.Exists(archivePath))
            {
                result.Error = "The backup file does not exist.";
                return result;
            }

            try
            {
                if (compareSidecar)
                {
                    var sidecar = archivePath + ".sha256";
                    if (File.Exists(sidecar))
                    {
                        var expected = File.ReadAllText(sidecar).Trim().Split(' ')[0];
                        var actual = BackupFormat.Sha256File(archivePath);
                        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                        {
                            result.Error = "The archive checksum does not match its .sha256 file.";
                            return result;
                        }
                    }
                }

                using (var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, false))
                {
                    var manifestEntry = archive.GetEntry(BackupConstants.ManifestEntryName);
                    if (manifestEntry == null)
                    {
                        result.Error = "manifest.json is missing.";
                        return result;
                    }

                    BackupManifest manifest;
                    using (var manifestStream = manifestEntry.Open())
                    {
                        manifest = JsonCodec.ReadFromStream<BackupManifest>(manifestStream);
                    }

                    if (manifest == null)
                    {
                        result.Error = "manifest.json could not be read.";
                        return result;
                    }

                    result.Manifest = manifest;
                    if (!string.Equals(manifest.Format, BackupConstants.FormatName, StringComparison.Ordinal))
                    {
                        result.Error = "This ZIP was not created by Emby Backup.";
                        return result;
                    }
                    if (manifest.FormatVersion != BackupConstants.FormatVersion)
                    {
                        result.Error = "Unsupported backup format version: " + manifest.FormatVersion;
                        return result;
                    }

                    var expectedNames = new HashSet<string>(StringComparer.Ordinal);
                    expectedNames.Add(BackupConstants.ManifestEntryName);

                    foreach (var file in manifest.Files)
                    {
                        var error = VerifyEntry(archive, file.Path, file.Bytes, file.Sha256, expectedNames);
                        if (error != null)
                        {
                            result.Error = error;
                            return result;
                        }
                        result.CheckedFiles++;
                    }

                    foreach (var database in manifest.Databases)
                    {
                        var error = VerifyEntry(archive, database.Path, database.Bytes, database.Sha256, expectedNames);
                        if (error != null)
                        {
                            result.Error = error;
                            return result;
                        }
                        result.CheckedDatabases++;
                    }

                    foreach (var entry in archive.Entries)
                    {
                        if (!expectedNames.Contains(entry.FullName))
                        {
                            result.Error = "Unexpected file in archive: " + entry.FullName;
                            return result;
                        }
                    }
                }

                result.Valid = true;
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }
        }

        private static string VerifyEntry(ZipArchive archive, string name, long expectedBytes, string expectedSha, HashSet<string> seen)
        {
            if (string.IsNullOrEmpty(name) || !seen.Add(name))
            {
                return "Duplicate or empty path in manifest: " + name;
            }

            var entry = archive.GetEntry(name);
            if (entry == null)
            {
                return "Missing from archive: " + name;
            }

            if (entry.Length != expectedBytes)
            {
                return "Size mismatch for " + name + ": manifest says " + expectedBytes + ", archive says " + entry.Length;
            }

            using (var input = entry.Open())
            using (var sha = SHA256.Create())
            {
                var actual = BackupFormat.Sha256(sha.ComputeHash(input));
                if (!string.Equals(actual, expectedSha, StringComparison.OrdinalIgnoreCase))
                {
                    return "SHA-256 mismatch for " + name;
                }
            }

            return null;
        }
    }
}
