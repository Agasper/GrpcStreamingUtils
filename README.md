# Niarru.GrpcStreamingUtils

A .NET 8 library for building typed RPC-over-stream communication on top of gRPC bidirectional streams. Provides request/response correlation, automatic dispatching, keep-alive management, and reconnection logic — all with a clean interface-based API.

## Features

- **Typed RPC over gRPC streams** — define an interface, get a proxy client and auto-dispatcher
- **Request/response correlation** — automatic matching via `RequestEnvelope` / `ResponseEnvelope`
- **Timeout & cancellation** — per-call timeouts with configurable defaults
- **Error propagation** — `StreamRpcException` carries `Grpc.Core.StatusCode` across the wire
- **Keep-alive** — automatic ping/pong and idle timeout detection
- **Reconnection** — exponential backoff reconnect client as a `BackgroundService`
- **Thread-safe** — concurrent writes are serialized, all state is protected

## Installation

Add a project reference or (when published) install via NuGet:

```bash
dotnet add package Niarru.GrpcStreamingUtils
```

## Quick Start

### 1. Define a shared RPC contract

Create an interface in a project shared between client and server. Each method must:
- Have `IMessage` (protobuf) as the first parameter
- Optionally accept `CancellationToken` as the second parameter
- Return `Task` (void) or `Task<T>` where `T : IMessage`

```csharp
public interface IBattleServerRpc
{
    Task<RoomCreated> CreateRoom(CreateRoom request, CancellationToken ct = default);
    Task<ModifyRoomResult> ModifyRoom(ModifyRoom request, CancellationToken ct = default);
    Task DeleteRoom(DeleteRoom request, CancellationToken ct = default);
}
```

### 2. Calling side (client)

```csharp
var options = new StreamingOptions { DefaultCommandTimeoutSeconds = 10 };

var rpcClient = new StreamRpcClient(
    sendFunc: async (envelope, ct) =>
    {
        // Wrap RequestEnvelope into your stream message and send
        await connection.SendAsync(new MyStreamMessage { Request = envelope }, ct);
    },
    options,
    logger);

// Create a typed proxy
var rpc = rpcClient.CreateProxy<IBattleServerRpc>();

// Make RPC calls as regular async method calls
var result = await rpc.CreateRoom(new CreateRoom { Name = "Room1" }, ct);
Console.WriteLine(result.Room.Id);
```

When receiving messages from the stream, feed responses back:

```csharp
// In your message receive loop:
if (msg.Response != null)
    rpcClient.TryComplete(msg.Response);
```

### 3. Handling side (server)

Implement the interface:

```csharp
public class BattleServerRpcHandler : IBattleServerRpc
{
    public async Task<RoomCreated> CreateRoom(CreateRoom request, CancellationToken ct)
    {
        var room = await _roomService.CreateAsync(request.Name, ct);
        return new RoomCreated { Room = room };
    }

    public async Task<ModifyRoomResult> ModifyRoom(ModifyRoom request, CancellationToken ct)
    {
        // ...
        return new ModifyRoomResult { Success = true };
    }

    public Task DeleteRoom(DeleteRoom request, CancellationToken ct)
    {
        _roomService.Delete(request.RoomId);
        return Task.CompletedTask;
    }
}
```

Create a dispatcher and route incoming requests:

```csharp
var dispatcher = StreamRpcDispatcher.Create<IBattleServerRpc>(
    handler: new BattleServerRpcHandler(),
    sendFunc: async (envelope, ct) =>
    {
        await connection.SendAsync(new MyStreamMessage { Response = envelope }, ct);
    },
    logger);

// In your message receive loop:
if (msg.Request != null)
    await dispatcher.DispatchAsync(msg.Request, ct);
```

---

## Detailed Usage

### RPC Contract Rules

```csharp
public interface IMyService
{
    // Request/Response — first param is IMessage, returns Task<IMessage>
    Task<GetUserResponse> GetUser(GetUserRequest request, CancellationToken ct = default);

    // Fire-and-forget — returns Task (void), server still sends acknowledgement
    Task NotifyEvent(EventNotification request, CancellationToken ct = default);

    // CancellationToken is optional
    Task<PingResponse> Ping(PingRequest request);
}
```

**Constraints:**
- First parameter **must** implement `Google.Protobuf.IMessage`
- Return type **must** be `Task` or `Task<T>` where `T : IMessage`
- Each request message type maps to exactly one handler method (via protobuf type URL)

