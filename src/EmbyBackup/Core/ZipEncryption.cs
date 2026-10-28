using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace EmbyBackup.Core
{
    /// <summary>
    /// WinZip AES (AE-2) constants and primitives. A ZIP entry encrypted this way keeps its real
    /// compression method in an extra field and is marked as method 99. The password is not stored in the
    /// archive: a 2-byte password verifier rejects most wrong passwords early, and the truncated HMAC-SHA1
    /// authenticates the ciphertext.
    ///
    /// Layout of an encrypted entry payload (compressed size includes all of it):
    ///   salt (16) | password verifier (2) | AES-256-CTR ciphertext | HMAC-SHA1 (first 10 bytes)
    /// The CTR counter is a 128-bit little-endian integer that starts at 1 for every entry.
    /// </summary>
    public static class ZipEncryption
    {
        public const ushort AesMethod = 99;
        public const ushort ExtraFieldId = 0x9901;
        public const byte AesVersion = 2; // AE-2: CRC is zero, HMAC authenticates the data.
        public const byte AesStrength = 3; // AES-256
        public const int SaltLength = 16;
        public const int PasswordVerifierLength = 2;
        public const int MacLength = 10;
        public const int KeyLength = 32;
        public const int MacKeyLength = 32;
        public const int DerivedKeyLength = KeyLength + MacKeyLength + PasswordVerifierLength;
        public const int Iterations = 1000;
        public const int Overhead = SaltLength + PasswordVerifierLength + MacLength;

        internal static byte[] DeriveKey(string password, byte[] salt)
        {
            if (password == null)
            {
                password = string.Empty;
            }

            // The ZIP AES specification (and 7-Zip/WinZip) derive the key from the UTF-8 password bytes
            // with PBKDF2-HMAC-SHA1 and 1000 iterations.
#pragma warning disable SYSLIB0041 // netstandard2.0 only exposes the SHA-1 constructor, which is what ZIP AES uses.
            using (var derive = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(password), salt, Iterations))
#pragma warning restore SYSLIB0041
            {
                return derive.GetBytes(DerivedKeyLength);
            }
        }

        internal static byte[] TruncatedMac(byte[] macKey, byte[] ciphertext, int offset, int count)
        {
            using (var hmac = new HMACSHA1(macKey))
            {
                var hash = hmac.ComputeHash(ciphertext, offset, count);
                var mac = new byte[MacLength];
                Buffer.BlockCopy(hash, 0, mac, 0, MacLength);
                return mac;
            }
        }

        internal static bool FixedTimeEquals(byte[] left, byte[] right, int length)
        {
            if (left == null || right == null || left.Length < length || right.Length < length)
            {
                return false;
            }

            var diff = 0;
            for (var i = 0; i < length; i++)
            {
                diff |= left[i] ^ right[i];
            }
            return diff == 0;
        }

        internal sealed class AesCtrEngine : IDisposable
        {
            private readonly Aes _aes;
            private readonly ICryptoTransform _encryptor;
            private byte[] _counters = new byte[0];
            private byte[] _keyStream = new byte[0];
            private ulong _low = 1;
            private ulong _high;

            public AesCtrEngine(byte[] key)
            {
                _aes = Aes.Create();
                _aes.KeySize = 256;
                _aes.BlockSize = 128;
                _aes.Mode = CipherMode.ECB;
                _aes.Padding = PaddingMode.None;
                _aes.Key = key;
                _encryptor = _aes.CreateEncryptor();
            }

            /// <summary>XORs the input with the next keystream bytes. The same call decrypts as well.</summary>
            public void Transform(byte[] input, int inputOffset, int count, byte[] output, int outputOffset)
            {
                if (count <= 0)
                {
                    return;
                }

                var blocks = (count + 15) / 16;
                var bytes = blocks * 16;
                if (_counters.Length < bytes)
                {
                    _counters = new byte[bytes];
                    _keyStream = new byte[bytes];
                }

                for (var i = 0; i < blocks; i++)
                {
                    WriteUInt64LittleEndian(_counters, i * 16, _low);
                    WriteUInt64LittleEndian(_counters, i * 16 + 8, _high);
                    _low++;
                    if (_low == 0)
                    {
                        _high++;
                    }
                }

                _encryptor.TransformBlock(_counters, 0, bytes, _keyStream, 0);

                for (var i = 0; i < count; i++)
                {
                    output[outputOffset + i] = (byte)(input[inputOffset + i] ^ _keyStream[i]);
                }
            }

            public void Dispose()
            {
                if (_encryptor != null)
                {
                    _encryptor.Dispose();
                }
                if (_aes != null)
                {
                    _aes.Dispose();
                }
            }
        }

        internal static void WriteUInt64LittleEndian(byte[] buffer, int offset, ulong value)
        {
            for (var i = 0; i < 8; i++)
            {
                buffer[offset + i] = (byte)(value >> (8 * i));
            }
        }

        internal static void WriteUInt32LittleEndian(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        internal static void WriteUInt16LittleEndian(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
        }

        internal static ushort ReadUInt16(byte[] buffer, int offset)
        {
            return (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
        }

        internal static uint ReadUInt32(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24));
        }

        internal static ulong ReadUInt64(byte[] buffer, int offset)
        {
            ulong value = 0;
            for (var i = 7; i >= 0; i--)
            {
                value = (value << 8) | buffer[offset + i];
            }
            return value;
        }

        internal static void ToDosTime(DateTime value, out ushort time, out ushort date)
        {
            var local = value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;
            if (local.Year < 1980)
            {
                local = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);
            }
            else if (local.Year > 2107)
            {
                local = new DateTime(2107, 12, 31, 23, 59, 58, DateTimeKind.Local);
            }

            time = (ushort)((local.Hour << 11) | (local.Minute << 5) | (local.Second / 2));
            date = (ushort)(((local.Year - 1980) << 9) | (local.Month << 5) | local.Day);
        }

        internal static DateTime FromDosTime(ushort time, ushort date)
        {
            var year = 1980 + ((date >> 9) & 0x7F);
            var month = Math.Max(1, (date >> 5) & 0x0F);
            var day = Math.Max(1, date & 0x1F);
            var hour = (time >> 11) & 0x1F;
            var minute = (time >> 5) & 0x3F;
            var second = (time & 0x1F) * 2;
            try
            {
                return new DateTime(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)),
                    Math.Min(hour, 23), Math.Min(minute, 59), Math.Min(second, 59), DateTimeKind.Local)
                    .ToUniversalTime();
            }
            catch
            {
                return DateTime.UtcNow;
            }
        }
    }

    public class ZipException : Exception
    {
        public ZipException(string message)
            : base(message)
        {
        }
    }

    public sealed class ZipPasswordRequiredException : ZipException
    {
        public ZipPasswordRequiredException()
            : base("This backup is protected by a password.")
        {
        }
    }

    public sealed class ZipWrongPasswordException : ZipException
    {
        public ZipWrongPasswordException()
            : base("The backup password is incorrect.")
        {
        }
    }

    public sealed class ZipArchiveEntryResult
    {
        public long PlainBytes { get; set; }
        public string Sha256 { get; set; }
    }

    /// <summary>
    /// Writes a ZIP archive whose entries are all encrypted with WinZip AES-256 (AE-2).
    /// It is deliberately small: entries are compressed into a spool file first so their sizes are known
    /// before the local header is written, which keeps the result compatible with 7-Zip, WinRAR and
    /// WinZip. ZIP64 is used automatically when an entry or the archive crosses 4 GiB.
    /// </summary>
    public sealed class AesZipWriter : IDisposable
    {
        private const uint LocalHeaderSignature = 0x04034b50;
        private const uint CentralHeaderSignature = 0x02014b50;
        private const uint EndOfCentralDirectorySignature = 0x06054b50;
        private const uint Zip64EndOfCentralDirectorySignature = 0x06064b50;
        private const uint Zip64LocatorSignature = 0x07064b50;
        private const ushort Utf8Flag = 0x0800;
        private const ushort EncryptedFlag = 0x0001;
        private const ushort Zip64ExtraId = 0x0001;
        private const uint Max32 = 0xFFFFFFFF;
        private const ushort Max16 = 0xFFFF;

        private readonly FileStream _output;
        private readonly string _password;
        private readonly string _spoolDirectory;
        private readonly List<CentralEntry> _entries = new List<CentralEntry>();
        private readonly byte[] _buffer = new byte[1024 * 1024];
        private readonly byte[] _cipher = new byte[1024 * 1024];
        private long _offset;
        private bool _completed;
        private bool _disposed;

        private sealed class CentralEntry
        {
            public string Name;
            public byte[] NameBytes;
            public ushort Flags;
            public ushort Method;
            public ushort Time;
            public ushort Date;
            public uint Crc;
            public long CompressedSize;
            public long UncompressedSize;
            public long LocalHeaderOffset;
            public bool Zip64;
            public byte ActualMethod;
        }

        public AesZipWriter(string path, string password, string spoolDirectory)
        {
            if (string.IsNullOrEmpty(password))
            {
                throw new ArgumentException("A password is required to create an encrypted backup.", nameof(password));
            }

            _password = password;
            _spoolDirectory = string.IsNullOrWhiteSpace(spoolDirectory)
                ? Path.GetTempPath()
                : spoolDirectory;
            Directory.CreateDirectory(_spoolDirectory);
            _output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024);
        }

        public ZipArchiveEntryResult AddEntry(
            string name,
            DateTime lastWriteUtc,
            Stream input,
            CompressionLevel level,
            CancellationToken cancellationToken)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            var archiveName = PathSafety.ToArchivePath(name);
            var spoolPath = Path.Combine(_spoolDirectory, "zip-" + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                long plainBytes;
                byte[] hash;
                byte actualMethod;

                using (var spool = new FileStream(spoolPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024))
                {
                    actualMethod = level == CompressionLevel.NoCompression ? (byte)0 : (byte)8;
                    Stream compressor = actualMethod == 8
                        ? new DeflateStream(spool, level, true)
                        : (Stream)spool;

                    try
                    {
                        plainBytes = CopyAndHash(input, compressor, _buffer, cancellationToken, out hash);
                    }
                    finally
                    {
                        if (actualMethod == 8)
                        {
                            compressor.Dispose();
                        }
                    }
                }

                var compressedSize = new FileInfo(spoolPath).Length;
                var encryptedSize = compressedSize + ZipEncryption.Overhead;
                WriteEntry(
                    archiveName,
                    lastWriteUtc,
                    spoolPath,
                    compressedSize,
                    encryptedSize,
                    plainBytes,
                    actualMethod,
                    hash,
                    cancellationToken);

                return new ZipArchiveEntryResult
                {
                    PlainBytes = plainBytes,
                    Sha256 = BackupFormat.Sha256(hash),
                };
            }
            finally
            {
                TryDelete(spoolPath);
            }
        }

        public ZipArchiveEntryResult AddBytes(
            string name,
            DateTime lastWriteUtc,
            byte[] data,
            CompressionLevel level,
            CancellationToken cancellationToken)
        {
            using (var input = new MemoryStream(data ?? new byte[0], false))
            {
                return AddEntry(name, lastWriteUtc, input, level, cancellationToken);
            }
        }

        /// <summary>Writes the central directory and the end records. Call it once, after the last entry.</summary>
        public void Complete()
        {
            if (_completed)
            {
                return;
            }

            var centralOffset = _offset;
            foreach (var entry in _entries)
            {
                WriteCentralEntry(entry);
            }

            var centralSize = _offset - centralOffset;
            var needsZip64 = _entries.Count >= Max16 || centralOffset >= Max32 || centralSize >= Max32;

            if (needsZip64)
            {
                var zip64Offset = _offset;
                WriteZip64End(centralOffset, centralSize);
                WriteZip64Locator(zip64Offset);
                WriteEndOfCentralDirectory(Max16, Max32, Max32);
            }
            else
            {
                WriteEndOfCentralDirectory((ushort)_entries.Count, (uint)centralSize, (uint)centralOffset);
            }

            _output.Flush(true);
            _completed = true;
        }

        private void WriteEntry(
            string name,
            DateTime lastWriteUtc,
            string spoolPath,
            long compressedSize,
            long encryptedSize,
            long plainBytes,
            byte actualMethod,
            byte[] hash,
            CancellationToken cancellationToken)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            if (nameBytes.Length > Max16)
            {
                throw new InvalidOperationException("The archive path is too long: " + name);
            }

            var zip64 = encryptedSize >= Max32 || plainBytes >= Max32;
            ushort time;
            ushort date;
            ZipEncryption.ToDosTime(lastWriteUtc, out time, out date);

            var extra = BuildAesExtra(actualMethod, zip64);
            var flags = (ushort)(Utf8Flag | EncryptedFlag);
            var version = (ushort)51; // 5.1: AES encryption.
            var localOffset = _offset;

            var header = new byte[30];
            ZipEncryption.WriteUInt32LittleEndian(header, 0, LocalHeaderSignature);
            ZipEncryption.WriteUInt16LittleEndian(header, 4, version);
            ZipEncryption.WriteUInt16LittleEndian(header, 6, flags);
            ZipEncryption.WriteUInt16LittleEndian(header, 8, ZipEncryption.AesMethod);
            ZipEncryption.WriteUInt16LittleEndian(header, 10, time);
            ZipEncryption.WriteUInt16LittleEndian(header, 12, date);
            ZipEncryption.WriteUInt32LittleEndian(header, 14, 0); // AE-2: CRC is not used.
            ZipEncryption.WriteUInt32LittleEndian(header, 18, zip64 ? Max32 : (uint)encryptedSize);
            ZipEncryption.WriteUInt32LittleEndian(header, 22, zip64 ? Max32 : (uint)plainBytes);
            ZipEncryption.WriteUInt16LittleEndian(header, 26, (ushort)nameBytes.Length);
            ZipEncryption.WriteUInt16LittleEndian(header, 28, (ushort)extra.Length);

            _output.Write(header, 0, header.Length);
            _output.Write(nameBytes, 0, nameBytes.Length);
            _output.Write(extra, 0, extra.Length);
            _offset += header.Length + nameBytes.Length + extra.Length;

            var salt = new byte[ZipEncryption.SaltLength];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(salt);
            }

            var derived = ZipEncryption.DeriveKey(_password, salt);
            var key = new byte[ZipEncryption.KeyLength];
            var macKey = new byte[ZipEncryption.MacKeyLength];
            Buffer.BlockCopy(derived, 0, key, 0, ZipEncryption.KeyLength);
            Buffer.BlockCopy(derived, ZipEncryption.KeyLength, macKey, 0, ZipEncryption.MacKeyLength);

            _output.Write(salt, 0, salt.Length);
            _output.Write(derived, ZipEncryption.KeyLength + ZipEncryption.MacKeyLength,
                ZipEncryption.PasswordVerifierLength);
            _offset += ZipEncryption.SaltLength + ZipEncryption.PasswordVerifierLength;

            using (var hmac = new HMACSHA1(macKey))
            using (var ctr = new ZipEncryption.AesCtrEngine(key))
            using (var spool = new FileStream(spoolPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
                FileOptions.SequentialScan))
            {
                var remaining = compressedSize;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var toRead = (int)Math.Min(_buffer.Length, remaining);
                    var read = spool.Read(_buffer, 0, toRead);
                    if (read <= 0)
                    {
                        throw new InvalidDataException("The compressed entry ended unexpectedly: " + name);
                    }

                    ctr.Transform(_buffer, 0, read, _cipher, 0);
                    hmac.TransformBlock(_cipher, 0, read, _cipher, 0);
                    _output.Write(_cipher, 0, read);
                    _offset += read;
                    remaining -= read;
                }

                hmac.TransformFinalBlock(new byte[0], 0, 0);
                var mac = new byte[ZipEncryption.MacLength];
                Buffer.BlockCopy(hmac.Hash, 0, mac, 0, ZipEncryption.MacLength);
                _output.Write(mac, 0, mac.Length);
                _offset += mac.Length;
            }

            _entries.Add(new CentralEntry
            {
                Name = name,
                NameBytes = nameBytes,
                Flags = flags,
                Method = ZipEncryption.AesMethod,
                Time = time,
                Date = date,
                Crc = 0,
                CompressedSize = encryptedSize,
                UncompressedSize = plainBytes,
                LocalHeaderOffset = localOffset,
                Zip64 = zip64,
                ActualMethod = actualMethod,
            });
        }

        private static byte[] BuildAesExtra(byte actualMethod, bool zip64)
        {
            var zip64Length = zip64 ? 4 + 16 : 0;
            var aesLength = 4 + 7;
            var extra = new byte[zip64Length + aesLength];
            var position = 0;

            if (zip64)
            {
                // Both sizes are present because both 32-bit fields are set to 0xFFFFFFFF.
                ZipEncryption.WriteUInt16LittleEndian(extra, position, Zip64ExtraId);
                ZipEncryption.WriteUInt16LittleEndian(extra, position + 2, 16);
                position += 4 + 16;
            }

            ZipEncryption.WriteUInt16LittleEndian(extra, position, ZipEncryption.ExtraFieldId);
            ZipEncryption.WriteUInt16LittleEndian(extra, position + 2, 7);
            ZipEncryption.WriteUInt16LittleEndian(extra, position + 4, ZipEncryption.AesVersion);
            extra[position + 6] = (byte)'A';
            extra[position + 7] = (byte)'E';
            extra[position + 8] = ZipEncryption.AesStrength;
            ZipEncryption.WriteUInt16LittleEndian(extra, position + 9, actualMethod);
            return extra;
        }

        private void WriteCentralEntry(CentralEntry entry)
        {
            var zip64Sizes = entry.CompressedSize >= Max32 || entry.UncompressedSize >= Max32 || entry.Zip64;
            var zip64Offset = entry.LocalHeaderOffset >= Max32;
            var zip64 = zip64Sizes || zip64Offset;
            var extra = BuildCentralExtra(entry, zip64Sizes, zip64Offset);

            var header = new byte[46];
            ZipEncryption.WriteUInt32LittleEndian(header, 0, CentralHeaderSignature);
            ZipEncryption.WriteUInt16LittleEndian(header, 4, VersionMadeBy());
            ZipEncryption.WriteUInt16LittleEndian(header, 6, (ushort)51);
            ZipEncryption.WriteUInt16LittleEndian(header, 8, entry.Flags);
            ZipEncryption.WriteUInt16LittleEndian(header, 10, entry.Method);
            ZipEncryption.WriteUInt16LittleEndian(header, 12, entry.Time);
            ZipEncryption.WriteUInt16LittleEndian(header, 14, entry.Date);
            ZipEncryption.WriteUInt32LittleEndian(header, 16, entry.Crc);
            ZipEncryption.WriteUInt32LittleEndian(header, 20, zip64Sizes ? Max32 : (uint)entry.CompressedSize);
            ZipEncryption.WriteUInt32LittleEndian(header, 24, zip64Sizes ? Max32 : (uint)entry.UncompressedSize);
            ZipEncryption.WriteUInt16LittleEndian(header, 28, (ushort)entry.NameBytes.Length);
            ZipEncryption.WriteUInt16LittleEndian(header, 30, (ushort)extra.Length);
            ZipEncryption.WriteUInt16LittleEndian(header, 32, 0); // comment
            ZipEncryption.WriteUInt16LittleEndian(header, 34, 0); // disk
            ZipEncryption.WriteUInt16LittleEndian(header, 36, 0); // internal attributes
            ZipEncryption.WriteUInt32LittleEndian(header, 38, ExternalAttributes());
            ZipEncryption.WriteUInt32LittleEndian(header, 42, zip64Offset ? Max32 : (uint)entry.LocalHeaderOffset);

            _output.Write(header, 0, header.Length);
            _output.Write(entry.NameBytes, 0, entry.NameBytes.Length);
            _output.Write(extra, 0, extra.Length);
            _offset += header.Length + entry.NameBytes.Length + extra.Length;
        }

        private static byte[] BuildCentralExtra(CentralEntry entry, bool zip64Sizes, bool zip64Offset)
        {
            var zip64DataLength = (zip64Sizes ? 16 : 0) + (zip64Offset ? 8 : 0);
            var zip64Length = zip64DataLength > 0 ? 4 + zip64DataLength : 0;
            var extra = new byte[zip64Length + 4 + 7];
            var position = 0;

            if (zip64DataLength > 0)
            {
                ZipEncryption.WriteUInt16LittleEndian(extra, position, Zip64ExtraId);
                ZipEncryption.WriteUInt16LittleEndian(extra, position + 2, (ushort)zip64DataLength);
                position += 4;
                if (zip64Sizes)
                {
                    ZipEncryption.WriteUInt64LittleEndian(extra, position, (ulong)entry.UncompressedSize);
                    ZipEncryption.WriteUInt64LittleEndian(extra, position + 8, (ulong)entry.CompressedSize);
                    position += 16;
                }
                if (zip64Offset)
                {
                    ZipEncryption.WriteUInt64LittleEndian(extra, position, (ulong)entry.LocalHeaderOffset);
                    position += 8;
                }
            }

            ZipEncryption.WriteUInt16LittleEndian(extra, position, ZipEncryption.ExtraFieldId);
            ZipEncryption.WriteUInt16LittleEndian(extra, position + 2, 7);
            ZipEncryption.WriteUInt16LittleEndian(extra, position + 4, ZipEncryption.AesVersion);
            extra[position + 6] = (byte)'A';
            extra[position + 7] = (byte)'E';
            extra[position + 8] = ZipEncryption.AesStrength;
            ZipEncryption.WriteUInt16LittleEndian(extra, position + 9, entry.ActualMethod);
            return extra;
        }

        private void WriteZip64End(long centralOffset, long centralSize)
        {
            var record = new byte[56];
            ZipEncryption.WriteUInt32LittleEndian(record, 0, Zip64EndOfCentralDirectorySignature);
            ZipEncryption.WriteUInt64LittleEndian(record, 4, 44);
            ZipEncryption.WriteUInt16LittleEndian(record, 12, VersionMadeBy());
            ZipEncryption.WriteUInt16LittleEndian(record, 14, 45);
            ZipEncryption.WriteUInt32LittleEndian(record, 16, 0);
            ZipEncryption.WriteUInt32LittleEndian(record, 20, 0);
            ZipEncryption.WriteUInt64LittleEndian(record, 24, (ulong)_entries.Count);
            ZipEncryption.WriteUInt64LittleEndian(record, 32, (ulong)_entries.Count);
            ZipEncryption.WriteUInt64LittleEndian(record, 40, (ulong)centralSize);
            ZipEncryption.WriteUInt64LittleEndian(record, 48, (ulong)centralOffset);
            _output.Write(record, 0, record.Length);
            _offset += record.Length;
        }

        private void WriteZip64Locator(long zip64EndOffset)
        {
            var locator = new byte[20];
            ZipEncryption.WriteUInt32LittleEndian(locator, 0, Zip64LocatorSignature);
            ZipEncryption.WriteUInt32LittleEndian(locator, 4, 0);
            ZipEncryption.WriteUInt64LittleEndian(locator, 8, (ulong)zip64EndOffset);
            ZipEncryption.WriteUInt32LittleEndian(locator, 16, 1);
            _output.Write(locator, 0, locator.Length);
            _offset += locator.Length;
        }

        private void WriteEndOfCentralDirectory(ushort entries, uint centralSize, uint centralOffset)
        {
            var record = new byte[22];
            ZipEncryption.WriteUInt32LittleEndian(record, 0, EndOfCentralDirectorySignature);
            ZipEncryption.WriteUInt16LittleEndian(record, 4, 0);
            ZipEncryption.WriteUInt16LittleEndian(record, 6, 0);
            ZipEncryption.WriteUInt16LittleEndian(record, 8, entries);
            ZipEncryption.WriteUInt16LittleEndian(record, 10, entries);
            ZipEncryption.WriteUInt32LittleEndian(record, 12, centralSize);
            ZipEncryption.WriteUInt32LittleEndian(record, 16, centralOffset);
            ZipEncryption.WriteUInt16LittleEndian(record, 20, 0);
            _output.Write(record, 0, record.Length);
            _offset += record.Length;
        }

        private static ushort VersionMadeBy()
        {
            // Unix host (3) with version 3.0. Windows tools ignore the host byte.
            return Environment.OSVersion.Platform == PlatformID.Win32NT ? (ushort)0x0014 : (ushort)0x031E;
        }

        private static uint ExternalAttributes()
        {
            // Regular file, mode 0644, for Unix. Windows ignores this field.
            return Environment.OSVersion.Platform == PlatformID.Win32NT ? 0u : 0x81A40000u;
        }

        private static long CopyAndHash(
            Stream input,
            Stream output,
            byte[] buffer,
            CancellationToken cancellationToken,
            out byte[] hash)
        {
            var total = 0L;
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = input.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    output.Write(buffer, 0, read);
                    sha.AppendData(buffer, 0, read);
                    total += read;
                }

                hash = sha.GetHashAndReset();
            }

            output.Flush();
            return total;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // A stale spool file is harmless; the operating system can clean it later.
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (!_completed)
            {
                try
                {
                    Complete();
                }
                catch
                {
                    // The caller is already handling a failure; do not mask it.
                }
            }

            _output.Dispose();
        }
    }
}
