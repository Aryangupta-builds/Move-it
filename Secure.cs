using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace MoveIt;

// Spec 26: encrypted channel for the TCP transfer (50505). Ephemeral ECDH (P-256) -> HKDF-SHA256 -> AES-256-GCM records, one key per direction.
// The 6-digit pairing code is mixed into the key derivation: a laptop with the wrong code cannot read or write a single record.
// What this protects: anyone sniffing the Wi-Fi/cable sees only ciphertext, and changed or replayed data is rejected.
// What it does NOT do: a 6-digit code is not a strong secret. An active attacker on the same network who relays the connection could guess codes
// offline from one captured handshake. Use MoveIt on a network you trust. Not audited by a third party.
public sealed class SecureStream : Stream
{
    static readonly byte[] Magic = { 0x4D, 0x49, 0x45, 0x31 };   // "MIE1"
    const int MaxPlain = 64 * 1024, Tag = 16;
    readonly Stream inner; readonly AesGcm sendAes, recvAes;
    readonly SemaphoreSlim writeLock = new(1, 1);
    ulong sendCtr, recvCtr; byte[] plain = Array.Empty<byte>(); int pos, len;

    SecureStream(Stream inner, byte[] sendKey, byte[] recvKey) { this.inner = inner; sendAes = new AesGcm(sendKey, Tag); recvAes = new AesGcm(recvKey, Tag); }

    static byte[] Derive(byte[] z, byte[] salt, string label, string code) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, z, 32, salt, System.Text.Encoding.UTF8.GetBytes("MoveIt1|" + label + "|" + code));

    static async Task<byte[]> ReadExact(Stream s, int n, CancellationToken ct) { var b = new byte[n]; await s.ReadExactlyAsync(b, ct); return b; }

    static byte[] Hello(byte[] nonce, byte[] pub, bool withMagic)
    {
        var l = new List<byte>(); if (withMagic) l.AddRange(Magic);
        l.AddRange(nonce); l.Add((byte)(pub.Length >> 8)); l.Add((byte)pub.Length); l.AddRange(pub); return l.ToArray();
    }

    static byte[] Agree(ECDiffieHellman mine, byte[] peerPub)
    {
        using var peer = ECDiffieHellman.Create(); peer.ImportSubjectPublicKeyInfo(peerPub, out _);
        return mine.DeriveRawSecretAgreement(peer.PublicKey);
    }

    // Sender side. Throws IOException/EndOfStreamException when the other side does not speak the secure protocol (older MoveIt).
    public static async Task<Stream> Client(Stream raw, string code, CancellationToken ct)
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var nc = RandomNumberGenerator.GetBytes(16);
        await raw.WriteAsync(Hello(nc, ecdh.PublicKey.ExportSubjectPublicKeyInfo(), true), ct); await raw.FlushAsync(ct);
        var ns = await ReadExact(raw, 16, ct); var pl = await ReadExact(raw, 2, ct); int n = (pl[0] << 8) | pl[1];
        if (n < 60 || n > 200) throw new IOException("Bad secure handshake");
        var z = Agree(ecdh, await ReadExact(raw, n, ct)); var salt = nc.Concat(ns).ToArray();
        return new SecureStream(raw, Derive(z, salt, "c2s", code), Derive(z, salt, "s2c", code));
    }

    // Receiver side. A plain (older) sender is detected from its first 4 bytes and served unencrypted; secure == false then.
    public static async Task<(Stream s, bool secure)> Accept(Stream raw, string code, CancellationToken ct)
    {
        var first = await ReadExact(raw, 4, ct);
        if (!first.AsSpan().SequenceEqual(Magic)) return (new PrefixStream(raw, first), false);
        var nc = await ReadExact(raw, 16, ct); var pl = await ReadExact(raw, 2, ct); int n = (pl[0] << 8) | pl[1];
        if (n < 60 || n > 200) throw new IOException("Bad secure handshake");
        var cpub = await ReadExact(raw, n, ct);
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ns = RandomNumberGenerator.GetBytes(16);
        await raw.WriteAsync(Hello(ns, ecdh.PublicKey.ExportSubjectPublicKeyInfo(), false), ct); await raw.FlushAsync(ct);
        var z = Agree(ecdh, cpub); var salt = nc.Concat(ns).ToArray();
        return (new SecureStream(raw, Derive(z, salt, "s2c", code), Derive(z, salt, "c2s", code)), true);
    }

    static void Nonce(Span<byte> n, ulong ctr) { n.Clear(); BinaryPrimitives.WriteUInt64BigEndian(n[4..], ctr); }

    async Task<bool> Fill(CancellationToken ct)
    {
        var h = new byte[4]; int got = 0;
        while (got < 4) { int r = await inner.ReadAsync(h.AsMemory(got, 4 - got), ct); if (r == 0) { if (got == 0) return false; throw new EndOfStreamException(); } got += r; }
        int n = BinaryPrimitives.ReadInt32BigEndian(h);
        if (n < Tag || n > MaxPlain + Tag) throw new IOException("Bad secure record");
        var ct2 = System.Buffers.ArrayPool<byte>.Shared.Rent(n);   // pooled: no new buffer per 64 KB record
        try
        {
            await inner.ReadExactlyAsync(ct2.AsMemory(0, n), ct);
            if (plain.Length < n - Tag) plain = new byte[MaxPlain];
            var nonce = new byte[12]; Nonce(nonce, recvCtr);
            try { recvAes.Decrypt(nonce, ct2.AsSpan(0, n - Tag), ct2.AsSpan(n - Tag, Tag), plain.AsSpan(0, n - Tag), h); }
            catch (CryptographicException ex) { throw new IOException("Secure channel error: wrong pairing code or data was changed", ex); }
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(ct2); }
        recvCtr++; pos = 0; len = n - Tag; return true;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (pos >= len) { if (!await Fill(ct)) return 0; }
        int k = Math.Min(buffer.Length, len - pos); plain.AsMemory(pos, k).CopyTo(buffer); pos += k; return k;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        await writeLock.WaitAsync(ct);
        try
        {
            for (int off = 0; off < data.Length;)
            {
                int k = Math.Min(MaxPlain, data.Length - off); var frame = System.Buffers.ArrayPool<byte>.Shared.Rent(4 + k + Tag);
                try
                {
                    BinaryPrimitives.WriteInt32BigEndian(frame, k + Tag);
                    var nonce = new byte[12]; Nonce(nonce, sendCtr++);
                    sendAes.Encrypt(nonce, data.Span.Slice(off, k), frame.AsSpan(4, k), frame.AsSpan(4 + k, Tag), frame.AsSpan(0, 4));
                    await inner.WriteAsync(frame.AsMemory(0, 4 + k + Tag), ct);
                }
                finally { System.Buffers.ArrayPool<byte>.Shared.Return(frame); }
                off += k;
            }
        }
        finally { writeLock.Release(); }
    }

    public override int Read(byte[] b, int o, int c) => ReadAsync(b.AsMemory(o, c)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] b, int o, int c) => WriteAsync(b.AsMemory(o, c)).AsTask().GetAwaiter().GetResult();
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();
    protected override void Dispose(bool d) { if (d) { sendAes.Dispose(); recvAes.Dispose(); } base.Dispose(d); }
}