### StreamRpcClient

The client manages request/response correlation over a single stream.

```csharp
var client = new StreamRpcClient(sendFunc, options, logger);
```

| Method | Description |
|--------|-------------|
| `CreateProxy<T>()` | Creates a `DispatchProxy` implementing `T` |
| `TryComplete(ResponseEnvelope)` | Resolves a pending call by `InReplyToRequestId` |
| `CancelAll()` | Cancels all pending calls |
| `Dispose()` | Calls `CancelAll()` and prevents new calls |

**Lifecycle:** Create one `StreamRpcClient` per stream connection. When the connection drops, dispose the client (all pending calls get cancelled) and create a new one on reconnect.

### StreamRpcProxy

Created via `StreamRpcClient.CreateProxy<T>()`. Intercepts interface method calls and:

1. Packs the `IMessage` argument into `RequestEnvelope.Payload` via `Any.Pack()`
2. Sends the envelope through the client's `sendFunc`
3. Waits for `TryComplete()` to resolve the matching response
4. Unpacks `ResponseEnvelope.Payload` via `Any.Unpack<T>()` and returns it
5. For `Task` (void) methods — waits for acknowledgement but ignores payload

### StreamRpcDispatcher

Created via `StreamRpcDispatcher.Create<T>(handler, sendFunc, logger)`. At creation time:

1. Scans all methods on the interface
2. Validates signatures (fail-fast on invalid contracts)
3. Builds a `TypeUrl → MethodHandler` mapping using protobuf descriptors

On `DispatchAsync(RequestEnvelope, CancellationToken)`:

1. Looks up handler by `Payload.TypeUrl`
2. Parses the payload using the pre-built `MessageParser`
3. Invokes the handler method
4. Packs the result into `ResponseEnvelope` and sends it back

**Error mapping:**

| Situation | StatusCode | Error |
|-----------|-----------|-------|
| Handler returns normally | `OK (0)` | — |
| Handler throws `StreamRpcException` | Exception's `StatusCode` | Exception's `Message` |
| Handler throws any other `Exception` | `Internal (13)` | Exception's `Message` |
| Unknown `TypeUrl` | `Unimplemented (12)` | `"No handler for '...'"` |
| Failed to parse payload | `InvalidArgument (3)` | Parse error message |
| Null payload | `InvalidArgument (3)` | `"Request payload is null."` |

### StreamRpcException

Typed exception carrying a gRPC `StatusCode`. Use it in handlers to return specific error codes:

```csharp
public async Task<RoomCreated> CreateRoom(CreateRoom request, CancellationToken ct)
{
    if (string.IsNullOrEmpty(request.Name))
        throw new StreamRpcException(StatusCode.InvalidArgument, "Room name is required");

    if (!await HasCapacity(ct))
        throw new StreamRpcException(StatusCode.ResourceExhausted, "No capacity for new rooms");

    var room = await FindRoom(request.Name, ct);
    if (room != null)
        throw new StreamRpcException(StatusCode.AlreadyExists, $"Room '{request.Name}' already exists");

    // ...
}
```

On the calling side, catch it:

```csharp
try
{
    var result = await rpc.CreateRoom(new CreateRoom { Name = "Room1" }, ct);
}
catch (StreamRpcException ex) when (ex.StatusCode == StatusCode.ResourceExhausted)
{
    logger.LogWarning("Server is at capacity: {Error}", ex.Message);
}
catch (StreamRpcException ex)
{
    logger.LogError("RPC failed with {StatusCode}: {Error}", ex.StatusCode, ex.Message);
}
catch (TimeoutException)
{
    logger.LogError("RPC call timed out");
}
```

### Proto Messages

The library uses two envelope messages defined in `streaming_rpc.proto`:

```protobuf
message RequestEnvelope {
  string request_id = 1;              // Unique correlation ID (GUID)
  google.protobuf.Any payload = 2;    // Packed IMessage request
}

message ResponseEnvelope {
  string in_reply_to_request_id = 1;  // Matches RequestEnvelope.request_id
  int32 status = 2;                   // Grpc.Core.StatusCode as int (0 = OK)
  string error = 3;                   // Error text (when status != 0)
  google.protobuf.Any payload = 4;    // Packed IMessage response (when status == 0)
}
```

Embed these envelopes in your application-level stream messages:

