# DynType Network Framework

A **TCP/UDP AOT-safe RPC network framework** for C# with support for:

- Asynchronous TCP and UDP messaging
- RPC-style method calls with return values or fire-and-forget
- Client and server events
- Serialization with `DynTypeSerializer`
- Launch modes: server, dedicated, debug

Designed for **high-performance, secure networking** in games or real-time applications, including AOT environments.

---

## Features

- **TCP & UDP Communication**
  - Reliable TCP for requests and responses
  - Optional UDP for fast, fire-and-forget messaging
- **RPC System**
  - Call methods on server or clients by name
  - Support for `void` or typed return methods
  - Automatic method registration and introspection
- **Serialization**
  - Supports primitives, strings, arrays, and dynamic types
  - Optional JSON formatting for debug
- **Event Hooks**
  - `OnTcpMessageSent`, `OnTcpMessageReceived`
  - `OnUdpMessageReceived`
  - Client connection/disconnection events
  - Server shutdown events
- **Launch Modes**
  - `server` – starts a server instance
  - `dedicated` – starts a dedicated server without client mode
  - `debug` – enables verbose logging

---

## Installation

1. Clone or download the repository.
2. Reference `DynTypeNetwork.dll` and `DynTypeSerializer.dll` in your project.
3. Use **.NET 10** or later for best AOT compatibility.
4. Run the sample program or implement your own server/client classes.

---

## Launch Parameters

| Parameter | Description |
|-----------|-------------|
| `server` | Start server instance |
| `dedicated` | Start dedicated server without client mode |
| `debug` | Enable verbose logging and serialized message dumps |

> Future settings: `port`, `maxClients`, `encryption`, `natTraversal`, `compression`

---

## Quick Start

```bash
# Run server
dotnet run -- server debug

# Run dedicated server
dotnet run -- dedicated

# Run client
dotnet run
```

---

## Example Usage

### Register Server Methods

```csharp
public class ServerMethods
{
    public static string GetDataFromServer(NetworkMessage message, string testMessage)
    {
        Console.WriteLine($"[SERVER] MSG: {testMessage}");
        return "Hello from Server!";
    }
}
```

### Register Client Methods

```csharp
public class ClientMethods
{
    public static string GetDataFromClient(NetworkMessage message, string testMessage)
    {
        Console.WriteLine($"[CLIENT {Client.ClientID}] MSG: {testMessage}");
        return $"Hello from Client {Client.ClientID}!";
    }
}
```

---

## Event Hooks

```csharp
Client.OnTcpMessageSent += message =>
    Console.WriteLine($"*EVENT* [CLIENT] Sent: {message.Payload}");

Client.OnTcpMessageReceived += message =>
    Console.WriteLine($"*EVENT* [CLIENT] Received: {message.Payload}");

Server.OnTcpMessageReceived += message =>
    Console.WriteLine($"*EVENT* [SERVER] Received: {message.Payload}");

Server.OnServerShutdown += () =>
    Console.WriteLine("*EVENT* [SERVER] Shutdown");
```

---

## Making Requests

```csharp
// Client requesting data from server
string response = await Client.RequestDataAsync<string>(targetId, "GetDataFromServer", "Hello");

// Server requesting data from client
string clientResponse = await Server.RequestDataAsync<string>(clientId, "GetDataFromClient", "Ping");
```

---

## Sending Fire-and-Forget Messages

```csharp
// TCP
await Client.SendTcpMessageAsync(targetId, "MethodName", argument);

// UDP
await Client.SendUdpMessageAsync(targetId, "MethodName", argument);
```

---

## Minimal Example Program

```csharp
static async Task Main(string[] args)
{
    bool serverMode = args.Any(a => a.Equals("server", StringComparison.OrdinalIgnoreCase));
    bool dedicatedMode = args.Any(a => a.Equals("dedicated", StringComparison.OrdinalIgnoreCase));
    bool debugMode = args.Any(a => a.Equals("debug", StringComparison.OrdinalIgnoreCase));
    MessageBuilder.DEBUG = debugMode;

    if (!dedicatedMode) RegisterClientMethods(new ClientMethods());
    if (serverMode || dedicatedMode) RegisterServerMethods(new ServerMethods());

    if (serverMode || dedicatedMode) Server.Start(5000, startUdp: true);

    if (!dedicatedMode && !serverMode)
        await RunClientMode();
    else if (serverMode)
        await RunServerMode();
    else if (dedicatedMode)
        await RunDedicatedMode();
}
```


