using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace DynTypeNetwork;

internal static class MessageCrypto
{
    private const byte EncryptedMarker = 0x00;
    private const byte HandshakeMarker = 0x01;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    internal static byte[] CreateTcpEnvelope(byte[] messageBytes, MessageType type, int senderId, int targetId)
    {
        if (type == MessageType.Handshake)
            return PrependMarker(HandshakeMarker, messageBytes);

        byte[] key = GetEncryptionKeyForOutbound(senderId, targetId);
        byte[] ciphertext = EncryptBytes(messageBytes, key);
        return PrependMarker(EncryptedMarker, ciphertext);
    }

    internal static byte[] CreateUdpEnvelope(byte[] messageBytes, MessageType type, int senderId, int targetId)
    {
        if (type == MessageType.Handshake)
            return PrependMarker(HandshakeMarker, messageBytes);

        byte[] key = GetEncryptionKeyForOutbound(senderId, targetId);
        byte[] ciphertext = EncryptBytes(messageBytes, key);
        return PrependMarker(EncryptedMarker, ciphertext);
    }

    internal static byte[] DecodeTcpEnvelope(byte[] data, int? connectionId = null)
    {
        if (data == null || data.Length < 1)
            throw new ArgumentException("TCP envelope is too short.", nameof(data));

        byte marker = data[0];
        byte[] body = data[1..];

        return marker == HandshakeMarker
            ? body
            : DecryptTcpEnvelope(body, connectionId);
    }

    internal static byte[] DecodeUdpEnvelope(byte[] data, int? senderId = null)
    {
        if (data == null || data.Length < 1)
            throw new ArgumentException("UDP envelope is too short.", nameof(data));

        byte marker = data[0];
        byte[] body = data[1..];

        return marker == HandshakeMarker
            ? body
            : DecryptUdpEnvelope(body, senderId);
    }

    private static byte[] DecryptTcpEnvelope(byte[] body, int? connectionId)
    {
        if (connectionId.HasValue)
        {
            byte[]? key = GetEncryptionKeyForInbound(connectionId.Value);
            if (key == null)
                throw new InvalidOperationException($"No TCP encryption key available for connection {connectionId.Value}.");

            return DecryptBytes(body, key);
        }

        if (KeyExchange.ClientSharedSecret != null)
            return DecryptBytes(body, KeyExchange.ClientSharedSecret);

        if (TryDecryptWithAnyServerKey(body, out var plaintext))
            return plaintext;

        throw new CryptographicException("Unable to decrypt TCP message.");
    }

    private static byte[] DecryptUdpEnvelope(byte[] body, int? senderId)
    {
        if (senderId.HasValue)
        {
            byte[]? key = GetEncryptionKeyForInbound(senderId.Value);
            if (key == null)
                throw new InvalidOperationException($"No UDP encryption key available for sender {senderId.Value}.");

            return DecryptBytes(body, key);
        }

        if (KeyExchange.ClientSharedSecret != null)
        {
            try
            {
                return DecryptBytes(body, KeyExchange.ClientSharedSecret);
            }
            catch (CryptographicException) { }
        }

        if (TryDecryptWithAnyServerKey(body, out var plaintext))
            return plaintext;

        throw new CryptographicException("Unable to decrypt UDP message.");
    }

    private static byte[] EncryptBytes(byte[] plaintext, byte[] secret)
    {
        byte[] key = DeriveAesKey(secret);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        byte[] result = new byte[NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, result, NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, result, NonceSize + TagSize, ciphertext.Length);
        return result;
    }

    private static byte[] DecryptBytes(byte[] encrypted, byte[] secret)
    {
        if (encrypted.Length < NonceSize + TagSize)
            throw new CryptographicException("Encrypted message is too short.");

        byte[] key = DeriveAesKey(secret);
        byte[] nonce = new byte[NonceSize];
        byte[] tag = new byte[TagSize];
        byte[] ciphertext = new byte[encrypted.Length - NonceSize - TagSize];

        Buffer.BlockCopy(encrypted, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(encrypted, NonceSize, tag, 0, TagSize);
        Buffer.BlockCopy(encrypted, NonceSize + TagSize, ciphertext, 0, ciphertext.Length);

        byte[] plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    private static byte[] GetEncryptionKeyForOutbound(int senderId, int targetId)
    {
        if (senderId != Server.SERVER_ID)
            return KeyExchange.ClientSharedSecret ?? throw new InvalidOperationException("Client shared secret is not initialized.");
        
        return KeyExchange.GetServerSharedSecret(targetId) ?? throw new InvalidOperationException($"No shared secret found for target {targetId}.");
    }

    private static byte[]? GetEncryptionKeyForInbound(int senderId)
    {
        return senderId == Server.SERVER_ID
            ? KeyExchange.ClientSharedSecret
            : KeyExchange.GetServerSharedSecret(senderId);
    }

    private static bool TryDecryptWithAnyServerKey(byte[] encrypted, out byte[] plaintext)
    {
        foreach (var (_, secret) in KeyExchange.GetAllServerSharedSecrets())
        {
            try
            {
                plaintext = DecryptBytes(encrypted, secret);
                return true;
            }
            catch (CryptographicException)
            {
                continue;
            }
        }

        plaintext = Array.Empty<byte>();
        return false;
    }

    private static byte[] DeriveAesKey(byte[] secret)
    {
        if (secret.Length == 32)
            return secret;

        using var sha256 = SHA256.Create();
        return sha256.ComputeHash(secret);
    }

    private static byte[] PrependMarker(byte marker, byte[] data)
    {
        byte[] container = new byte[1 + data.Length];
        container[0] = marker;
        Buffer.BlockCopy(data, 0, container, 1, data.Length);
        return container;
    }
}
