using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DynTypeNetwork;
using DynTypeSerializer;
using static DynTypeNetwork.MethodBuilder;


namespace Program;



class Program
{
    public class ServerMethods
    {
        public static string GetDataFromServer(NetworkMessage message, string testMessage) {
            Console.WriteLine($"[SERVER] MSG:{testMessage} type:({testMessage.GetType()})");
            return "Hello MSG RESPONSE From SERVER!";
        }
        public static string GetData(NetworkMessage message, dynamic testData) {
            Console.WriteLine($"MSG:{testData} type:({testData.GetType()})");
            return "SAME TEST";
        }
    }

    public class ClientMethods
    {
        public static string GetDataFromClient(NetworkMessage message, string testMessage) {
            Console.WriteLine($"[CLIENT {Client.ClientID}] MSG:{testMessage} type:({testMessage.GetType()})");
            return $"Hello MSG RESPONSE From CLIENT ({Client.ClientID})!";
        }

        public static string GetData(NetworkMessage message, dynamic testData) {
            Console.WriteLine($"[CLIENT {Client.ClientID}] MSG:{testData} type:({testData.GetType()})");
            return "SAME TEST";
        }
    }


    public static void OnTcpMessageSent(NetworkMessage message){
        Console.WriteLine($"*EVENT* [CLIENT {Client.ClientID}] OnTcpMessageSent {Serializer.Serialize(message, new Serializer.Options { WriteIndented = true })}");
    }
    public static void OnTcpMessageReceived(NetworkMessage message){
        Console.WriteLine($"*EVENT* [CLIENT {Client.ClientID}] OnTcpMessageReceived {Serializer.Serialize(message, new Serializer.Options { WriteIndented = true })}");
    }
    public static void OnTcpMessageSentServer(NetworkMessage message){
        Console.WriteLine($"*EVENT* [SERVER] OnTcpMessageSent {Serializer.Serialize(message, new Serializer.Options { WriteIndented = true })}");
    }
    public static void OnTcpMessageReceivedServer(NetworkMessage message){
        Console.WriteLine($"*EVENT* [SERVER] OnTcpMessageReceived {Serializer.Serialize(message, new Serializer.Options { WriteIndented = true })}");
    }
    public static void OnOtherClientDisconnected(int clientId, bool success){
        Console.WriteLine($"*EVENT* [CLIENT {Client.ClientID}] OnOtherClientDisconnected {clientId} - Success: {success}");
    }
    public static void OnServerShutdown(bool success){
        Console.WriteLine($"*EVENT* [CLIENT {Client.ClientID}] OnServerShutdown - Success: {success}");
        Environment.Exit(success ? 0 : 1);
    }
    public static void OnServerShutdownServer() {
        Console.WriteLine($"*EVENT* [SERVER] OnServerShutdown");
    }

    static async Task Main(string[] args)
    {
        bool serverMode = args.Any(a => a.Equals("server", StringComparison.OrdinalIgnoreCase));
        bool dedicatedMode = args.Any(a => a.Equals("dedicated", StringComparison.OrdinalIgnoreCase));
        bool debugMode = args.Any(a => a.Equals("debug", StringComparison.OrdinalIgnoreCase));
        MessageBuilder.DEBUG = debugMode;

        // ── REGISTER METHODS ─────────────────────
        if (!dedicatedMode) RegisterClientMethods(new ClientMethods());
        if (serverMode || dedicatedMode) RegisterServerMethods(new ServerMethods());

        // ── PRINT AVAILABLE METHODS ─────────────
        if (!dedicatedMode) PrintAvailableMethods("Client", GetAvailableClientMethods());
        if (serverMode || dedicatedMode) PrintAvailableMethods("Server", GetAvailableServerMethods());

        // ── REGISTER CLIENT EVENTS ──────────────────────
        if (!dedicatedMode)
        {
            Client.OnTcpMessageSent += OnTcpMessageSent;
            Client.OnTcpMessageReceived += OnTcpMessageReceived;
            Client.OnOtherClientDisconnected += OnOtherClientDisconnected;
            Client.OnServerShutdown += OnServerShutdown;
        }

        if (serverMode || dedicatedMode)
        {
            Server.OnTcpMessageReceived += OnTcpMessageReceivedServer;
            Server.OnTcpMessageSent += OnTcpMessageSentServer;
            Server.OnServerShutdown += OnServerShutdownServer;
        }

        // ── START SERVER IF APPLICABLE ──────────
        if (serverMode || dedicatedMode)
        {
            Server.Start(5000, startUdp: true);
            Console.WriteLine("[SERVER] TCP Server started");
        }

        // ── RUN APPROPRIATE MODE ─────────────────
        if (!dedicatedMode && !serverMode)
            await RunClientMode();
        else if (serverMode)
            await RunServerMode();
        else if (dedicatedMode)
            await RunDedicatedMode();
    }