# Roadmap

This roadmap outlines the **key features, security, reliability, and performance considerations** for the framework. It is structured as a **checklist** for implementation progress tracking and planning.

---

## 0. Launch Parameters & Settings

Current launch parameters:
- `server` – run in server mode
- `dedicated` – run as dedicated server
- `debug` – enable verbose logging

Future settings to add:
- `port` – override default TCP/UDP ports
- `maxClients` – maximum allowed clients
- `encryption` – enable/disable payload encryption
- `logLevel` – DEBUG / INFO / WARN / ERROR
- `natTraversal` – enable/disable NAT hole punching
- `compression` – enable/disable payload compression

---

## 1. Core Networking

- [ ] **TCP Send/Receive Loop**  
  - Async, cancellation token support, error recovery  
- [ ] **UDP Send/Receive Loop**  
  - NAT rebinding support, endpoint registration  
- [ ] **Client Identification**  
  - Unique IDs, reconnect handling, NAT rebinding  
- [ ] **Message Framing / Checksum**  
  - Verify integrity for all messages  

---

## 2. RPC System

- [ ] **Method Registration**
  - Server methods (`CallServerMethod`)  
  - Client methods (`CallClientMethod`)  
  - Support for `void` vs return-type methods  
- [ ] **Request/Response Handling**
  - Request IDs, timeout handling  
  - Fire-and-forget support for UDP
  - [ ] Error handling
- [ ] **Event System**
  - Events: `OnMessageReceived`, `OnUdpMessageReceived`, `OnClientConnected`, `OnClientDisconnected`, `OnError`  
  - Offload execution to thread pool  

---

## 3. Serialization / Payload

- [ ] **Implemented** – supports primitives, strings, arrays  
- [ ] **Large Payload Handling** – splitting & reconstruction  
- [ ] **Versioning & Backward Compatibility**  

---

## 4. Security

- [ ] **Key Exchange / Encryption**
  - Asymmetric handshake (RSA/ECDH) or pre-shared keys  
  - Symmetric session keys (AES/ChaCha20)  
  - Encrypt payloads; optional headers remain unencrypted for routing  
- [ ] **Message Integrity**
  - Checksum + HMAC to prevent tampering  
- [ ] **Replay Protection**
  - Nonce or timestamp per message  
- [ ] **Optional**: authentication & client verification  
- [ ] **Optional**: role based methods
---

## 5. Reliability / Robustness

- [ ] **Connection Handling**
  - Auto-reconnect TCP  
  - UDP endpoint re-registration  
- [ ] **Error Handling**
  - Recover from deserialization errors & bad checksums  
  - Restart loops even if `_udpListener` is null  
- [ ] **Flood / DoS Prevention**
  - Validate payload size  
  - Max requests per second per client  

---

## 6. Performance / Optimization

- [ ] **Batch / Compress Messages** (optional)  
- [ ] **UDP Rate Limiting / Throttling**  
- [ ] **Thread Pool / Task.Run Optimization**  

---

## 7. Diagnostics / Debugging

- [ ] Logging with levels: DEBUG, INFO, ERROR  
- [ ] Metrics: connections, messages/sec, packet loss  
- [ ] Network simulation for testing latency, packet loss, reordering  

---

## 8. Optional / Advanced Features

- [ ] NAT Traversal / Hole Punching  
- [ ] Peer-to-Peer Mode support  
- [ ] Dynamic method registration at runtime  
- [ ] Compression of large payloads (Zstd, LZ4)  

---

## Immediate Priorities

1. Finish **event system** (`OnMessageReceived`, `OnUdpMessageReceived`, etc.)  
2. Implement **key exchange and payload encryption**  
3. Ensure **robust message forwarding / request-response handling** in both TCP and UDP  
4. Add **timeouts, retry, and error handling**  

---

## Notes

- This roadmap can be extended with additional **launch parameters or settings** by adding entries under **0. Launch Parameters & Settings**.  
- Each checklist item can be marked as `[x]` when implemented for tracking progress.  