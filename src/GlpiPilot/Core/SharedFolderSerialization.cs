using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace GlpiPilot.Core;

public sealed partial class SharedFolderWorkspace
{
    internal static byte[] SerializeBounded<T>(T value, long maximum = MaximumEnvelopeBytes)
    {
        using var stream = new BoundedBuffer(maximum);
        JsonSerializer.Serialize(stream, value, Json);
        return stream.ToArray();
    }
    internal static string DigestBounded<T>(T value)
    {
        using var stream = new DigestStream(MaximumEnvelopeBytes);
        JsonSerializer.Serialize(stream, value, Json);
        return stream.Finish();
    }
    private sealed class BoundedBuffer(long maximum) : MemoryStream
    {
        private void Check(long count) { if (Position + count > maximum) throw new InvalidDataException("Le document partagé dépasse la taille de transaction autorisée."); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
        public override void SetLength(long value) { if (value > maximum) throw new InvalidDataException("Transaction partagée trop volumineuse."); base.SetLength(value); }
    }
    private sealed class DigestStream(long maximum) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _length;
        public string Finish() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_length + buffer.Length > maximum) throw new InvalidDataException("Le reçu ou la transaction partagée dépasse 64 Mio.");
            _hash.AppendData(buffer); _length += buffer.Length;
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
    }
}