    #region Client Mode
    private static async Task RunClientMode()
    {
        int clientId = await Client.ConnectAsync("127.0.0.1", 5000, startUdp: true);
        Console.WriteLine($"[CLIENT] Connected with ID: {clientId}");

        while (true)
        {
            try {
                Console.Write("\n[CLIENT] Enter command (send/request/methods/clients/self/udpstatus/sendudp/exit): ");
                string? input = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(input)) continue;

                string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string command = parts[0].ToLowerInvariant();

                switch (command)
                {
                    case "exit":
                        await Client.Disconnect();
                        return;

                    case "methods":
                        PrintAvailableMethods("Client", GetAvailableClientMethods());
                        PrintAvailableMethods("Server", GetAvailableServerMethods());
                        break;
                    
                    case "self":
                        Console.WriteLine($"[CLIENT] My ID: {Client.ClientID}");
                        break;

                    case "clients":
                        var others = Client.GetOtherClients();
                        Console.WriteLine($"[CLIENT] Other connected clients: {others.Count}");
                        foreach (var other in others) Console.WriteLine($"Other client: {other}");
                        break;

                    case "send":
                        await HandleClientSend(parts, isTcp: true);
                        break;
                    
                    case "sendudp":
                        await HandleClientSend(parts, isTcp: false);
                        break;

                    case "request":
                        await HandleClientRequest(parts);
                        break;
                    
                    case "udpstatus":
                        Console.WriteLine($"[CLIENT] UDP Status: {(Client.IsUdpConnected() ? "Connected" : "Disconnected")}");
                        break;

                    default:
                        Console.WriteLine("[CLIENT] Unknown command");
                        break;
                }
            } catch (Exception ex) {
                Console.WriteLine($"[CLIENT] Error: {ex.Message}");
            }
        }
    }

    private static async Task HandleClientSend(string[] parts, bool isTcp = true)
    {
        if (parts.Length < 4)
        {
            Console.WriteLine("[CLIENT] Usage: send <targetId> <methodName> <arg>");
            return;
        }

        if (!int.TryParse(parts[1], out int targetId))
        {
            Console.WriteLine("[CLIENT] Invalid target ID");
            return;
        }

        string methodName = parts[2];
        string argument = string.Join(' ', parts.Skip(3));

        await (isTcp
            ? Client.SendTcpMessageAsync(targetId, methodName, argument)
            : Client.SendUdpMessageAsync(targetId, methodName, argument));

        Console.WriteLine($"[CLIENT] Message sent to {targetId}");
    }

    private static async Task HandleClientRequest(string[] parts)
    {
        if (parts.Length < 4)
        {
            Console.WriteLine("[CLIENT] Usage: request <targetId> <methodName> <arg>");
            return;
        }

        if (!int.TryParse(parts[1], out int targetId))
        {
            Console.WriteLine("[CLIENT] Invalid target ID");
            return;
        }

        string methodName = parts[2];
        string argument = string.Join(' ', parts.Skip(3));

        try
        {
            var result = await Client.RequestDataAsync<string>(targetId, methodName, argument);
            Console.WriteLine($"[CLIENT] Result from {targetId}.{methodName}: {result}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CLIENT] Error: {ex.Message}");
        }
    }
    #endregion

    #region Server Mode
    private static async Task RunServerMode()
    {
        while (true)
        {
            try {
                Console.Write("\n[SERVER] Enter command (send/request/methods/clients/udpstatus/sendudp/exit): ");
                string? input = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(input)) continue;

                string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string command = parts[0].ToLowerInvariant();

                switch (command)
                {
                    case "exit":
                        Console.WriteLine("[SERVER] Stopping...");
                        await Server.StopAsync();
                        return;

                    case "methods":
                        PrintAvailableMethods("Client", GetAvailableClientMethods());
                        PrintAvailableMethods("Server", GetAvailableServerMethods());
                        break;

                    case "clients":
                        var clients = Server.GetClients();
                        Console.WriteLine($"[SERVER] Connected clients: {clients.Count}");
                        foreach (var id in clients) Console.WriteLine($"Client ID: {id}");
                        break;

                    case "send":
                        await HandleServerSend(parts, isTcp: true);
                        break;
                    
                    case "sendudp":
                        await HandleServerSend(parts, isTcp: false);
                        break;

                    case "request":
                        await HandleServerRequest(parts);
                        break;
                    
                    case "udpstatus":
                        Console.WriteLine($"[SERVER] UDP Status: {(Server.IsUdpServerRunning() ? "Connected" : "Disconnected")}");
                        break;

                    default:
                        Console.WriteLine("[SERVER] Unknown command");
                        break;
                }
            } catch (Exception ex) {
                Console.WriteLine($"[SERVER] Error: {ex.Message}");
            }
        }
    }

    private static async Task HandleServerSend(string[] parts, bool isTcp = true)
    {
        if (parts.Length < 4)
        {
            Console.WriteLine("[SERVER] Usage: send <targetId> <methodName> <arg>");
            return;
        }

        if (!int.TryParse(parts[1], out int targetId))
        {
            Console.WriteLine("[SERVER] Invalid target ID");
            return;
        }

        string methodName = parts[2];
        string argument = string.Join(' ', parts.Skip(3));

        if (!Server.GetClients().Contains(targetId))
        {
            Console.WriteLine("[SERVER] Client not found");
            return;
        }

        await (isTcp
            ? Server.SendTcpMessageAsync(targetId, methodName, argument)
            : Server.SendUdpMessageAsync(targetId, methodName, argument));
        
        Console.WriteLine($"[SERVER] Message sent to client {targetId}");
    }

    private static async Task HandleServerRequest(string[] parts)
    {
        if (parts.Length < 4)
        {
            Console.WriteLine("[SERVER] Usage: request <targetId> <methodName> <arg>");
            return;
        }

        if (!int.TryParse(parts[1], out int targetId))
        {
            Console.WriteLine("[SERVER] Invalid target ID");
            return;
        }

        string methodName = parts[2];
        string argument = string.Join(' ', parts.Skip(3));

        try
        {
            var result = await Server.RequestDataAsync<string>(targetId, methodName, argument);
            Console.WriteLine($"[SERVER] Result from client {targetId} ({methodName}): {result}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SERVER] Error: {ex.Message}");
        }
    }
    #endregion

    #region Dedicated Mode
    private static async Task RunDedicatedMode()
    {
        Console.WriteLine("[DEDICATED] Server running in dedicated mode");
        await RunServerMode();
    }
    #endregion

    #region Helper
    private static void PrintAvailableMethods(string name, RpcMethodInfo[] methods)
    {
        Console.WriteLine($"\n=== {name} Methods ===");
        foreach (var method in methods)
        {
            string parameters = string.Join(", ", method.Parameters.Select(p => $"{p.Type.Name} {p.Name}"));
            string returnType = method.ReturnType?.Name ?? "void";
            Console.WriteLine($"{method.Name}({parameters}) : {returnType}");
        }
    }
    #endregion


}









