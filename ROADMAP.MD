# TCP/UDP AOT-Safe RPC Network Framework Roadmap

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