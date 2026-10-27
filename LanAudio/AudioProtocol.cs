using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AudioPlaybackConnector.Lan;

// APCL v1 is deliberately distinct from VBAN: packets are authenticated and encrypted.
internal sealed class AudioProtocol : IDisposable
{
    internal const int SampleRate = 48000;
    internal const int Channels = 2;
    internal const int BlockAlign = 4;
    internal const int HeaderSize = 40;
    internal const int TagSize = 16;
    internal const int MaxPayload = 1120; // 5.83 ms; total datagram stays under 1200 bytes.
    internal const int MaxDatagram = HeaderSize + MaxPayload + TagSize;
    private readonly AesGcm cipher;

    internal AudioProtocol(string pairingCode)
    {
        if (pairingCode.Trim().Length < 8 || pairingCode.Length > 128)
            throw new ArgumentException("连接码需要 8–128 个字符，两台电脑必须完全一致。");
        byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes("APCL/v1/" + pairingCode.Trim()));
        cipher = new AesGcm(key, TagSize);
        CryptographicOperations.ZeroMemory(key);
    }

    // Shared default so both of the user's own computers connect without pasting a code.
    // Still changeable per session; "生成新连接码" produces a fresh random one.
    internal const string DefaultPairingCode = "APCL-HOME-2026";

    internal static string NewPairingCode() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8));

    internal byte[] Encode(ReadOnlySpan<byte> pcm, ReadOnlySpan<byte> session, uint sequence, long frame)
    {
        if (pcm.Length == 0 || pcm.Length > MaxPayload || pcm.Length % BlockAlign != 0 || session.Length != 12 || frame < 0)
            throw new ArgumentException("Invalid audio packet.");
        byte[] packet = new byte[HeaderSize + pcm.Length + TagSize];
        Span<byte> header = packet.AsSpan(0, HeaderSize);
        "APCL"u8.CopyTo(header);
        header[4] = 1;
        header[5] = Channels;
        header[6] = 16;
        header[7] = HeaderSize;
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], sequence);
        session.CopyTo(header[16..28]);
        BinaryPrimitives.WriteInt64LittleEndian(header[28..], frame);
        BinaryPrimitives.WriteInt32LittleEndian(header[36..], pcm.Length);
        Span<byte> nonce = stackalloc byte[12];
        MakeNonce(session, sequence, nonce);
        cipher.Encrypt(nonce, pcm, packet.AsSpan(HeaderSize, pcm.Length), packet.AsSpan(HeaderSize + pcm.Length, TagSize), header);
        return packet;
    }

    internal AudioPacket? Decode(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < HeaderSize + BlockAlign + TagSize || datagram.Length > MaxDatagram)
            return null;
        ReadOnlySpan<byte> header = datagram[..HeaderSize];
        if (!header[..4].SequenceEqual("APCL"u8) || header[4] != 1 || header[5] != Channels || header[6] != 16 ||
            header[7] != HeaderSize || BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != SampleRate)
            return null;
        int size = BinaryPrimitives.ReadInt32LittleEndian(header[36..]);
        long frame = BinaryPrimitives.ReadInt64LittleEndian(header[28..]);
        if (size <= 0 || size > MaxPayload || size % BlockAlign != 0 || datagram.Length != HeaderSize + size + TagSize ||
            frame < 0 || frame > long.MaxValue - size / BlockAlign)
            return null;
        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        byte[] pcm = new byte[size];
        Span<byte> nonce = stackalloc byte[12];
        MakeNonce(header[16..28], sequence, nonce);
        try
        {
            cipher.Decrypt(nonce, datagram.Slice(HeaderSize, size), datagram.Slice(HeaderSize + size, TagSize), pcm, header);
        }
        catch (CryptographicException) { return null; }
        return new AudioPacket(Convert.ToHexString(header[16..28]), sequence, frame, pcm);
    }

    private static void MakeNonce(ReadOnlySpan<byte> session, uint sequence, Span<byte> nonce)
    {
        session.CopyTo(nonce);
        BinaryPrimitives.WriteUInt32LittleEndian(nonce[8..], BinaryPrimitives.ReadUInt32LittleEndian(nonce[8..]) ^ sequence);
    }

    public void Dispose() => cipher.Dispose();
}

internal sealed record AudioPacket(string Session, uint Sequence, long Frame, byte[] Pcm)
{
    internal int Frames => Pcm.Length / AudioProtocol.BlockAlign;
    internal long End => Frame + Frames;
}
