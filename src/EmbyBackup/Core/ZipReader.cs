using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace EmbyBackup.Core
{
    public sealed class ZipAuthenticationException : ZipException
    {
        public ZipAuthenticationException()
            : base("The backup password is incorrect or the archive is damaged.")
        {
        }
    }

    public sealed class ZipEntryInfo
    {
        public string Name { get; set; }
        public ushort Flags { get; set; }
        public ushort Method { get; set; }
        public ushort Time { get; set; }
        public ushort Date { get; set; }
        public uint Crc { get; set; }
        public long CompressedSize { get; set; }
        public long UncompressedSize { get; set; }
        public long LocalHeaderOffset { get; set; }
        public ushort ActualMethod { get; set; }
        public byte AesStrength { get; set; }
        public bool IsEncrypted { get; set; }

        public DateTime LastWriteUtc
        {
            get { return ZipEncryption.FromDosTime(Time, Date); }
        }
    }

    /// <summary>
    /// A stream over a single encrypted ZIP entry. Reading gives the decompressed plaintext. Callers must
    /// read to the end (or call <see cref="Complete"/>) so the HMAC is checked before accepting the data.
    /// </summary>
    public sealed class ZipEntryHandle : IDisposable
    {
        private readonly AesCtrDecryptStream _decrypt;
        private readonly Stream _deflate;
        private readonly BoundedFileStream _source;
        private bool _completed;

        internal ZipEntryHandle(Stream stream, AesCtrDecryptStream decrypt, Stream deflate, BoundedFileStream source)
        {
            Stream = stream;
            _decrypt = decrypt;
            _deflate = deflate;
            _source = source;
        }

        public Stream Stream { get; private set; }

        /// <summary>Drains any remaining ciphertext and verifies the authentication code.</summary>
        public void Complete()
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            if (_deflate != null)
            {
                try
                {
                    _deflate.Dispose();
                }
                catch (InvalidDataException)
                {
                    // A wrong password that passed the 2-byte verifier usually fails here. The HMAC check
                    // below produces the clearer message when the ciphertext was read completely.
                }
            }

            if (_decrypt != null)
            {
                _decrypt.Drain();
            }

            if (_source != null)
            {
                _source.Dispose();
            }
        }

        public void Dispose()
        {
            Complete();
        }
    }

    /// <summary>
    /// Reads ZIP central directories and entries, including WinZip AES-256 entries. It does not need the
    /// password to list entries or to tell whether an archive is protected.
    /// </summary>
    public sealed class ZipReader : IDisposable
    {
        private const uint LocalHeaderSignature = 0x04034b50;
        private const uint CentralHeaderSignature = 0x02014b50;
        private const uint EndOfCentralDirectorySignature = 0x06054b50;
        private const uint Zip64EndOfCentralDirectorySignature = 0x06064b50;
        private const uint Zip64LocatorSignature = 0x07064b50;
        private const ushort Zip64ExtraId = 0x0001;
        private const ushort Max16 = 0xFFFF;
        private const uint Max32 = 0xFFFFFFFF;

        // Archives are supplied by destinations outside the process (especially FTP). Keep parsing
        // metadata bounded before allocating it. These limits are far above normal Emby configuration
        // backups, while preventing a crafted EOCD/ZIP64 record from exhausting the server's memory.
        private const long MaximumCentralDirectoryBytes = 128L * 1024 * 1024;
        private const long MaximumEntries = 500000;

        private readonly string _path;
        private readonly List<ZipEntryInfo> _entries = new List<ZipEntryInfo>();

        public ZipReader(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("An archive path is required.", nameof(path));
            }

            _path = path;
            ReadCentralDirectory();
        }

        public IReadOnlyList<ZipEntryInfo> Entries
        {
            get { return _entries; }
        }

        public bool IsEncrypted
        {
            get
            {
                foreach (var entry in _entries)
                {
                    if (entry.IsEncrypted)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        public ZipEntryInfo Find(string name)
        {
            foreach (var entry in _entries)
            {
                if (string.Equals(entry.Name, name, StringComparison.Ordinal))
                {
                    return entry;
                }
            }
            return null;
        }

        /// <summary>Opens an entry and verifies its password and authentication code as it is read.</summary>
        public ZipEntryHandle OpenEntry(ZipEntryInfo entry, string password)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

                var local = ReadLocalHeader(entry);
            if (entry.Method == ZipEncryption.AesMethod)
            {
                if (string.IsNullOrEmpty(password))
                {
                    throw new ZipPasswordRequiredException();
                }

                if (!entry.IsEncrypted || entry.AesStrength != ZipEncryption.AesStrength)
                {
                    throw new InvalidDataException("Unsupported encrypted ZIP entry: " + entry.Name);
                }

                    var payloadLength = entry.CompressedSize - ZipEncryption.Overhead;
                if (payloadLength < 0)
                {
                    throw new InvalidDataException("The encrypted entry is truncated: " + entry.Name);
                }

                var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
                    FileOptions.SequentialScan);
                try
                {
                    file.Seek(local.DataOffset, SeekOrigin.Begin);
                    var salt = new byte[ZipEncryption.SaltLength];
                    ReadExact(file, salt, 0, salt.Length);
                    var verifier = new byte[ZipEncryption.PasswordVerifierLength];
                    ReadExact(file, verifier, 0, verifier.Length);

                    var mac = new byte[ZipEncryption.MacLength];
                    var payloadOffset = CheckedAdd(local.DataOffset, ZipEncryption.SaltLength
                        + ZipEncryption.PasswordVerifierLength, "The encrypted entry offset is invalid.");
                    file.Seek(payloadOffset + payloadLength, SeekOrigin.Begin);
                    ReadExact(file, mac, 0, mac.Length);
                    file.Seek(payloadOffset, SeekOrigin.Begin);

                    var derived = ZipEncryption.DeriveKey(password, salt);
                    var expectedVerifier = new byte[ZipEncryption.PasswordVerifierLength];
                    Buffer.BlockCopy(derived, ZipEncryption.KeyLength + ZipEncryption.MacKeyLength,
                        expectedVerifier, 0, expectedVerifier.Length);
                    if (!ZipEncryption.FixedTimeEquals(verifier, expectedVerifier, expectedVerifier.Length))
                    {
                        file.Dispose();
                        throw new ZipWrongPasswordException();
                    }

                    var key = new byte[ZipEncryption.KeyLength];
                    var macKey = new byte[ZipEncryption.MacKeyLength];
                    Buffer.BlockCopy(derived, 0, key, 0, key.Length);
                    Buffer.BlockCopy(derived, ZipEncryption.KeyLength, macKey, 0, macKey.Length);

                    var source = new BoundedFileStream(file, payloadLength);
                    var decrypt = new AesCtrDecryptStream(source, payloadLength, mac, macKey, key);
                    Stream stream = decrypt;
                    if (entry.ActualMethod == 8)
                    {
                        stream = new DeflateStream(decrypt, CompressionMode.Decompress, true);
                    }
                    else if (entry.ActualMethod != 0)
                    {
                        throw new InvalidDataException(
                            "Unsupported compression method in the encrypted entry: " + entry.ActualMethod);
                    }

                    return new ZipEntryHandle(stream, decrypt, entry.ActualMethod == 8 ? stream : null, source);
                }
                catch
                {
                    file.Dispose();
                    throw;
                }
            }

            var plainFile = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
                FileOptions.SequentialScan);
            try
            {
                plainFile.Seek(local.DataOffset, SeekOrigin.Begin);
                var source = new BoundedFileStream(plainFile, entry.CompressedSize);
                Stream stream = source;
                if (entry.Method == 8)
                {
                    stream = new DeflateStream(source, CompressionMode.Decompress, true);
                }
                else if (entry.Method != 0)
                {
                    throw new InvalidDataException("Unsupported compression method: " + entry.Method);
                }

                return new ZipEntryHandle(stream, null, entry.Method == 8 ? stream : null, source);
            }
            catch
            {
                plainFile.Dispose();
                throw;
            }
        }

        public byte[] ReadAllBytes(ZipEntryInfo entry, string password, long maximumBytes)
        {
            if (entry.UncompressedSize > maximumBytes)
            {
                throw new InvalidDataException("The archive entry is too large to read into memory: " + entry.Name);
            }

            using (var handle = OpenEntry(entry, password))
            using (var output = new MemoryStream((int)Math.Min(entry.UncompressedSize, 1024 * 1024)))
            {
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var read = handle.Stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }
                    output.Write(buffer, 0, read);
                }
                handle.Complete();
                return output.ToArray();
            }
        }

        public static bool TryInspect(string path, out bool encrypted, out string error)
        {
            encrypted = false;
            error = null;
            try
            {
                using (var reader = new ZipReader(path))
                {
                    encrypted = reader.IsEncrypted;
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private LocalHeader ReadLocalHeader(ZipEntryInfo entry)
        {
            using (var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024))
            {
                if (entry.LocalHeaderOffset < 0 || entry.LocalHeaderOffset > file.Length - 30)
                {
                    throw new InvalidDataException("The local header is outside the archive: " + entry.Name);
                }

                file.Seek(entry.LocalHeaderOffset, SeekOrigin.Begin);
                var header = new byte[30];
                ReadExact(file, header, 0, header.Length);
                if (ZipEncryption.ReadUInt32(header, 0) != LocalHeaderSignature)
                {
                    throw new InvalidDataException("Invalid local header for " + entry.Name);
                }

                var nameLength = ZipEncryption.ReadUInt16(header, 26);
                var extraLength = ZipEncryption.ReadUInt16(header, 28);
                var dataOffset = CheckedAdd(entry.LocalHeaderOffset, 30L + nameLength + extraLength,
                    "The local entry offset is invalid.");
                if (dataOffset > file.Length || entry.CompressedSize < 0
                    || entry.CompressedSize > file.Length - dataOffset)
                {
                    throw new InvalidDataException("The entry data is outside the archive: " + entry.Name);
                }

                return new LocalHeader
                {
                    DataOffset = dataOffset,
                };
            }
        }

        private void ReadCentralDirectory()
        {
            using (var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024))
            {
                if (file.Length < 22)
                {
                    throw new InvalidDataException("The file is not a ZIP archive.");
                }

                var tailLength = (int)Math.Min(file.Length, 65557);
                var tail = new byte[tailLength];
                file.Seek(file.Length - tailLength, SeekOrigin.Begin);
                ReadExact(file, tail, 0, tailLength);

                var eocdIndex = -1;
                for (var i = tailLength - 22; i >= 0; i--)
                {
                    if (ZipEncryption.ReadUInt32(tail, i) == EndOfCentralDirectorySignature)
                    {
                        eocdIndex = i;
                        break;
                    }
                }

                if (eocdIndex < 0)
                {
                    throw new InvalidDataException("The ZIP end-of-central-directory record is missing.");
                }

                var eocdOffset = file.Length - tailLength + eocdIndex;
                long entriesTotal = ZipEncryption.ReadUInt16(tail, eocdIndex + 10);
                long centralSize = ZipEncryption.ReadUInt32(tail, eocdIndex + 12);
                long centralOffset = ZipEncryption.ReadUInt32(tail, eocdIndex + 16);

                if (entriesTotal == Max16 || centralSize == Max32 || centralOffset == Max32)
                {
                    ReadZip64End(file, eocdOffset, ref entriesTotal, ref centralSize, ref centralOffset);
                }

                if (entriesTotal < 0 || entriesTotal > MaximumEntries)
                {
                    throw new InvalidDataException("The ZIP contains too many entries.");
                }

                if (centralOffset < 0 || centralSize < 0 || centralSize > MaximumCentralDirectoryBytes
                    || centralOffset > file.Length || centralSize > file.Length - centralOffset)
                {
                    throw new InvalidDataException("The ZIP central directory is outside the file.");
                }

                if (centralSize > int.MaxValue)
                {
                    throw new InvalidDataException("The ZIP central directory is too large.");
                }
                var central = new byte[(int)centralSize];
                file.Seek(centralOffset, SeekOrigin.Begin);
                ReadExact(file, central, 0, central.Length);

                var position = 0;
                for (var index = 0; index < entriesTotal; index++)
                {
                    if (position + 46 > central.Length
                        || ZipEncryption.ReadUInt32(central, position) != CentralHeaderSignature)
                    {
                        throw new InvalidDataException("The ZIP central directory is damaged.");
                    }

                    var entry = ParseCentralEntry(central, position, out var consumed);
                    _entries.Add(entry);
                    position += consumed;
                }
            }
        }

        private static void ReadZip64End(
            FileStream file,
            long eocdOffset,
            ref long entriesTotal,
            ref long centralSize,
            ref long centralOffset)
        {
            if (eocdOffset < 20)
            {
                throw new InvalidDataException("The ZIP64 locator is missing.");
            }

            file.Seek(eocdOffset - 20, SeekOrigin.Begin);
            var locator = new byte[20];
            ReadExact(file, locator, 0, locator.Length);
            if (ZipEncryption.ReadUInt32(locator, 0) != Zip64LocatorSignature)
            {
                throw new InvalidDataException("The ZIP64 locator is missing.");
            }

            var rawZip64Offset = ZipEncryption.ReadUInt64(locator, 8);
            if (rawZip64Offset > long.MaxValue)
            {
                throw new InvalidDataException("The ZIP64 end record offset is invalid.");
            }
            var zip64Offset = (long)rawZip64Offset;
            if (zip64Offset < 0 || zip64Offset > file.Length - 56)
            {
                throw new InvalidDataException("The ZIP64 end record is outside the file.");
            }

            file.Seek(zip64Offset, SeekOrigin.Begin);
            var record = new byte[56];
            ReadExact(file, record, 0, record.Length);
            if (ZipEncryption.ReadUInt32(record, 0) != Zip64EndOfCentralDirectorySignature)
            {
                throw new InvalidDataException("The ZIP64 end record is damaged.");
            }

            entriesTotal = ReadZip64Int64(record, 32, "The ZIP64 entry count is invalid.");
            centralSize = ReadZip64Int64(record, 40, "The ZIP64 directory size is invalid.");
            centralOffset = ReadZip64Int64(record, 48, "The ZIP64 directory offset is invalid.");
        }

        private static ZipEntryInfo ParseCentralEntry(byte[] central, int position, out int consumed)
        {
            var flags = ZipEncryption.ReadUInt16(central, position + 8);
            var method = ZipEncryption.ReadUInt16(central, position + 10);
            var time = ZipEncryption.ReadUInt16(central, position + 12);
            var date = ZipEncryption.ReadUInt16(central, position + 14);
            var crc = ZipEncryption.ReadUInt32(central, position + 16);
            var compressed = ZipEncryption.ReadUInt32(central, position + 20);
            var uncompressed = ZipEncryption.ReadUInt32(central, position + 24);
            var nameLength = ZipEncryption.ReadUInt16(central, position + 28);
            var extraLength = ZipEncryption.ReadUInt16(central, position + 30);
            var commentLength = ZipEncryption.ReadUInt16(central, position + 32);
            var offset = ZipEncryption.ReadUInt32(central, position + 42);

            var nameStart = position + 46;
            var extraStart = nameStart + nameLength;
            var commentStart = extraStart + extraLength;
            consumed = 46 + nameLength + extraLength + commentLength;
            if (commentStart + commentLength > central.Length)
            {
                throw new InvalidDataException("The ZIP central directory is damaged.");
            }

            var name = Encoding.UTF8.GetString(central, nameStart, nameLength);
            var extra = new byte[extraLength];
            Buffer.BlockCopy(central, extraStart, extra, 0, extraLength);

            long compressedSize = compressed;
            long uncompressedSize = uncompressed;
            long localOffset = offset;
            ushort actualMethod = method;
            byte aesStrength = 0;

            var index = 0;
            while (index + 4 <= extra.Length)
            {
                var id = ZipEncryption.ReadUInt16(extra, index);
                var size = ZipEncryption.ReadUInt16(extra, index + 2);
                var dataStart = index + 4;
                if (dataStart + size > extra.Length)
                {
                    break;
                }

                if (id == Zip64ExtraId)
                {
                    var data = dataStart;
                    if (uncompressed == Max32 && data + 8 <= dataStart + size)
                    {
                        uncompressedSize = (long)ZipEncryption.ReadUInt64(extra, data);
                        data += 8;
                    }
                    if (compressed == Max32 && data + 8 <= dataStart + size)
                    {
                        compressedSize = (long)ZipEncryption.ReadUInt64(extra, data);
                        data += 8;
                    }
                    if (offset == Max32 && data + 8 <= dataStart + size)
                    {
                        localOffset = (long)ZipEncryption.ReadUInt64(extra, data);
                    }
                }
                else if (id == ZipEncryption.ExtraFieldId && size >= 7)
                {
                    aesStrength = extra[dataStart + 4];
                    actualMethod = ZipEncryption.ReadUInt16(extra, dataStart + 5);
                }

                index += 4 + size;
            }

            return new ZipEntryInfo
            {
                Name = name,
                Flags = flags,
                Method = method,
                Time = time,
                Date = date,
                Crc = crc,
                CompressedSize = compressedSize,
                UncompressedSize = uncompressedSize,
                LocalHeaderOffset = localOffset,
                ActualMethod = actualMethod,
                AesStrength = aesStrength,
                IsEncrypted = method == ZipEncryption.AesMethod,
            };
        }

        private static void ReadExact(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                var read = stream.Read(buffer, offset, count);
                if (read <= 0)
                {
                    throw new InvalidDataException("The archive ended unexpectedly.");
                }
                offset += read;
                count -= read;
            }
        }

        private static long ReadZip64Int64(byte[] buffer, int offset, string error)
        {
            var value = ZipEncryption.ReadUInt64(buffer, offset);
            if (value > long.MaxValue)
            {
                throw new InvalidDataException(error);
            }
            return (long)value;
        }

        private static long CheckedAdd(long left, long right, string error)
        {
            if (left < 0 || right < 0 || left > long.MaxValue - right)
            {
                throw new InvalidDataException(error);
            }
            return left + right;
        }

        private sealed class LocalHeader
        {
            public long DataOffset { get; set; }
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Reads a fixed number of bytes from a file and closes it when disposed.</summary>
    internal sealed class BoundedFileStream : Stream
    {
        private readonly FileStream _file;
        private long _remaining;

        public BoundedFileStream(FileStream file, long length)
        {
            _file = file;
            _remaining = length;
        }

        public override bool CanRead
        {
            get { return true; }
        }

        public override bool CanSeek
        {
            get { return false; }
        }

        public override bool CanWrite
        {
            get { return false; }
        }

        public override long Length
        {
            get { throw new NotSupportedException(); }
        }

        public override long Position
        {
            get { return _file.Position; }
            set { throw new NotSupportedException(); }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            var toRead = (int)Math.Min(count, _remaining);
            var read = _file.Read(buffer, offset, toRead);
            if (read <= 0)
            {
                throw new InvalidDataException("The archive ended unexpectedly.");
            }
            _remaining -= read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _file.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Decrypts AES-256-CTR ciphertext and authenticates it with the truncated HMAC-SHA1 that follows it.
    /// </summary>
    internal sealed class AesCtrDecryptStream : Stream
    {
        private readonly BoundedFileStream _source;
        private readonly long _length;
        private readonly byte[] _expectedMac;
        private readonly HMACSHA1 _hmac;
        private readonly ZipEncryption.AesCtrEngine _ctr;
        private readonly byte[] _cipher = new byte[64 * 1024];
        private readonly byte[] _plain = new byte[64 * 1024];
        private long _remaining;
        private int _plainPosition;
        private int _plainLength;
        private bool _finished;
        private bool _disposed;

        public AesCtrDecryptStream(
            BoundedFileStream source,
            long length,
            byte[] expectedMac,
            byte[] macKey,
            byte[] key)
        {
            _source = source;
            _length = length;
            _remaining = length;
            _expectedMac = expectedMac;
            _hmac = new HMACSHA1(macKey);
            _ctr = new ZipEncryption.AesCtrEngine(key);
        }

        public override bool CanRead
        {
            get { return true; }
        }

        public override bool CanSeek
        {
            get { return false; }
        }

        public override bool CanWrite
        {
            get { return false; }
        }

        public override long Length
        {
            get { return _length; }
        }

        public override long Position
        {
            get { return _length - _remaining - (_plainLength - _plainPosition); }
            set { throw new NotSupportedException(); }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AesCtrDecryptStream));
            }

            if (count <= 0)
            {
                return 0;
            }

            if (_plainPosition >= _plainLength)
            {
                if (!Fill())
                {
                    return 0;
                }
            }

            var available = _plainLength - _plainPosition;
            var toCopy = Math.Min(count, available);
            Buffer.BlockCopy(_plain, _plainPosition, buffer, offset, toCopy);
            _plainPosition += toCopy;
            return toCopy;
        }

        private bool Fill()
        {
            if (_remaining <= 0)
            {
                Finish();
                return false;
            }

            var toRead = (int)Math.Min(_cipher.Length, _remaining);
            var read = 0;
            while (read < toRead)
            {
                var chunk = _source.Read(_cipher, read, toRead - read);
                if (chunk <= 0)
                {
                    throw new InvalidDataException("The encrypted entry ended unexpectedly.");
                }
                read += chunk;
            }

            _remaining -= read;
            _hmac.TransformBlock(_cipher, 0, read, _cipher, 0);
            _ctr.Transform(_cipher, 0, read, _plain, 0);
            _plainPosition = 0;
            _plainLength = read;

            if (_remaining <= 0)
            {
                Finish();
            }

            return true;
        }

        /// <summary>Reads and authenticates any data the caller did not consume.</summary>
        public void Drain()
        {
            var buffer = new byte[64 * 1024];
            while (Read(buffer, 0, buffer.Length) > 0)
            {
            }
        }

        private void Finish()
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            _hmac.TransformFinalBlock(new byte[0], 0, 0);
            var actual = new byte[ZipEncryption.MacLength];
            Buffer.BlockCopy(_hmac.Hash, 0, actual, 0, actual.Length);
            if (!ZipEncryption.FixedTimeEquals(actual, _expectedMac, actual.Length))
            {
                throw new ZipAuthenticationException();
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                try
                {
                    Drain();
                }
                catch (ZipAuthenticationException)
                {
                    throw;
                }
                catch
                {
                    // The caller may already be unwinding after a decompression error.
                }
                finally
                {
                    _disposed = true;
                    _hmac.Dispose();
                    _ctr.Dispose();
                    _source.Dispose();
                }
            }
            else
            {
                _disposed = true;
            }

            base.Dispose(disposing);
        }
    }
}
