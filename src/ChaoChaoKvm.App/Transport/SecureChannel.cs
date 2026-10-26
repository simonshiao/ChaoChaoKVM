using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using ChaoChaoKvm.Protocol;

namespace ChaoChaoKvm.Transport;

public sealed class SecureChannel : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly AesGcm _sendAes;
    private readonly AesGcm _recvAes;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ulong _sendCounter;
    private ulong _recvCounter;

    public SecureChannel(Stream stream, ReadOnlySpan<byte> key)
    {
        if (key.Length != 32)
            throw new ArgumentException("LanKVM requires a 256-bit AES key.", nameof(key));

        _stream = stream;
        byte[] ownedKey = key.ToArray();
        _sendAes = new AesGcm(ownedKey, 16);
        _recvAes = new AesGcm(ownedKey, 16);
        CryptographicOperations.ZeroMemory(ownedKey);
    }

    public async Task SendAsync(
        byte kind,
        ReadOnlyMemory<byte> payload = default,
        CancellationToken cancellationToken = default)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            byte[] nonce = BuildNonce(_sendCounter++);
            byte[] plain = new byte[1 + payload.Length];
            plain[0] = kind;
            payload.CopyTo(plain.AsMemory(1));

            byte[] cipher = new byte[plain.Length];
            byte[] tag = new byte[16];
            _sendAes.Encrypt(nonce, plain, cipher, tag, associatedData: null);

            int encryptedSize = cipher.Length + tag.Length;
            if (encryptedSize > LanKvmProtocol.MaxPacket)
                throw new InvalidDataException("Encrypted packet exceeds protocol limit.");

            byte[] header = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)encryptedSize));
            await _stream.WriteAsync(header, cancellationToken);
            await _stream.WriteAsync(cipher, cancellationToken);
            await _stream.WriteAsync(tag, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<(byte Kind, byte[] Payload)> ReceiveAsync(
        CancellationToken cancellationToken = default)
    {
        byte[] header = await PacketIo.ReadExactlyAsync(_stream, 4, cancellationToken);
        uint size = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (size < 17 || size > LanKvmProtocol.MaxPacket)
            throw new InvalidDataException("Invalid encrypted packet size.");

        byte[] encrypted = await PacketIo.ReadExactlyAsync(_stream, checked((int)size), cancellationToken);
        int cipherSize = encrypted.Length - 16;
        byte[] cipher = encrypted.AsSpan(0, cipherSize).ToArray();
        byte[] tag = encrypted.AsSpan(cipherSize, 16).ToArray();
        byte[] plain = new byte[cipherSize];
        byte[] nonce = BuildNonce(_recvCounter++);

        _recvAes.Decrypt(nonce, cipher, tag, plain, associatedData: null);
        if (plain.Length == 0)
            throw new InvalidDataException("Encrypted packet has no kind byte.");

        byte kind = plain[0];
        byte[] payload = plain.AsSpan(1).ToArray();
        CryptographicOperations.ZeroMemory(plain);
        return (kind, payload);
    }

    private static byte[] BuildNonce(ulong counter)
    {
        byte[] nonce = new byte[12];
        nonce[0] = (byte)'L';
        nonce[1] = (byte)'K';
        nonce[2] = (byte)'V';
        nonce[3] = (byte)'1';
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), counter);
        return nonce;
    }

    public async ValueTask DisposeAsync()
    {
        _sendAes.Dispose();
        _recvAes.Dispose();
        _sendLock.Dispose();
        await _stream.DisposeAsync();
    }
}
