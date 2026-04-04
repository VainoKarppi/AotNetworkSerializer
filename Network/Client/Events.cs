using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DynTypeSerializer;

namespace DynTypeNetwork;


public enum HandshakeFailureReason
{
    Unknown,
    VersionMismatch,
    InvalidHash,
    ServerFull,
    Timeout,
    RejectedByServer
}

public static partial class Client
{
    /// <summary>
    /// True if intentional / false if not
    /// </summary>
    public static event Action<bool>? OnServerShutdown;

    public static event Action<HandshakeFailureReason, string>? OnHandshakeFailed;

    public static event Action<int>? OnClientConnected;
    public static event Action<bool>? OnClientDisconnected;

    public static event Action<int>? OnOtherClientConnected;
    public static event Action<int, bool>? OnOtherClientDisconnected;

    public static event Action<NetworkMessage>? OnTcpMessageSent;
    public static event Action<NetworkMessage>? OnTcpMessageReceived;

    public static event Action<NetworkMessage>? OnUdpMessageSent;
    public static event Action<NetworkMessage>? OnUdpMessageReceived;
}