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






public static partial class Client
{
    /// <summary>
    /// True if intentional / false if not
    /// </summary>
    public static event Action<bool>? OnServerShutdown;
    public static event Action<HandshakeMessage>? OnClientConnected;
    public static event Action<NetworkMessage>? OnClientDisconnected;
    public static event Action<NetworkMessage>? OnOtherClientConnected;
    public static event Action<NetworkMessage>? OnOtherClientDisconnected;

    public static event Action<NetworkMessage>? OnTcpMessageSent;
    public static event Action<NetworkMessage>? OnTcpMessageReceived;
    public static event Action<NetworkMessage>? OnTcpRequestMessageReceived;

    public static event Action<NetworkMessage>? OnUdpMessageSent;
    public static event Action<NetworkMessage>? OnUdpMessageReceived;
}