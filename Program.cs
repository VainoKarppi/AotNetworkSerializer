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

public class Person
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
    public object?[] Items { get; set; } = [];
    public Dictionary<string, object?> Flags { get; set; } = [];
    public TimeSpan Test { get; set; }
    public Person? Sub { get; set; }
}


class Program
{
    public class ServerMethods
    {
        public static string GetDataFromServer(string testMessage) {
            Console.WriteLine($"MSG:{testMessage}");
            return "Hello MSG RESPONSE From SERVER!";
        }
        public static string GetData(NetworkMessage message, dynamic testData) {
            Console.WriteLine($"MSG:{testData} type:({testData.GetType()})");
            return "SAME TEST";
        }
    }

    public class ClientMethods
    {
        public string GetDataFromClient(NetworkMessage message, dynamic testData) {
            Console.WriteLine($"MSG:{testData} type:({testData.GetType()})");
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
        MessageBuilder.Methods.RegisterClientMethods( new ClientMethods() );
        MessageBuilder.Methods.RegisterServerMethods( new ServerMethods() );

        var clientMethods = MessageBuilder.Methods.GetAvailableClientMethods();
        var serverMethods = MessageBuilder.Methods.GetAvailableServerMethods();

        Console.WriteLine("=== Client Methods ===");
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

        
        Client.OnTcpMessageSent += OnTcpMessageSent;
        Client.OnTcpMessageReceived += OnTcpMessageReceived;

        // Example Person object with nested data
        var person = new Person
        {
            Name = "Alice",
            Age = 30,
            Test = TimeSpan.FromHours(3.5),
            Items = [
                42,
                "hello",
                null,
                new object?[] { "nested", 123, null }
            ],
            Flags = new Dictionary<string, object?> {
                ["IsActive"] = true,
                ["Null"] = null,
                ["Score"] = 99.5
            }
        };

        Server.StartTcp(5000);
        int id = await Client.ConnectTcp("127.0.0.1", 5000);


        Console.WriteLine($"RECEIVED ID: {id}");

        string? dataFromServer = await Client.RequestTcpDataAsync<string>(1, "GetDataFromServer", "Hello from client! #1");
        string? dataFromServer2 = await Client.RequestTcpDataAsync(1, () => ServerMethods.GetDataFromServer("Hello from client! #2"));

        string data = Serializer.Serialize(person, new Serializer.Options { WriteIndented = true, IncludeRootType = true });


        Type? rootType = Serializer.GetRootType(data);
        if (rootType != null) {
            Console.WriteLine("Data has RootType");
            Console.WriteLine($"ROOT TYPE: {rootType} {rootType == typeof(Person)}");
        }
        

        // --- Serialize and send over network ---
        var msg = new NetworkMessage
        {
            SenderId = 2,
            TargetId = 1,
            MessageType = MessageType.Update
        };



        byte[] packet = MessageBuilder.Pack(msg, person);

        // Peek the header without deserializing the payload
        NetworkMessage message = MessageBuilder.ReadMessage(packet, includeData: true);


        // Peek the message type before deserializing
        MessageType type = MessageBuilder.GetMessageType(packet);
        Console.WriteLine($"MessageType in packet: {type}");

        if (type == MessageType.Update) {
            // --- Unpack on receiving side ---
            Person? receivedPerson = MessageBuilder.Unpack<Person>(message.PayloadBytes);

            // --- Inspect received object ---
            if (receivedPerson != null)
            {
                Console.WriteLine("\nReceived Person object:");
                Console.WriteLine($"Name: {receivedPerson.Name}");
                Console.WriteLine($"Age: {receivedPerson.Age}");
                Console.WriteLine($"Test: {receivedPerson.Test}");
                Console.WriteLine("Items:");
                foreach (var item in receivedPerson.Items)
                {
                    if (item == null) { Console.WriteLine("\t(NULL)"); continue; }
                    Console.WriteLine($"\t{item} ({item.GetType()})");
                    if (item is Array arr)
                    {
                        foreach (var nested in arr)
                        {
                            if (nested == null) { Console.WriteLine("\t\t(NULL)"); continue; }
                            Console.WriteLine($"\t\t{nested} ({nested.GetType()})");
                        }
                    }
                }

                Console.WriteLine("Flags:");
                foreach (var kv in receivedPerson.Flags)
                {
                    Console.WriteLine($"\t{kv.Key}: {kv.Value} ({kv.Value?.GetType()})");
                }
            }
            else
            {
                Console.WriteLine("Received person is null.");
            }
        }
    }
}









