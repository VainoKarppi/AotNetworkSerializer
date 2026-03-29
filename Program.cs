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
        public static string GetDataFromServer(string testMessage) {
            Console.WriteLine($"[SERVER] MSG:{testMessage} type:({testMessage.GetType()})");
            return "Hello MSG RESPONSE From SERVER!";
        }
        public static string GetData(dynamic testData) {
            Console.WriteLine($"MSG:{testData} type:({testData.GetType()})");
            return "SAME TEST";
        }
    }

    public class ClientMethods
    {
        public static string GetDataFromClient(string testMessage) {
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
    static async Task Main()
    {
        MethodBuilder.RegisterClientMethods( new ClientMethods() );
        MethodBuilder.RegisterServerMethods( new ServerMethods() );

        var clientMethods = MethodBuilder.GetAvailableClientMethods();
        var serverMethods = MethodBuilder.GetAvailableServerMethods();

        Console.WriteLine("\n=== Client Methods ===");
        foreach (var method in clientMethods)
        {
            string parameters = string.Join(", ", method.Parameters.Select(p => $"{p.Type.Name} {p.Name}"));
            string returnType = method.ReturnType?.Name ?? "void";
            Console.WriteLine($"{method.Name}({parameters}) : {returnType}");
        }

        Console.WriteLine("\n=== Server Methods ===");
        foreach (var method in serverMethods)
        {
            string parameters = string.Join(", ", method.Parameters.Select(p => $"{p.Type.Name} {p.Name}"));
            string returnType = method.ReturnType?.Name ?? "void";
            Console.WriteLine($"{method.Name}({parameters}) : {returnType}");
        }
        Console.WriteLine("\n");

        
        Client.OnTcpMessageSent += OnTcpMessageSent;
        Client.OnTcpMessageReceived += OnTcpMessageReceived;


        Server.StartTcp(5000);
        int id = await Client.ConnectTcp("127.0.0.1", 5000);

        Console.WriteLine($"RECEIVED CLIENT_ID: {id}\n\n");

        string? dataFromServer = await Client.RequestTcpDataAsync<string>(Server.SERVER_ID, "GetDataFromServer", "Hello from client! #1");
        Console.WriteLine($"\nRETURNED FROM SERVER #1: {dataFromServer}\n");
        string? dataFromServer2 = await Client.RequestTcpDataAsync(Server.SERVER_ID, () => ServerMethods.GetDataFromServer("Hello from client! #2"));
        Console.WriteLine($"\nRETURNED FROM SERVER #2: {dataFromServer2}\n");

        
        string? dataFromClient = await Server.RequestTcpDataAsync<string>(2, "GetDataFromClient", "Hello from server! #1");
        Console.WriteLine($"\nRETURNED FROM CLIENT: {dataFromClient}");
        
    }
}









