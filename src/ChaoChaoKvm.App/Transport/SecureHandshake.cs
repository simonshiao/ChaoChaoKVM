using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace ChaoChaoKvm.Transport;

public static class SecureHandshake
{
    private const int NonceSize = 24;
    private static readonly byte[] HkdfInfo = Encoding.ASCII.GetBytes("LanKVM-v1");

    public static async Task<SecureChannel> RunClientAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        byte[] clientNonce = RandomNumberGenerator.GetBytes(NonceSize);
        X25519PrivateKeyParameters privateKey = new(new SecureRandom());
        byte[] publicKey = privateKey.GeneratePublicKey().GetEncoded();

        await PacketIo.WritePlainJsonAsync(stream, new
        {
            protocol = 1,
            nonce = Convert.ToBase64String(clientNonce),
            @public = Convert.ToBase64String(publicKey)
        }, cancellationToken);

        using JsonDocument challenge = await PacketIo.ReadPlainJsonAsync(stream, cancellationToken);
        byte[] serverNonce = ReadBase64(challenge.RootElement, "nonce", NonceSize);
        byte[] serverPublic = ReadBase64(challenge.RootElement, "public", 32);

        byte[] key = DeriveKey(privateKey, serverPublic, clientNonce, serverNonce);
        try
        {
            return new SecureChannel(stream, key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static async Task<SecureChannel> RunServerAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        using JsonDocument hello = await PacketIo.ReadPlainJsonAsync(stream, cancellationToken);
        int protocol = hello.RootElement.TryGetProperty("protocol", out JsonElement protocolNode)
            ? protocolNode.GetInt32()
            : 0;
        if (protocol != 1)
            throw new InvalidDataException("Unsupported LanKVM protocol.");

        byte[] clientNonce = ReadBase64(hello.RootElement, "nonce", NonceSize);
        byte[] clientPublic = ReadBase64(hello.RootElement, "public", 32);
        byte[] serverNonce = RandomNumberGenerator.GetBytes(NonceSize);
        X25519PrivateKeyParameters privateKey = new(new SecureRandom());
        byte[] publicKey = privateKey.GeneratePublicKey().GetEncoded();

        await PacketIo.WritePlainJsonAsync(stream, new
        {
            nonce = Convert.ToBase64String(serverNonce),
            @public = Convert.ToBase64String(publicKey)
        }, cancellationToken);

        byte[] key = DeriveKey(privateKey, clientPublic, clientNonce, serverNonce);
        try
        {
            return new SecureChannel(stream, key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] DeriveKey(
        X25519PrivateKeyParameters privateKey,
        byte[] peerPublic,
        byte[] clientNonce,
        byte[] serverNonce)
    {
        X25519PublicKeyParameters peer = new(peerPublic, 0);
        byte[] shared = new byte[32];
        privateKey.GenerateSecret(peer, shared, 0);

        byte[] salt = new byte[clientNonce.Length + serverNonce.Length];
        Buffer.BlockCopy(clientNonce, 0, salt, 0, clientNonce.Length);
        Buffer.BlockCopy(serverNonce, 0, salt, clientNonce.Length, serverNonce.Length);

        try
        {
            return HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                shared,
                32,
                salt,
                HkdfInfo);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
        }
    }

    private static byte[] ReadBase64(JsonElement root, string name, int expectedLength)
    {
        if (!root.TryGetProperty(name, out JsonElement node))
            throw new InvalidDataException($"Handshake field '{name}' is missing.");

        string raw = node.GetString() ?? "";
        byte[] value;
        try
        {
            value = Convert.FromBase64String(raw);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException($"Handshake field '{name}' is not valid base64.", ex);
        }

        if (value.Length != expectedLength)
            throw new InvalidDataException($"Handshake field '{name}' has invalid length.");
        return value;
    }
}