```protobuf
// Your application proto
message MyStreamMessage {
  oneof content {
    RequestEnvelope request = 1;
    ResponseEnvelope response = 2;
    Ping ping = 3;
    // ...other message types...
  }
}
```

---

## Connection Management

### ClientStreamConnection

Abstract base for client-side gRPC duplex stream connections. Provides write serialization, keep-alive integration, and lifecycle events.

```csharp
public class MyClientConnection : ClientStreamConnection<ServerMessage, ClientMessage>
{
    private readonly Action<ServerMessage> _onMessage;

    public MyClientConnection(
        AsyncDuplexStreamingCall<ClientMessage, ServerMessage> stream,
        StreamingOptions options,
        TimeProvider timeProvider,
        ILogger logger,
        Action<ServerMessage> onMessage)
        : base(stream, options, timeProvider, logger)
    {
        _onMessage = onMessage;
    }

    protected override ClientMessage CreatePingMessage()
        => new ClientMessage { Ping = new Ping() };

    protected override Task OnMessageReceivedAsync(ServerMessage message, CancellationToken ct)
    {
        _onMessage(message);
        return Task.CompletedTask;
    }

    protected override void OnConnectionClosed(StreamConnectionClosedArgs args)
    {
        if (args.Reason == CloseReason.Error)
            Console.WriteLine($"Connection error: {args.Exception?.Message}");
    }
}
```

### ServerStreamConnection

Abstract base for server-side connections within a gRPC service method.

```csharp
public class MyServerConnection : ServerStreamConnection<ClientMessage, ServerMessage>
{
    private readonly StreamRpcDispatcher _dispatcher;

    public MyServerConnection(
        IAsyncStreamReader<ClientMessage> requestStream,
        IServerStreamWriter<ServerMessage> responseStream,
        StreamingOptions options,
        TimeProvider timeProvider,
        CancellationToken grpcCallCancellation,
        ILogger logger,
        StreamRpcDispatcher dispatcher)
        : base(requestStream, responseStream, options, timeProvider, grpcCallCancellation, logger)
    {
        _dispatcher = dispatcher;
    }

    protected override ServerMessage CreatePingMessage()
        => new ServerMessage { Pong = new Pong() };

    protected override async Task OnMessageReceivedAsync(ClientMessage message, CancellationToken ct)
    {
        if (message.Request != null)
            await _dispatcher.DispatchAsync(message.Request, ct);
    }
}
```

### ReconnectingStreamClient

A `BackgroundService` that automatically reconnects on stream failure with exponential backoff.

```csharp
public class MyStreamClient : ReconnectingStreamClient<MyClientConnection, ServerMessage, ClientMessage>
{
    private readonly MyGrpcService.MyGrpcServiceClient _grpcClient;
    private StreamRpcClient? _rpcClient;
    private IBattleServerRpc? _rpc;

    public MyStreamClient(
        MyGrpcService.MyGrpcServiceClient grpcClient,
        StreamKeepAliveMonitor keepAliveMonitor,
        StreamingOptions options,
        TimeProvider timeProvider,
        ILogger<MyStreamClient> logger)
        : base(keepAliveMonitor, options, timeProvider, logger)
    {
        _grpcClient = grpcClient;
    }

    protected override string ClientName => "MyStreamClient";

    protected override AsyncDuplexStreamingCall<ClientMessage, ServerMessage> CreateStream(
        CancellationToken ct)
        => _grpcClient.BiDirectionalStream(cancellationToken: ct);

    protected override MyClientConnection CreateConnection(
        AsyncDuplexStreamingCall<ClientMessage, ServerMessage> stream)
    {
        _rpcClient?.Dispose();
        _rpcClient = new StreamRpcClient(
            async (env, ct) => await SendAsync(new ClientMessage { Request = env }, ct),
            _streamingOptions,
            Logger);
        _rpc = _rpcClient.CreateProxy<IBattleServerRpc>();

        return new MyClientConnection(stream, _streamingOptions, TimeProvider, Logger,
            msg =>
            {
                if (msg.Response != null)
                    _rpcClient.TryComplete(msg.Response);
            });
    }

    protected override Task SendHandshakeAsync(
        AsyncDuplexStreamingCall<ClientMessage, ServerMessage> stream,
        CancellationToken ct)
    {
        // Optional: send auth/handshake before entering the read loop
        return stream.RequestStream.WriteAsync(
            new ClientMessage { Handshake = new Handshake { Token = "..." } });
    }

    // Expose RPC proxy for external use
    public IBattleServerRpc Rpc => _rpc ?? throw new StreamNotEstablishedException();
}
```

