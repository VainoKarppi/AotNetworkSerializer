using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq.Expressions;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DynTypeSerializer;

namespace DynTypeNetwork;






public static partial class Client
{
    private static TcpClient? _tcpClient;
    private static NetworkStream? _tcpStream;
    
    
    public static bool IsTcpConnected() => _tcpClient != null && _tcpClient.Connected;


    // ── Connect TCP ──────────────────────────
    private static async Task<int> ConnectTcp(string host, int port, string? customHash = null)
    {
        _tcpClient = new TcpClient();
        await _tcpClient.ConnectAsync(host, port);
        _tcpStream = _tcpClient.GetStream();
        StartTcpReceiveLoop(_tcpStream);

        KeyExchange.InitializeClientKeyExchange();

        //string assemblyHash = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "";

        string buildId = Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId.ToString();

        // Combine with customHash if provided
        var availableMethods = MethodBuilder.GetAvailableClientMethods();
        string methodsHash = MethodBuilder.ComputeMethodsHash(availableMethods);

        HandshakeMessage handshake = new() {
            Hash = $"{buildId}|{customHash ?? ""}|{methodsHash}",
            AvailableMethods = availableMethods,
            ClientPublicKey = Convert.ToBase64String(KeyExchange.ClientPublicKey!)
        };
        
        HandshakeMessage? response = await RequestDataInternalAsync(Server.SERVER_ID, MessageType.Handshake, handshake);
        if (response == null) throw new Exception("Handshake failed (Connection lost)");

        if (!response.Success) throw new Exception(response.Message ?? "Handshake failed (Unknown reason)");
        if (string.IsNullOrEmpty(response.ServerPublicKey)) throw new Exception("Handshake failed (Missing server public key)");

        KeyExchange.ComputeClientSharedSecret(response.ServerPublicKey);

        ClientID = response.ClientId;
        Clients.AddRange(response.OtherConnectedClients);

        int count = MethodBuilder.RegisterFromHandshake(response.AvailableMethods, isServer: false);

        // Allow API user to request custom data from server, before connect success (eg. other clients etc)
        OnClientConnected?.Invoke(response.ClientId);

        return ClientID;
    }

    


    private static void StartTcpReceiveLoop(NetworkStream stream)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    // Read ONE full message from stream using the proper helper
                    NetworkMessage? msg = MessageBuilder.ReadTcpMessage(stream);

                    if (msg == null) {
                        // Connection lost or stream closed
                        // TODO send client disconnect instead?
                        await HandleServerShutdown(false);
                        break;
                    }

                    if (msg.MessageType == MessageType.Handshake) {
                        Responses[msg.MessageId] = msg;
                        continue;
                    }

                    if (msg.MessageType == MessageType.Response) {
                        Responses[msg.MessageId] = msg;
                        continue;
                    }

                    if (msg.MessageType == MessageType.ClientConnected) {
                        if (msg.Payload == null) continue;
                        int? newClient = MessageBuilder.UnpackPayload<int>(msg.Payload);
                        if (newClient == null) continue;

                        Clients.Add(newClient.Value);

                        _ = Task.Run(() => OnOtherClientConnected?.Invoke(newClient.Value));
                        
                        continue;
                    }

                    if (msg.MessageType == MessageType.ClientDisconnected) {
                        object[]? data = MessageBuilder.UnpackPayload<object[]>(msg.Payload);
                        if (data == null || data.Length != 2) continue;

                        int client_id = (int)data[0];
                        bool success = (bool)data[1];
                        
                        Clients.Remove(client_id);
                        _ = Task.Run(() => OnOtherClientDisconnected?.Invoke(client_id, success));

                        continue;
                    }

                    if (msg.MessageType == MessageType.ServerShutdown) {
                        await HandleServerShutdown(true);
                        break;
                    }

                    if (msg.MessageType == MessageType.Custom) {
                        // Invoke event 
                        _ = Task.Run(() => OnTcpMessageReceived?.Invoke(msg));

                        await MessageBuilder.HandleCustomMessage(stream, msg, token);
                        continue;
                    }

                    
                }
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
                Console.WriteLine("[CLIENT] TCP receive loop cancelled.");
            }
            catch (Exception ex) when (ex is ObjectDisposedException || ex is IOException)
            {
                // Connection was forcibly closed
                Console.WriteLine($"[CLIENT] Connection lost: {ex.Message}");
                await HandleServerShutdown(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CLIENT] Receive loop exception: {ex}");
                await HandleServerShutdown(false);
            }
        });
    }
}