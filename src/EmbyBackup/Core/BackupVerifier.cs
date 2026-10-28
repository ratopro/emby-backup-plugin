using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace EmbyBackup.Core
{
    public sealed class BackupVerificationResult
    {
        public bool Valid { get; set; }
        public bool Encrypted { get; set; }
        public bool PasswordRequired { get; set; }
        public bool WrongPassword { get; set; }
        public string Error { get; set; }
        public BackupManifest Manifest { get; set; }
        public int CheckedFiles { get; set; }
        public int CheckedDatabases { get; set; }
    }

    public sealed class BackupVerifier
    {
        public BackupVerificationResult Verify(string archivePath, bool compareSidecar, string password = null)
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
                    var sidecar = archivePath + BackupConstants.SidecarSuffix;
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

                using (var reader = new ZipReader(archivePath))
                {
                    result.Encrypted = reader.IsEncrypted;
                    if (reader.IsEncrypted && string.IsNullOrEmpty(password))
                    {
                        result.PasswordRequired = true;
                        result.Error = "This backup is protected by a password.";
                        return result;
                    }

                    var manifestEntry = reader.Find(BackupConstants.ManifestEntryName);
                    if (manifestEntry == null)
                    {
                        result.Error = "manifest.json is missing.";
                        return result;
                    }

                    BackupManifest manifest;
                    try
                    {
                        var manifestBytes = reader.ReadAllBytes(manifestEntry, password, 16 * 1024 * 1024);
                        using (var manifestStream = new MemoryStream(manifestBytes, false))
                        {
                            manifest = JsonCodec.ReadFromStream<BackupManifest>(manifestStream);
                        }
                    }
                    catch (ZipPasswordRequiredException ex)
                    {
                        result.PasswordRequired = true;
                        result.Error = ex.Message;
                        return result;
                    }
                    catch (ZipWrongPasswordException ex)
                    {
                        result.WrongPassword = true;
                        result.Error = ex.Message;
                        return result;
                    }
                    catch (ZipAuthenticationException ex)
                    {
                        result.WrongPassword = true;
                        result.Error = ex.Message;
                        return result;
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
                        var error = VerifyEntry(reader, file.Path, file.Bytes, file.Sha256, password, expectedNames, result);
                        if (error != null)
                        {
                            result.Error = error;
                            return result;
                        }
                        result.CheckedFiles++;
                    }

                    foreach (var database in manifest.Databases)
                    {
                        var error = VerifyEntry(reader, database.Path, database.Bytes, database.Sha256, password,
                            expectedNames, result);
                        if (error != null)
                        {
                            result.Error = error;
                            return result;
                        }
                        result.CheckedDatabases++;
                    }

                    foreach (var entry in reader.Entries)
                    {
                        if (!expectedNames.Contains(entry.Name))
                        {
                            result.Error = "Unexpected file in archive: " + entry.Name;
                            return result;
                        }
                    }
                }

                result.Valid = true;
                return result;
            }
            catch (ZipPasswordRequiredException ex)
            {
                result.PasswordRequired = true;
                result.Error = ex.Message;
                return result;
            }
            catch (ZipWrongPasswordException ex)
            {
                result.WrongPassword = true;
                result.Error = ex.Message;
                return result;
            }
            catch (ZipAuthenticationException ex)
            {
                result.WrongPassword = true;
                result.Error = ex.Message;
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }
        }

        private static string VerifyEntry(
            ZipReader reader,
            string name,
            long expectedBytes,
            string expectedSha,
            string password,
            HashSet<string> seen,
            BackupVerificationResult result)
        {
            if (string.IsNullOrEmpty(name) || !seen.Add(name))
            {
                return "Duplicate or empty path in manifest: " + name;
            }

            var entry = reader.Find(name);
            if (entry == null)
            {
                return "Missing from archive: " + name;
            }

            if (entry.UncompressedSize != expectedBytes)
            {
                return "Size mismatch for " + name + ": manifest says " + expectedBytes
                    + ", archive says " + entry.UncompressedSize;
            }

            try
            {
                using (var handle = reader.OpenEntry(entry, password))
                using (var sha = SHA256.Create())
                {
                    var buffer = new byte[1024 * 1024];
                    while (true)
                    {
                        var read = handle.Stream.Read(buffer, 0, buffer.Length);
                        if (read <= 0)
                        {
                            break;
                        }
                        sha.TransformBlock(buffer, 0, read, buffer, 0);
                    }

                    handle.Complete();
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    var actual = BackupFormat.Sha256(sha.Hash);
                    if (!string.Equals(actual, expectedSha, StringComparison.OrdinalIgnoreCase))
                    {
                        return "SHA-256 mismatch for " + name;
                    }
                }
            }
            catch (ZipPasswordRequiredException ex)
            {
                result.PasswordRequired = true;
                return ex.Message;
            }
            catch (ZipWrongPasswordException ex)
            {
                result.WrongPassword = true;
                return ex.Message;
            }
            catch (ZipAuthenticationException ex)
            {
                result.WrongPassword = true;
                return ex.Message;
            }
            catch (InvalidDataException ex)
            {
                return "Could not read " + name + ": " + ex.Message;
            }

            return null;
        }
    }
}