---

## Configuration

### StreamingOptions

Configure via `appsettings.json` section `"Streaming"`:

```json
{
  "Streaming": {
    "DefaultCommandTimeoutSeconds": 15,
    "IdleTimeoutSeconds": 30,
    "PingIntervalSeconds": 10,
    "InitialReconnectIntervalSeconds": 1,
    "MaxReconnectIntervalSeconds": 60
  }
}
```

| Property | Default | Description |
|----------|---------|-------------|
| `DefaultCommandTimeoutSeconds` | 15 | Default timeout for RPC calls |
| `IdleTimeoutSeconds` | 30 | Close connection if no messages received |
| `PingIntervalSeconds` | 10 | Interval between keep-alive pings |
| `InitialReconnectIntervalSeconds` | 1 | First reconnect delay (1–300) |
| `MaxReconnectIntervalSeconds` | 60 | Max reconnect delay after backoff (1–3600) |

### DI Registration

```csharp
builder.Services.AddStreaming(builder.Configuration);
```

This registers:
- `StreamingOptions` from configuration with validation
- `StreamKeepAliveMonitor` as a singleton + hosted service

---

## Bidirectional RPC

Both sides can be callers and handlers simultaneously. Use `StreamRpcClient` + `StreamRpcDispatcher` on each side:

```csharp
// Server side: handles client requests AND can call client back
var serverDispatcher = StreamRpcDispatcher.Create<IClientToServerRpc>(serverHandler, sendFunc);
var serverRpcClient = new StreamRpcClient(sendFunc, options);
var clientProxy = serverRpcClient.CreateProxy<IServerToClientRpc>();

// On incoming message:
if (msg.Request != null)
    await serverDispatcher.DispatchAsync(msg.Request, ct);
if (msg.Response != null)
    serverRpcClient.TryComplete(msg.Response);

// Server calls client:
await clientProxy.SyncWorldState(new WorldState { ... }, ct);
```

---

## Error Handling Summary

| Scenario | Exception on caller side |
|----------|------------------------|
| Handler returns OK | — (success) |
| Handler throws `StreamRpcException(NotFound, ...)` | `StreamRpcException` with `StatusCode.NotFound` |
| Handler throws `InvalidOperationException` | `StreamRpcException` with `StatusCode.Internal` |
| No response within timeout | `TimeoutException` |
| Caller cancels via `CancellationToken` | `OperationCanceledException` |
| Client disposed / `CancelAll()` | `OperationCanceledException` |
| Client disposed before call | `ObjectDisposedException` |
| Unknown `TypeUrl` on server | `StreamRpcException` with `StatusCode.Unimplemented` |

---

## Project Structure

```
src/GrpcStreamingUtils/
├── Proto/streaming_rpc.proto         # RequestEnvelope, ResponseEnvelope
├── Rpc/
│   ├── StreamRpcClient.cs            # Request/response correlation
│   ├── StreamRpcProxy.cs             # DispatchProxy for typed calls
│   ├── StreamRpcDispatcher.cs        # Auto-dispatch to handler methods
│   └── StreamRpcException.cs         # Typed error with StatusCode
├── Connection/
│   ├── IStreamConnection.cs          # Connection interface
│   ├── StreamConnectionBase.cs       # Abstract base with keep-alive
│   ├── ClientStreamConnection.cs     # Client-side duplex stream
│   ├── ServerStreamConnection.cs     # Server-side stream within gRPC call
│   ├── CloseReason.cs                # Normal / Timeout / Error
│   └── StreamConnectionClosedArgs.cs # Close event args
├── Client/
│   └── ReconnectingStreamClient.cs   # BackgroundService with auto-reconnect
├── KeepAlive/
│   ├── StreamKeepAliveMonitor.cs     # Background ping/timeout monitor
│   └── StreamKeepAliveManager.cs     # Per-connection keep-alive state
├── Configuration/
│   └── StreamingOptions.cs           # All configurable timeouts
├── Exceptions/
│   └── StreamNotEstablishedException.cs
└── Extensions/
    └── ServiceCollectionExtensions.cs # AddStreaming() DI registration
```

## Requirements

- .NET 8.0+
- Google.Protobuf 3.29.6+
- Grpc.AspNetCore 2.70.0+

## License

MIT
