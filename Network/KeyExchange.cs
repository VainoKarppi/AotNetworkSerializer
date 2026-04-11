using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace DynTypeNetwork;

public static class KeyExchange
{
    private static ECDiffieHellman? _clientEcdh;
    private static readonly object _serverLock = new();
    private static readonly Dictionary<int, ECDiffieHellman> _serverEcdh = [];
    private static readonly Dictionary<int, byte[]> _serverSharedSecrets = [];
    private static readonly Dictionary<int, string> _serverPublicKeys = [];
    private static readonly Dictionary<int, string> _clientPublicKeys = [];

    public static byte[]? ClientPublicKey { get; private set; }
    public static byte[]? ClientSharedSecret { get; private set; }

    public static void InitializeClientKeyExchange()
    {
        _clientEcdh?.Dispose();
        _clientEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        ClientPublicKey = _clientEcdh.PublicKey.ExportSubjectPublicKeyInfo();
        ClientSharedSecret = null;
    }

    public static void ComputeClientSharedSecret(string serverPublicKeyBase64)
    {
        if (_clientEcdh == null)
            throw new InvalidOperationException("Client key exchange is not initialized.");

        if (string.IsNullOrEmpty(serverPublicKeyBase64))
            throw new ArgumentException("Server public key is missing.", nameof(serverPublicKeyBase64));

        byte[] serverPublicKeyBytes = Convert.FromBase64String(serverPublicKeyBase64);
        using var importedServerKey = ECDiffieHellman.Create();
        importedServerKey.ImportSubjectPublicKeyInfo(serverPublicKeyBytes, out _);
        ClientSharedSecret = _clientEcdh.DeriveKeyFromHash(importedServerKey.PublicKey, HashAlgorithmName.SHA256, null, null);
    }

    public static void InitializeServerKeyExchange(int clientId, string clientPublicKeyBase64)
    {
        if (string.IsNullOrEmpty(clientPublicKeyBase64))
            throw new ArgumentException("Client public key is missing.", nameof(clientPublicKeyBase64));

        byte[] clientPublicKeyBytes = Convert.FromBase64String(clientPublicKeyBase64);

        lock (_serverLock)
        {
            if (_serverEcdh.TryGetValue(clientId, out var existingEcdh))
            {
                existingEcdh.Dispose();
                _serverEcdh.Remove(clientId);
                _serverSharedSecrets.Remove(clientId);
                _serverPublicKeys.Remove(clientId);
                _clientPublicKeys.Remove(clientId);
            }

            var serverEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            _serverEcdh[clientId] = serverEcdh;

            using var importedClientKey = ECDiffieHellman.Create();
            importedClientKey.ImportSubjectPublicKeyInfo(clientPublicKeyBytes, out _);
            _serverSharedSecrets[clientId] = serverEcdh.DeriveKeyFromHash(importedClientKey.PublicKey, HashAlgorithmName.SHA256, null, null);
            _serverPublicKeys[clientId] = Convert.ToBase64String(serverEcdh.PublicKey.ExportSubjectPublicKeyInfo());
            _clientPublicKeys[clientId] = clientPublicKeyBase64;
        }
    }

    public static string? GetClientPublicKey(int clientId)
    {
        lock (_serverLock)
        {
            return _clientPublicKeys.TryGetValue(clientId, out var key) ? key : null;
        }
    }

    public static byte[]? GetClientSharedSecret(int clientId)
    {
        lock (_serverLock)
        {
            return _clientPublicKeys.TryGetValue(clientId, out var key) ? Convert.FromBase64String(key) : null;
        }
    }
    public static string? GetServerPublicKey(int clientId)
    {
        lock (_serverLock)
        {
            return _serverPublicKeys.TryGetValue(clientId, out var key) ? key : null;
        }
    }

    public static byte[]? GetServerSharedSecret(int clientId)
    {
        lock (_serverLock)
        {
            return _serverSharedSecrets.TryGetValue(clientId, out var secret) ? secret : null;
        }
    }

    public static IEnumerable<KeyValuePair<int, byte[]>> GetAllServerSharedSecrets()
    {
        lock (_serverLock)
        {
            return [.. _serverSharedSecrets];
        }
    }

    public static void RemoveServerKeyExchange(int clientId)
    {
        lock (_serverLock)
        {
            if (_serverEcdh.TryGetValue(clientId, out var serverEcdh))
            {
                serverEcdh.Dispose();
                _serverEcdh.Remove(clientId);
            }

            _serverSharedSecrets.Remove(clientId);
            _serverPublicKeys.Remove(clientId);
            _clientPublicKeys.Remove(clientId);
        }
    }
}
