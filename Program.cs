using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DynTypeNetwork;
using DynTypeSerializer;




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
    public static void OnTcpMessageSent(NetworkMessage message){
        Console.WriteLine($"*EVENT* [CLIENT] OnTcpMessageSent {Serializer.Serialize(message, new Serializer.Options { WriteIndented = true })}");
    }
    public static void OnTcpMessageReceived(NetworkMessage message){
        Console.WriteLine($"*EVENT* [CLIENT] OnTcpMessageReceived {Serializer.Serialize(message, new Serializer.Options { WriteIndented = true })}");
    }
    static async Task Main()
    {
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

        //Server.StartTcp(5000);
        //int id = await Client.ConnectTcp("127.0.0.1", 5000);

        string data = Serializer.Serialize(person, new Serializer.Options { WriteIndented = true, IncludeRootType = true });
        Console.WriteLine(data);

        bool hasRootType = Serializer.ContainsRootType(data);
        if (hasRootType)
        {
            Console.WriteLine("Data has RootType");
            Type rootType = Serializer.GetRootType(data);
            Console.WriteLine($"ROOT TYPE: {rootType} {rootType == typeof(Person)}");
        }
        

        // --- Serialize and send over network ---
        byte[] packet = Network.SendMessage(42, MessageType.Update, person);

        // Peek the header without deserializing the payload
        NetworkMessage header = Network.ReadMessage(packet);

        Console.WriteLine($"SenderId: {header.SenderId}");
        Console.WriteLine($"TargetId: {header.TargetId}");
        Console.WriteLine($"MessageType: {header.MessageType}");
        Console.WriteLine($"MessageId: {header.MessageId}");
        Console.WriteLine($"Timestamp: {header.Timestamp}");
        // PayloadBytes is empty, PayloadLength will be 0

        // Peek the message type before deserializing
        MessageType type = Network.ReadMessageType(packet);
        Console.WriteLine($"MessageType in packet: {type}");

        if (type == MessageType.Update) {
            // --- Unpack on receiving side ---
            Person? receivedPerson = Network.Unpack<Person>(packet);

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