// Plain stream that first hands back bytes already read (used to detect older senders).
public sealed class PrefixStream : Stream
{
    readonly Stream inner; readonly byte[] prefix; int used;
    public PrefixStream(Stream inner, byte[] prefix) { this.inner = inner; this.prefix = prefix; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (used < prefix.Length) { int k = Math.Min(buffer.Length, prefix.Length - used); prefix.AsMemory(used, k).CopyTo(buffer); used += k; return k; }
        return await inner.ReadAsync(buffer, ct);
    }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => inner.WriteAsync(data, ct);
    public override int Read(byte[] b, int o, int c) => ReadAsync(b.AsMemory(o, c)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] b, int o, int c) => inner.Write(b, o, c);
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();
}

// Spec 27: packs many tiny writes (file header, small chunk, checksum message, next header ...) into one 64 KB write = one encrypted record / TCP send.
// The bytes on the wire are IDENTICAL, so the receiver and old peers need no change. While Hold is true, Flush does nothing; the sender sets Hold = false (and flushes)
// before it waits for any answer from the receiver.
public sealed class CoalescingStream : Stream
{
    readonly Stream inner; readonly byte[] buf = new byte[1 << 16]; int n; public bool Hold;
    public CoalescingStream(Stream s) { inner = s; }
    public async Task FlushNow(CancellationToken ct = default) { if (n > 0) { var k = n; n = 0; await inner.WriteAsync(buf.AsMemory(0, k), ct); } await inner.FlushAsync(ct); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> m, CancellationToken ct = default)
    {
        if (!Hold) { if (n > 0) { var k = n; n = 0; await inner.WriteAsync(buf.AsMemory(0, k), ct); } await inner.WriteAsync(m, ct); return; }
        if (m.Length > buf.Length - n)
        {
            if (n > 0) { var k = n; n = 0; await inner.WriteAsync(buf.AsMemory(0, k), ct); }
            if (m.Length >= buf.Length) { await inner.WriteAsync(m, ct); return; }   // big chunk: straight through
        }
        m.CopyTo(buf.AsMemory(n)); n += m.Length;
    }
    public override Task WriteAsync(byte[] b, int o, int c, CancellationToken ct) => WriteAsync(b.AsMemory(o, c), ct).AsTask();
    public override void Write(byte[] b, int o, int c) => WriteAsync(b.AsMemory(o, c)).AsTask().GetAwaiter().GetResult();
    public override Task FlushAsync(CancellationToken ct) => Hold ? Task.CompletedTask : FlushNow(ct);
    public override void Flush() { if (!Hold) FlushNow().GetAwaiter().GetResult(); }
    public override ValueTask<int> ReadAsync(Memory<byte> m, CancellationToken ct = default) => inner.ReadAsync(m, ct);
    public override Task<int> ReadAsync(byte[] b, int o, int c, CancellationToken ct) => inner.ReadAsync(b, o, c, ct);
    public override int Read(byte[] b, int o, int c) => inner.Read(b, o, c);
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException();
    protected override void Dispose(bool d) { if (d) inner.Dispose(); base.Dispose(d); }
}
