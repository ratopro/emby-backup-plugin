using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace EmbyBackup.Core
{
    /// <summary>
    /// Authenticates to an SMB share on Windows. On Linux, Docker and NAS the share has to be mounted by the
    /// administrator, so the configured path is used as-is and these credentials are ignored (with a warning).
    /// </summary>
    internal static class NetworkShareConnector
    {
        private const int ResourceTypeDisk = 1;
        private const int ErrorAlreadyAssigned = 85;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NetResource
        {
            public int Scope;
            public int Type;
            public int DisplayType;
            public int Usage;
            public string LocalName;
            public string RemoteName;
            public string Comment;
            public string Provider;
        }

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetAddConnection2(ref NetResource netResource, string password, string username, int flags);

        public static void Connect(string path, string username, string password, string domain, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                return;
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                log("Network credentials are ignored: on this platform the share must be mounted by the operating system. "
                    + "The configured path is used as a local folder.");
                return;
            }

            var share = ShareRoot(path);
            if (share == null)
            {
                // A drive letter or a local path: the Windows session already governs access.
                return;
            }

            var resource = new NetResource
            {
                Type = ResourceTypeDisk,
                RemoteName = share,
                LocalName = null,
            };

            var user = string.IsNullOrWhiteSpace(domain) ? username : domain + "\\" + username;
            var result = WNetAddConnection2(ref resource, password ?? string.Empty, user, 0);
            if (result == 0 || result == ErrorAlreadyAssigned)
            {
                return;
            }

            var detail = new Win32Exception(result).Message;
            string hint;
            switch (result)
            {
                case 1326:
                    hint = "The server rejected the user name or password.";
                    break;
                case 1219:
                    hint = "Windows already has an open session to this server with other credentials. "
                        + "Disconnect it (net use \\\\server /delete) and try again, or use the same account.";
                    break;
                case 53:
                case 67:
                    hint = "The share was not found. Check the server name and the share name.";
                    break;
                case 5:
                    hint = "Access denied. The account needs write permission on the share and the folder.";
                    break;
                default:
                    hint = "Windows could not connect to the share.";
                    break;
            }

            throw new InvalidOperationException(
                "Could not connect to the network share '" + share + "' as '" + user + "': " + detail + " " + hint,
                new Win32Exception(result));
        }

        /// <summary>Returns "\\\\server\\share" for a UNC path, or null when the path is not UNC.</summary>
        internal static string ShareRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var normalized = path.Trim().Replace('/', '\\');
            if (!normalized.StartsWith("\\\\", StringComparison.Ordinal))
            {
                return null;
            }

            var parts = normalized.Substring(2).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                return null;
            }

            return "\\\\" + parts[0] + "\\" + parts[1];
        }
    }
}
