using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DynTypeNetwork;
using DynTypeSerializer;


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
            Console.WriteLine($"[CLIENT] MSG:{testMessage} type:({testMessage.GetType()})");
            return "Hello MSG RESPONSE From CLIENT!";
        }

        public static string GetData(NetworkMessage message, dynamic testData) {
            Console.WriteLine($"MSG:{testData} type:({testData.GetType()})");
            return "SAME TEST";
        }
    }


    public static void OnTcpMessageSent(NetworkMessage message){
        Console.WriteLine($"*EVENT* [CLIENT] OnTcpMessageSent {Serializer.Serialize(message, new Serializer.Options { WriteIndented = true })}");
    }
    public static void OnTcpMessageReceived(NetworkMessage message){
        Console.WriteLine($"*EVENT* [CLIENT] OnTcpMessageReceived {Serializer.Serialize(message, new Serializer.Options { WriteIndented = true })}");
    }
    public static void OnTcpMessageReceivedServer(NetworkMessage message){
        Console.WriteLine($"*EVENT* [SERVER] OnTcpMessageReceived {Serializer.Serialize(message, new Serializer.Options { WriteIndented = true })}");
    }
    public static void OnOtherClientDisconnected(int clientId, bool success){
        Console.WriteLine($"*EVENT* [CLIENT] OnOtherClientDisconnected {clientId} - Success: {success}");
    }

    static async Task Main(string[] args)
    {
        bool server = args.Any(a => a.Equals("server", StringComparison.OrdinalIgnoreCase));
        bool dedicated = args.Any(a => a.Equals("dedicated", StringComparison.OrdinalIgnoreCase));
        bool debug = args.Any(a => a.Equals("debug", StringComparison.OrdinalIgnoreCase));
        MessageBuilder.DEBUG = debug;

        // ── REGISTER METHODS ─────────────────────
        if (!dedicated) MethodBuilder.RegisterClientMethods(new ClientMethods());
        if (server || dedicated) MethodBuilder.RegisterServerMethods(new ServerMethods());

        // ── PRINT METHODS ────────────────────────
        if (!dedicated)
        {
            Console.WriteLine("\n=== Client Methods ===");
            foreach (var method in MethodBuilder.GetAvailableClientMethods())
            {
                string parameters = string.Join(", ", method.Parameters.Select(p => $"{p.Type.Name} {p.Name}"));
                string returnType = method.ReturnType?.Name ?? "void";
                Console.WriteLine($"{method.Name}({parameters}) : {returnType}");
            }
        }

        if (server || dedicated)
        {
            Console.WriteLine("\n=== Server Methods ===");
            foreach (var method in MethodBuilder.GetAvailableServerMethods())
            {
                string parameters = string.Join(", ", method.Parameters.Select(p => $"{p.Type.Name} {p.Name}"));
                string returnType = method.ReturnType?.Name ?? "void";
                Console.WriteLine($"{method.Name}({parameters}) : {returnType}");
            }
        }

        Console.WriteLine();

        // ── EVENTS ───────────────────────────────
        // Client events (only when not dedicated)
        if (!dedicated) {
            Client.OnTcpMessageSent += OnTcpMessageSent;
            Client.OnTcpMessageReceived += OnTcpMessageReceived;
            Client.OnOtherClientDisconnected += OnOtherClientDisconnected;
        }

        // Server events (when server exists)
        if (dedicated || server)
        {
            Server.MessageReceived += OnTcpMessageReceivedServer;
        }


        // ── START SERVER ─────────────────────────
        if (server || dedicated)
        {
            Server.StartTcp(5000);
            Console.WriteLine("[SERVER] Started");
        }

        // ── CLIENT MODE ──────────────────────────
        if (!dedicated)
        {
            int client_id = await Client.ConnectTcp("127.0.0.1", 5000);
            Console.WriteLine($"RECEIVED CLIENT_ID: {client_id}\n");

            Console.WriteLine("\n=== Server Methods ===");
            foreach (var method in MethodBuilder.GetAvailableServerMethods())
            {
                string parameters = string.Join(", ", method.Parameters.Select(p => $"{p.Type.Name} {p.Name}"));
                string returnType = method.ReturnType?.Name ?? "void";
                Console.WriteLine($"{method.Name}({parameters}) : {returnType}");
            }
            Console.WriteLine();

            Console.WriteLine($"Number of other connected clients: {Client.GetOtherClients().Count}");
            foreach (var item in Client.GetOtherClients())
            {
                Console.WriteLine($"Other connected client: {item}");
            }

            // ── CLIENT → SERVER ──────────────────
            string? dataFromServer = await Client.RequestTcpDataAsync<string>(Server.SERVER_ID, "GetDataFromServer", "Hello from client! #1");

            Console.WriteLine($"\nRETURNED FROM SERVER #1: {dataFromServer}\n");


            // ── SERVER → CLIENT (only if server exists) ──
            if (server)
            {
                string? dataFromClient = await Server.RequestTcpDataAsync<string>(client_id, "GetDataFromClient", "Hello from server! #1");

                Console.WriteLine($"\nRETURNED FROM CLIENT #1: {dataFromClient}");
            }
        }

        Console.ReadKey();
        Console.WriteLine("\n[SERVER] Stopped");
    }
}









