using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace EmbyBackup.Core
{
    public sealed class BackupDestinationFile
    {
        public string Name { get; set; }
        public long Bytes { get; set; }
        public DateTime ModifiedUtc { get; set; }
        public bool HasSidecar { get; set; }
    }

    public sealed class BackupCommitResult
    {
        public string Location { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>
    /// Where a finished archive is published. Local folders and mounted/UNC shares use the filesystem;
    /// FTP/FTPS builds the archive in a local staging file and uploads it.
    /// </summary>
    public interface IBackupDestination : IDisposable
    {
        string Kind { get; }

        /// <summary>Destination as shown in the UI and the history. Never contains a password.</summary>
        string Location { get; }

        /// <summary>Filesystem root when the destination is a folder, or null for FTP.</summary>
        string LocalRootPath { get; }

        bool CanCheckFreeSpace { get; }

        void Prepare();

        string CreateStagingPath(string fileName);

        BackupCommitResult Commit(string stagingPath, string fileName, string sha256, Action<string> log);

        IReadOnlyList<BackupDestinationFile> ListArchives();

        IReadOnlyList<BackupDestinationFile> ListAll();

        /// <summary>True when the archive is provably ours: a valid manifest (folder) or a valid sidecar (FTP).</summary>
        bool IsOwned(BackupDestinationFile file);

        void Delete(string fileName, Action<string> log);

        /// <summary>Returns a local path that can be read by the verifier (downloads it for FTP).</summary>
        string Fetch(string fileName, string purpose);

        void Release(string localPath);

        void Discard(string stagingPath);
    }

    public static class BackupDestinations
    {
        public static IBackupDestination Create(
            BackupDestinationSettings settings,
            string programDataPath,
            Action<string> log)
        {
            if (settings == null)
            {
                throw new InvalidOperationException("Set a backup destination first.");
            }

            log = log ?? (_ => { });

            if (settings.IsFtp)
            {
                return new FtpBackupDestination(settings, log);
            }

            if (string.IsNullOrWhiteSpace(settings.Path))
            {
                throw new InvalidOperationException(settings.IsNetwork
                    ? "Enter the network share path first."
                    : "Choose a backup folder first.");
            }

            return new LocalBackupDestination(settings, programDataPath, log);
        }
    }

    public sealed class LocalBackupDestination : IBackupDestination
    {
        private readonly BackupDestinationSettings _settings;
        private readonly string _programDataPath;
        private readonly Action<string> _log;
        private readonly string _root;

        public LocalBackupDestination(BackupDestinationSettings settings, string programDataPath, Action<string> log)
        {
            _settings = settings;
            _programDataPath = programDataPath;
            _log = log ?? (_ => { });
            _root = Path.GetFullPath(settings.Path);
        }

        public string Kind
        {
            get { return _settings.NormalizedType; }
        }

        public string Location
        {
            get { return _root; }
        }

        public string LocalRootPath
        {
            get { return _root; }
        }

        public bool CanCheckFreeSpace
        {
            get { return true; }
        }

        public void Prepare()
        {
            if (_settings.IsNetwork)
            {
                NetworkShareConnector.Connect(
                    _root,
                    _settings.NetworkUsername,
                    _settings.NetworkPassword,
                    _settings.NetworkDomain,
                    _log);
            }

            PathSafety.ValidateBackupDestination(_programDataPath, _root);
            PathSafety.EnsureDestinationWritable(_root);
        }

        public string CreateStagingPath(string fileName)
        {
            return Path.Combine(_root, fileName + BackupConstants.PartialSuffix);
        }

        public BackupCommitResult Commit(string stagingPath, string fileName, string sha256, Action<string> log)
        {
            log = log ?? _log;
            var result = new BackupCommitResult();
            var finalPath = Path.Combine(_root, fileName);

            File.Move(stagingPath, finalPath);

            var sidecarPath = finalPath + BackupConstants.SidecarSuffix;
            try
            {
                var temp = sidecarPath + ".tmp";
                File.WriteAllText(temp, BackupFormat.SidecarText(sha256, fileName));
                if (File.Exists(sidecarPath))
                {
                    File.Delete(sidecarPath);
                }
                File.Move(temp, sidecarPath);
            }
            catch (Exception ex)
            {
                result.Warnings.Add("The archive was created but its .sha256 file could not be written: " + ex.Message);
                log("Could not write the .sha256 file: " + ex);
            }

            result.Location = finalPath;
            return result;
        }

        public IReadOnlyList<BackupDestinationFile> ListArchives()
        {
            var result = new List<BackupDestinationFile>();
            if (!Directory.Exists(_root))
            {
                return result;
            }

            foreach (var path in Directory.GetFiles(
                _root,
                BackupConstants.ArchivePrefix + "*" + BackupConstants.ArchiveSuffix,
                SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                if (!IsArchiveName(name))
                {
                    continue;
                }

                var info = new FileInfo(path);
                result.Add(new BackupDestinationFile
                {
                    Name = name,
                    Bytes = info.Length,
                    ModifiedUtc = info.LastWriteTimeUtc,
                    HasSidecar = File.Exists(path + BackupConstants.SidecarSuffix),
                });
            }

            return result;
        }

        public IReadOnlyList<BackupDestinationFile> ListAll()
        {
            var result = new List<BackupDestinationFile>();
            if (!Directory.Exists(_root))
            {
                return result;
            }

            foreach (var path in Directory.GetFiles(_root, "*", SearchOption.TopDirectoryOnly))
            {
                var info = new FileInfo(path);
                result.Add(new BackupDestinationFile
                {
                    Name = info.Name,
                    Bytes = info.Length,
                    ModifiedUtc = info.LastWriteTimeUtc,
                    HasSidecar = info.Name.EndsWith(BackupConstants.SidecarSuffix, StringComparison.OrdinalIgnoreCase),
                });
            }

            return result;
        }

        public bool IsOwned(BackupDestinationFile file)
        {
            var path = Path.Combine(_root, file.Name);
            var manifest = BackupCatalog.ReadManifest(path, out _);
            return manifest != null
                && string.Equals(manifest.Format, BackupConstants.FormatName, StringComparison.Ordinal);
        }

        public void Delete(string fileName, Action<string> log)
        {
            log = log ?? _log;
            var path = Path.Combine(_root, RequireSafeName(fileName));
            if (File.Exists(path))
            {
                File.Delete(path);
                log("Removed old backup: " + fileName);
            }

            var sidecar = path + BackupConstants.SidecarSuffix;
            try
            {
                if (File.Exists(sidecar))
                {
                    File.Delete(sidecar);
                }
            }
            catch
            {
                // A stray .sha256 file is harmless.
            }
        }

        public string Fetch(string fileName, string purpose)
        {
            return Path.Combine(_root, RequireSafeName(fileName));
        }

        public void Release(string localPath)
        {
            // Nothing to release: the archive lives in the destination.
        }

        public void Discard(string stagingPath)
        {
            try
            {
                if (!string.IsNullOrEmpty(stagingPath) && File.Exists(stagingPath))
                {
                    File.Delete(stagingPath);
                }
            }
            catch (Exception ex)
            {
                _log("Could not remove the unfinished archive " + stagingPath + ": " + ex.Message);
            }
        }

        public void Dispose()
        {
        }

        internal static bool IsArchiveName(string name)
        {
            return !string.IsNullOrEmpty(name)
                && name.StartsWith(BackupConstants.ArchivePrefix, StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(BackupConstants.ArchiveSuffix, StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(BackupConstants.SidecarSuffix, StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(BackupConstants.PartialSuffix, StringComparison.OrdinalIgnoreCase);
        }

        internal static string RequireSafeName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)
                || fileName.IndexOf('/') >= 0
                || fileName.IndexOf('\\') >= 0
                || fileName == "."
                || fileName == "..")
            {
                throw new InvalidOperationException("Unsafe backup file name: " + fileName);
            }

            return fileName;
        }
    }

    /// <summary>
    /// FTP and explicit FTPS (AUTH TLS) uploads. The archive is built in a local staging file, uploaded as
    /// .partial, renamed to its final name and then followed by the .sha256 sidecar.
    /// </summary>
    public sealed class FtpBackupDestination : IBackupDestination
    {
#pragma warning disable SYSLIB0014 // FtpWebRequest is obsolete for new code but still supported and sufficient here.
        private const string TempFolderName = "embybackup-ftp";
        private readonly BackupDestinationSettings _settings;
        private readonly Action<string> _log;
        private readonly string _host;
        private readonly int _port;
        private readonly string _remoteDirectory;

        public FtpBackupDestination(BackupDestinationSettings settings, Action<string> log)
        {
            _settings = settings;
            _log = log ?? (_ => { });
            _host = (settings.FtpHost ?? string.Empty).Trim();
            _port = settings.FtpPort <= 0 ? 21 : settings.FtpPort;
            _remoteDirectory = BackupDestinationSettings.NormalizeFtpPath(settings.FtpPath);

            if (_settings.FtpUseTls && _settings.FtpAcceptAnyCertificate)
            {
                FtpTlsPolicy.RelaxCertificateChecks();
            }
        }

        public string Kind
        {
            get { return BackupDestinationKinds.Ftp; }
        }

        public string Location
        {
            get { return _settings.Location.TrimEnd('/'); }
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
            if (_host.Length == 0)
            {
                throw new InvalidOperationException("Enter the FTP server address first.");
            }

            if (_port < 1 || _port > 65535)
            {
                throw new InvalidOperationException("The FTP port must be between 1 and 65535.");
            }

            try
            {
                EnsureDirectory();

                var probe = ".embybackup-write-" + Guid.NewGuid().ToString("N") + ".tmp";
                UploadText(probe, "probe");
                DeleteRemote(probe);
            }
            catch (Exception ex) when (ex is WebException || ex is IOException)
            {
                throw new InvalidOperationException(
                    "Cannot write to the FTP destination '" + Location + "': " + Describe(ex)
                    + " Check the server, the remote folder and the credentials.", ex);
            }
        }

        public string CreateStagingPath(string fileName)
        {
            var directory = Path.Combine(Path.GetTempPath(), TempFolderName);
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, Guid.NewGuid().ToString("N") + "-" + fileName);
        }

        public BackupCommitResult Commit(string stagingPath, string fileName, string sha256, Action<string> log)
        {
            log = log ?? _log;
            var result = new BackupCommitResult();
            var partialName = fileName + BackupConstants.PartialSuffix;

            UploadFile(stagingPath, partialName);
            try
            {
                RenameWithFallback(partialName, fileName);
            }
            catch (Exception ex)
            {
                result.Warnings.Add("The FTP server could not rename the uploaded archive; it was uploaded again "
                    + "with the final name: " + Describe(ex));
                UploadFile(stagingPath, fileName);
                TryDeleteRemote(partialName);
            }

            try
            {
                UploadText(fileName + BackupConstants.SidecarSuffix, BackupFormat.SidecarText(sha256, fileName));
            }
            catch (Exception ex)
            {
                result.Warnings.Add("The archive was uploaded but its .sha256 file could not be uploaded: " + Describe(ex));
                log("Could not upload the .sha256 file: " + ex);
            }

            try
            {
                File.Delete(stagingPath);
            }
            catch
            {
                // The staging folder is also cleaned on the next run.
            }

            result.Location = ArchiveLocation(fileName);
            return result;
        }

        public IReadOnlyList<BackupDestinationFile> ListArchives()
        {
            var entries = ListEntries();
            var set = new HashSet<string>(entries.Select(entry => entry.Name), StringComparer.OrdinalIgnoreCase);
            var result = new List<BackupDestinationFile>();

            foreach (var entry in entries)
            {
                if (!LocalBackupDestination.IsArchiveName(entry.Name))
                {
                    continue;
                }

                DateTime stamp;
                if (!BackupFormat.TryParseArchiveStamp(entry.Name, out stamp))
                {
                    stamp = DateTime.MinValue;
                }

                result.Add(new BackupDestinationFile
                {
                    Name = entry.Name,
                    Bytes = entry.Size,
                    ModifiedUtc = stamp,
                    HasSidecar = set.Contains(entry.Name + BackupConstants.SidecarSuffix),
                });
            }

            return result;
        }

        public IReadOnlyList<BackupDestinationFile> ListAll()
        {
            var entries = ListEntries();
            var set = new HashSet<string>(entries.Select(entry => entry.Name), StringComparer.OrdinalIgnoreCase);
            return entries
                .Select(entry =>
                {
                    DateTime stamp;
                    if (!BackupFormat.TryParseArchiveStamp(entry.Name, out stamp))
                    {
                        stamp = entry.Name.EndsWith(BackupConstants.PartialSuffix, StringComparison.OrdinalIgnoreCase)
                            ? TryGetModified(entry.Name)
                            : DateTime.MinValue;
                    }

                    return new BackupDestinationFile
                    {
                        Name = entry.Name,
                        Bytes = entry.Size,
                        ModifiedUtc = stamp,
                        HasSidecar = set.Contains(entry.Name + BackupConstants.SidecarSuffix),
                    };
                })
                .ToList();
        }

        public bool IsOwned(BackupDestinationFile file)
        {
            try
            {
                var text = DownloadText(file.Name + BackupConstants.SidecarSuffix);
                string sha256;
                return BackupFormat.IsValidSidecar(text, file.Name, out sha256);
            }
            catch
            {
                return false;
            }
        }

        public void Delete(string fileName, Action<string> log)
        {
            log = log ?? _log;
            LocalBackupDestination.RequireSafeName(fileName);
            TryDeleteRemote(fileName);
            TryDeleteRemote(fileName + BackupConstants.SidecarSuffix);
            log("Removed old backup: " + fileName);
        }

        public string Fetch(string fileName, string purpose)
        {
            var directory = Path.Combine(Path.GetTempPath(), TempFolderName);
            Directory.CreateDirectory(directory);
            var localPath = Path.Combine(
                directory,
                Guid.NewGuid().ToString("N") + "-" + LocalBackupDestination.RequireSafeName(fileName));

            try
            {
                DownloadFile(fileName, localPath);
            }
            catch (Exception ex) when (ex is WebException || ex is IOException)
            {
                try
                {
                    if (File.Exists(localPath))
                    {
                        File.Delete(localPath);
                    }
                }
                catch
                {
                    // Best effort.
                }

                throw new InvalidOperationException(
                    "Could not download '" + fileName + "' from the FTP destination: " + Describe(ex), ex);
            }

            return localPath;
        }

        public void Release(string localPath)
        {
            try
            {
                if (!string.IsNullOrEmpty(localPath) && File.Exists(localPath))
                {
                    File.Delete(localPath);
                }
            }
            catch
            {
                // The temporary folder is cleaned by the operating system.
            }
        }

        public void Discard(string stagingPath)
        {
            Release(stagingPath);
        }

        public void Dispose()
        {
        }

        private string ArchiveLocation(string fileName)
        {
            var location = Location;
            return location.EndsWith("/", StringComparison.Ordinal) ? location + fileName : location + "/" + fileName;
        }

        private void EnsureDirectory()
        {
            if (string.Equals(_remoteDirectory, "/", StringComparison.Ordinal))
            {
                return;
            }

            var segments = _remoteDirectory.Trim('/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var current = string.Empty;
            foreach (var segment in segments)
            {
                current += "/" + segment;
                try
                {
                    var request = CreateRequest(current, WebRequestMethods.Ftp.MakeDirectory, true);
                    using (request.GetResponse())
                    {
                    }
                }
                catch (WebException ex)
                {
                    if (!IsAlreadyExists(ex))
                    {
                        throw;
                    }
                }
            }
        }

        internal sealed class RemoteEntry
        {
            public string Name { get; set; }
            public long Size { get; set; }
        }

        /// <summary>
        /// One LIST request for names and sizes. FtpWebRequest does not support SIZE reliably on every
        /// platform (it fails on .NET Core against some servers), so the listing is parsed instead.
        /// </summary>
        private List<RemoteEntry> ListEntries()
        {
            var request = CreateRequest(_remoteDirectory, WebRequestMethods.Ftp.ListDirectoryDetails, true);
            var entries = new List<RemoteEntry>();
            using (var response = (FtpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream()))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var entry = ParseListing(line);
                    if (entry != null && entry.Name != "." && entry.Name != "..")
                    {
                        entries.Add(entry);
                    }
                }
            }

            return entries;
        }

        internal static RemoteEntry ParseListing(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            line = line.TrimEnd();
            var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                return null;
            }

            // Unix: "-rw-r--r-- 1 owner group 23815 Oct 28 13:52 emby-backup-....zip".
            // Directories start with 'd'; they are never archives.
            if (line.Length > 10 && (line[0] == '-' || line[0] == 'd' || line[0] == 'l') && tokens.Length >= 9)
            {
                // Directories and links are not ours and are skipped.
                if (line[0] != '-')
                {
                    return null;
                }

                long size;
                if (!long.TryParse(tokens[4], out size))
                {
                    size = -1;
                }

                var name = string.Join(" ", tokens, 8, tokens.Length - 8);
                return new RemoteEntry { Name = name, Size = size };
            }

            // IIS/DOS: "10-28-26  01:52PM       <DIR>          folder" or "... 23815 emby-backup.zip".
            if (tokens.Any(token => string.Equals(token, "<DIR>", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            if (tokens.Length >= 4 && tokens[0].IndexOf('-') > 0)
            {
                long size;
                var hasSize = long.TryParse(tokens[tokens.Length - 2], out size);
                return new RemoteEntry
                {
                    Name = tokens[tokens.Length - 1],
                    Size = hasSize ? size : -1,
                };
            }

            // Some appliances return bare names from LIST.
            var slash = line.LastIndexOf('/');
            return new RemoteEntry
            {
                Name = (slash >= 0 ? line.Substring(slash + 1) : line).Trim(),
                Size = -1,
            };
        }

        private DateTime TryGetModified(string fileName)
        {
            try
            {
                var request = CreateRequest(fileName, WebRequestMethods.Ftp.GetDateTimestamp);
                using (var response = (FtpWebResponse)request.GetResponse())
                {
                    return response.LastModified.ToUniversalTime();
                }
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private void UploadFile(string localPath, string remoteName)
        {
            var length = new FileInfo(localPath).Length;
            var request = CreateRequest(remoteName, WebRequestMethods.Ftp.UploadFile);
            request.ContentLength = length;
            using (var input = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = request.GetRequestStream())
            {
                input.CopyTo(output, 128 * 1024);
            }

            using (request.GetResponse())
            {
            }
        }

        private void UploadText(string remoteName, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var request = CreateRequest(remoteName, WebRequestMethods.Ftp.UploadFile);
            request.ContentLength = bytes.Length;
            using (var output = request.GetRequestStream())
            {
                output.Write(bytes, 0, bytes.Length);
            }

            using (request.GetResponse())
            {
            }
        }

        private void DownloadFile(string remoteName, string localPath)
        {
            var request = CreateRequest(remoteName, WebRequestMethods.Ftp.DownloadFile);
            using (var response = (FtpWebResponse)request.GetResponse())
            using (var input = response.GetResponseStream())
            using (var output = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output, 128 * 1024);
            }
        }

        private string DownloadText(string remoteName)
        {
            var request = CreateRequest(remoteName, WebRequestMethods.Ftp.DownloadFile);
            using (var response = (FtpWebResponse)request.GetResponse())
            using (var input = response.GetResponseStream())
            using (var reader = new StreamReader(input, Encoding.UTF8))
            {
                var buffer = new char[4096];
                var read = reader.Read(buffer, 0, buffer.Length);
                return new string(buffer, 0, Math.Max(0, read));
            }
        }

        private void RenameWithFallback(string from, string to)
        {
            try
            {
                RenameRemote(from, to);
            }
            catch (Exception)
            {
                // The target name is unique, but some servers refuse to rename over an existing file.
                TryDeleteRemote(to);
                RenameRemote(from, to);
            }
        }

        private void RenameRemote(string from, string to)
        {
            var request = CreateRequest(from, WebRequestMethods.Ftp.Rename);
            request.RenameTo = to;
            using (request.GetResponse())
            {
            }
        }

        private void DeleteRemote(string remoteName)
        {
            var request = CreateRequest(remoteName, WebRequestMethods.Ftp.DeleteFile);
            using (request.GetResponse())
            {
            }
        }

        private void TryDeleteRemote(string remoteName)
        {
            try
            {
                DeleteRemote(remoteName);
            }
            catch
            {
                // The file did not exist or the server refused; nothing to clean up.
            }
        }

        private FtpWebRequest CreateRequest(string remotePath, string method, bool isDirectory = false)
        {
            // Uploads, deletes and renames need a file URI without a trailing slash; listings and MKD need
            // the directory form. Passing the wrong one makes FtpWebRequest reject the URI locally.
            var uri = RemoteUri(remotePath, isDirectory);
            var request = (FtpWebRequest)WebRequest.Create(uri);
            request.Method = method;
            request.Credentials = new NetworkCredential(_settings.FtpUsername ?? string.Empty, _settings.FtpPassword ?? string.Empty);
            request.UsePassive = _settings.FtpPassive;
            request.UseBinary = true;
            request.KeepAlive = false;
            request.EnableSsl = _settings.FtpUseTls;
            request.Timeout = 120000;
            request.ReadWriteTimeout = 600000;
            return request;
        }

        private string RemoteUri(string remoteName)
        {
            return RemoteUri(remoteName, false);
        }

        private string RemoteUri(string remoteName, bool isDirectory)
        {
            string path;
            if (remoteName != null && remoteName.StartsWith("/", StringComparison.Ordinal))
            {
                path = remoteName;
            }
            else
            {
                var basePath = string.Equals(_remoteDirectory, "/", StringComparison.Ordinal)
                    ? "/"
                    : _remoteDirectory.TrimEnd('/') + "/";
                path = basePath + (remoteName ?? string.Empty);
            }

            if (isDirectory && !path.EndsWith("/", StringComparison.Ordinal))
            {
                path += "/";
            }

            var escaped = string.Join(
                "/",
                path.Split('/')
                    .Select(part => part.Length == 0 ? string.Empty : Uri.EscapeDataString(part)));

            var builder = new StringBuilder("ftp://");
            builder.Append(_host);
            if (_port != 21)
            {
                builder.Append(':').Append(_port.ToString(CultureInfo.InvariantCulture));
            }

            builder.Append(escaped.StartsWith("/", StringComparison.Ordinal) ? escaped : "/" + escaped);
            return builder.ToString();
        }

        private static bool IsAlreadyExists(WebException ex)
        {
            var response = ex.Response as FtpWebResponse;
            return response != null && (int)response.StatusCode == 550;
        }

        private static string Describe(Exception ex)
        {
            var web = ex as WebException;
            var response = web == null ? null : web.Response as FtpWebResponse;
            if (response != null)
            {
                return response.StatusDescription == null
                    ? ex.Message
                    : response.StatusDescription.Trim() + " (" + (int)response.StatusCode + ")";
            }

            return ex.Message;
        }
#pragma warning restore SYSLIB0014
    }

    /// <summary>
    /// FtpWebRequest has no per-request certificate callback and .NET passes the SslStream, not the
    /// request, to ServicePointManager. The flag is AsyncLocal, so it only relaxes validation for the
    /// request that created a destination with "accept any certificate" enabled.
    /// </summary>
    internal static class FtpTlsPolicy
    {
        private static readonly AsyncLocal<bool> Relaxed = new AsyncLocal<bool>();
        private static readonly object Gate = new object();
        private static bool _installed;

        public static void RelaxCertificateChecks()
        {
            lock (Gate)
            {
                if (!_installed)
                {
                    var previous = ServicePointManager.ServerCertificateValidationCallback;
                    ServicePointManager.ServerCertificateValidationCallback =
                        (sender, certificate, chain, errors) =>
                            Relaxed.Value || (previous != null
                                ? previous(sender, certificate, chain, errors)
                                : errors == SslPolicyErrors.None);
                    _installed = true;
                }
            }

            Relaxed.Value = true;
        }
    }
}
