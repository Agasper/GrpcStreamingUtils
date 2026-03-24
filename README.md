# Niarru.GrpcStreamingUtils

A .NET 8 library that simplifies working with gRPC bidirectional streams. Handles connection lifecycle, write serialization, keep-alive, automatic reconnection with backoff, and — on top of all that — a typed RPC-over-stream framework.

## What Problem Does It Solve

gRPC bidirectional streaming is powerful, but working with it directly is tedious:

- **Concurrent writes** — `IServerStreamWriter` and `IClientStreamWriter` are not thread-safe. Two simultaneous `WriteAsync` calls will corrupt the stream. You have to manually wrap every write in a `SemaphoreSlim`.
- **Keep-alive** — gRPC HTTP/2 pings work at the transport level but won't detect "stuck" connections at the application level. You need to send your own ping messages and track idle timeouts.
- **Reconnection** — when a client-side stream breaks, you need to reconnect with exponential backoff to avoid overwhelming the server.
- **Lifecycle** — properly handling stream closure (cancellation, gRPC errors, normal completion, dispose) is error-prone. It's easy to leak resources or end up with a dead stream and no notification.
- **RPC over stream** — if you want request/response semantics over a bidirectional stream (instead of separate unary gRPC calls), you have to manually implement request/response correlation, dispatching, error handling, and timeouts.

This library addresses all of the above in two layers:

1. **Connection layer** — thread-safe wrappers over gRPC streams, keep-alive monitoring, automatic reconnection
2. **RPC layer** — typed request/response over stream via interfaces and `DispatchProxy`

The layers are independent: you can use connections without RPC.

---

## Installation

```bash
dotnet add package Niarru.GrpcStreamingUtils
```

---

## Layer 1: Connection Management

### The Problem

Typical code for working with a gRPC duplex stream:

```csharp
// Unsafe: two concurrent WriteAsync calls will corrupt the stream
var stream = client.BiDirectionalStream();
await stream.RequestStream.WriteAsync(msg1); // from thread A
await stream.RequestStream.WriteAsync(msg2); // from thread B — race condition!
```

### Solution: StreamConnectionBase

`StreamConnectionBase<TIncoming, TOutgoing>` — an abstract wrapper implementing `IStreamConnection`:

- **Thread-safe writes** — all `SendAsync` calls are serialized via `SemaphoreSlim`
- **Read loop** — `RunAsync` reads the stream until completion, calling `OnMessageReceivedAsync` for each message
- **Lifecycle events** — `OnConnectionClosed(CloseReason)` on normal close, timeout, or error
- **Keep-alive integration** — automatically updates last message time
- **Proper dispose** — cancel, resource cleanup, waiting for pending writes

```csharp
public interface IStreamConnection<TIncoming, TOutgoing> : IAsyncDisposable
{
    Guid ConnectionId { get; }
    CancellationToken ConnectionClosed { get; }
    Task RunAsync(CancellationToken cancellationToken);
    Task SendAsync(TOutgoing message, CancellationToken cancellationToken);
    Task CloseAsync(CancellationToken cancellationToken);
}
```

### ClientStreamConnection — Client Side

Wraps `AsyncDuplexStreamingCall<TOutgoing, TIncoming>`:

```csharp
public class MyClientConnection : ClientStreamConnection<ServerMessage, ClientMessage>
{
    private readonly Action<ServerMessage> _onMessage;

    public MyClientConnection(
        AsyncDuplexStreamingCall<ClientMessage, ServerMessage> stream,
        TimeProvider timeProvider,
        ILogger logger,
        Action<ServerMessage> onMessage)
        : base(stream, timeProvider, logger, pingInterval: TimeSpan.FromSeconds(10))
    {
        _onMessage = onMessage;
    }

    // Which message to send as a ping
    protected override ClientMessage CreatePingMessage()
        => new ClientMessage { Ping = new Ping() };

    // Called for every incoming message
    protected override Task OnMessageReceivedAsync(ServerMessage message, CancellationToken ct)
    {
        _onMessage(message);
        return Task.CompletedTask;
    }

    // Optional: react to connection close
    protected override void OnConnectionClosed(StreamConnectionClosedArgs args)
    {
        if (args.Reason == CloseReason.Error)
            Console.WriteLine($"Connection lost: {args.Exception?.Message}");
    }
}
```

Usage:

```csharp
var stream = grpcClient.BiDirectionalStream(cancellationToken: ct);
var connection = new MyClientConnection(stream, TimeProvider.System, logger,
    msg => Console.WriteLine($"Got: {msg}"));

// Start reading (blocks until stream closes)
_ = connection.RunAsync(ct);

// Thread-safe sending from any thread
await connection.SendAsync(new ClientMessage { Text = "hello" }, ct);
await connection.SendAsync(new ClientMessage { Text = "world" }, ct);

// Close
await connection.CloseAsync(ct);
await connection.DisposeAsync();
```

### ServerStreamConnection — Server Side

Wraps `IAsyncStreamReader` + `IServerStreamWriter` inside a gRPC method:

```csharp
public class MyServerConnection : ServerStreamConnection<ClientMessage, ServerMessage>
{
    public MyServerConnection(
        IAsyncStreamReader<ClientMessage> requestStream,
        IServerStreamWriter<ServerMessage> responseStream,
        TimeProvider timeProvider,
        CancellationToken grpcCallCancellation,
        ILogger logger)
        : base(requestStream, responseStream, timeProvider, grpcCallCancellation, logger,
            idleTimeout: TimeSpan.FromSeconds(30))
    { }

    protected override ServerMessage CreatePingMessage()
        => new ServerMessage { Pong = new Pong() };

    protected override async Task OnMessageReceivedAsync(ClientMessage message, CancellationToken ct)
    {
        if (message.Text != null)
        {
            await SendAsync(new ServerMessage { Text = $"Echo: {message.Text}" }, ct);
        }
    }
}
```

Usage in a gRPC service:

```csharp
public override async Task BiDirectionalStream(
    IAsyncStreamReader<ClientMessage> requestStream,
    IServerStreamWriter<ServerMessage> responseStream,
    ServerCallContext context)
{
    var connection = new MyServerConnection(
        requestStream, responseStream,
        TimeProvider.System, context.CancellationToken, _logger);

    _keepAliveMonitor.Register(connection);
    try
    {
        await connection.RunAsync(context.CancellationToken);
    }
    finally
    {
        _keepAliveMonitor.Unregister(connection);
        await connection.DisposeAsync();
    }
}
```

### CloseReason

Close reason, passed to `OnConnectionClosed`:

| Value | When |
|-------|------|
| `Normal` | Stream completed normally, cancellation, or gRPC Cancelled |
| `Timeout` | Not used directly (timeout is handled by keep-alive via cancel) |
| `Error` | Unhandled exception while reading the stream |

---

## Keep-Alive

### The Problem

gRPC HTTP/2 keep-alive works at the TCP connection level but knows nothing about your application protocol. A client can "hang" — stop sending data while the TCP connection remains technically alive. Or the server may not notice that a client disconnected long ago.

### Solution: StreamKeepAliveMonitor + StreamKeepAliveManager

Each `StreamConnectionBase` automatically creates a `StreamKeepAliveManager` that:

- Tracks the time of the last received message
- Periodically sends ping messages (via `CreatePingMessage()`)
- Closes the connection when `IdleTimeoutSeconds` elapses without messages

`StreamKeepAliveMonitor` — a `BackgroundService` that checks all registered connections every second and runs their keep-alive logic.

```csharp
// DI registration (or manually):
services.AddSingleton<StreamKeepAliveMonitor>();
services.AddHostedService(sp => sp.GetRequiredService<StreamKeepAliveMonitor>());

// Register a connection:
_keepAliveMonitor.Register(connection);   // start monitoring
_keepAliveMonitor.Unregister(connection); // stop monitoring
```

Closed connections are automatically removed from monitoring.

---

## Automatic Reconnection

### The Problem

When a gRPC stream breaks on the client side, you need to:
- Reconnect
- Avoid overwhelming the server during mass disconnects
- Send a handshake (auth, identification)
- Recreate the connection and register it with keep-alive

### Solution: ReconnectingStreamClient

A `BackgroundService` with exponential backoff. Override 3-4 methods and get a resilient client:

```csharp
public class NotificationStreamClient
    : ReconnectingStreamClient<MyClientConnection, ServerMessage, ClientMessage>
{
    private readonly NotificationService.NotificationServiceClient _grpcClient;
    private readonly string _subscriberId;

    public NotificationStreamClient(
        NotificationService.NotificationServiceClient grpcClient,
        string subscriberId,
        StreamKeepAliveMonitor keepAliveMonitor,
        TimeProvider timeProvider,
        ILogger<NotificationStreamClient> logger)
        : base(keepAliveMonitor, timeProvider, logger)
    {
        _grpcClient = grpcClient;
        _subscriberId = subscriberId;
    }

    // Client name for logs
    protected override string ClientName => "NotificationStreamClient";

    // How to create the gRPC stream
    protected override AsyncDuplexStreamingCall<ClientMessage, ServerMessage> CreateStream(
        CancellationToken ct)
        => _grpcClient.Subscribe(cancellationToken: ct);

    // How to create a connection from the stream
    protected override MyClientConnection CreateConnection(
        AsyncDuplexStreamingCall<ClientMessage, ServerMessage> stream)
        => new MyClientConnection(stream, TimeProvider, Logger,
            msg => HandleMessage(msg));

    // Optional: handshake on connect (before RunAsync)
    protected override async Task SendHandshakeAsync(
        AsyncDuplexStreamingCall<ClientMessage, ServerMessage> stream,
        CancellationToken ct)
    {
        await stream.RequestStream.WriteAsync(
            new ClientMessage { Auth = new Auth { SubscriberId = _subscriberId } }, ct);
    }

    // Optional: logging scope for all client logs
    protected override IDisposable? CreateLoggingScope()
        => Logger.BeginScope(new Dictionary<string, object> { ["SubscriberId"] = _subscriberId });
}
```

Behavior on disconnect:
1. Stream breaks → connection dispose → unregister from keep-alive
2. Waits `InitialReconnectIntervalSeconds` (default 1s)
3. Attempts to reconnect
4. On repeated failure — multiplies delay by backoff multiplier (default 2x) up to max interval (default 60s)
5. On successful connection — resets backoff

The client also provides:
- `Connection` — current connection (or null)
- `IsConnected` — whether there is an active connection
- `SendAsync()` — sends through the current connection (throws `StreamNotEstablishedException` if not connected)

---

## Layer 2: RPC-over-Stream

### The Problem

A bidirectional stream is a message flow with no request/response semantics. If you need a unary RPC analogue over a stream (to avoid opening a new HTTP/2 stream per call), you have to manually:

- Assign a unique ID to each request
- Return that ID in the response
- Correlate responses with pending calls
- Handle timeouts, cancellations, errors
- Write switch/case dispatching on the receiving side

### Solution

Define an interface — get a typed client and an automatic dispatcher.

### Defining the RPC Contract

An interface known to both sides:

```csharp
public interface IOrderService
{
    // Request/Response — returns Task<IMessage>
    Task<PlaceOrderResponse> PlaceOrder(PlaceOrderRequest request, CancellationToken ct = default);
    Task<OrderInfo> GetOrder(GetOrderRequest request, CancellationToken ct = default);

    // Fire-and-forget — returns Task, server sends an acknowledgement (status OK)
    Task CancelOrder(CancelOrderRequest request, CancellationToken ct = default);

    // CancellationToken is optional
    Task<HealthCheckResponse> HealthCheck(HealthCheckRequest request);
}
```

**Rules:**
- First parameter — `IMessage` (protobuf message)
- Second parameter (optional) — `CancellationToken`
- Return type — `Task` or `Task<T>` where `T : IMessage`
- Each request message type maps to exactly one method (by protobuf TypeUrl)
- Methods without parameters **are not supported** — `StreamRpcDispatcher.Create` will throw `ArgumentException`. If you need a call with no data, use an empty protobuf message:

```protobuf
message Empty {}  // or google.protobuf.Empty
```

```csharp
Task<StatusResponse> GetStatus(Empty request, CancellationToken ct = default);
```

### Proto: RequestEnvelope / ResponseEnvelope

The library uses two envelope messages for correlation:

```protobuf
message RequestEnvelope {
  string request_id = 1;              // GUID, generated automatically
  google.protobuf.Any payload = 2;    // Packed IMessage request
}

message ResponseEnvelope {
  string in_reply_to_request_id = 1;  // Matches RequestEnvelope.request_id
  int32 status = 2;                   // Grpc.Core.StatusCode as int (0 = OK)
  string error = 3;                   // Error text (when status != 0)
  google.protobuf.Any payload = 4;    // Packed IMessage response
}
```

Embed them in your application-level proto:

```protobuf
import "Proto/streaming_rpc.proto";

message MyStreamMessage {
  oneof content {
    RequestEnvelope request = 1;
    ResponseEnvelope response = 2;
    Ping ping = 3;
    // ...
  }
}
```

### Caller Side: StreamRpcClient + CreateProxy

```csharp
// Create an RPC client with a send function and default timeout
var rpcClient = new StreamRpcClient(
    sendFunc: async (envelope, ct) =>
    {
        await connection.SendAsync(new MyStreamMessage { Request = envelope }, ct);
    },
    logger);

// Get a typed proxy
var orders = rpcClient.CreateProxy<IOrderService>();

// Call as regular async methods
var result = await orders.PlaceOrder(new PlaceOrderRequest { ItemId = "sku-42", Quantity = 2 }, ct);
Console.WriteLine($"Order placed: {result.OrderId}");

await orders.CancelOrder(new CancelOrderRequest { OrderId = result.OrderId }, ct);
```

In the message receive loop, feed responses back to the client:

```csharp
if (msg.Response != null)
    rpcClient.TryComplete(msg.Response);
```

**Lifecycle:** one `StreamRpcClient` per connection. On disconnect — `Dispose()` (all pending calls get cancelled), on reconnect — create a new instance.

| Method | Description |
|--------|-------------|
| `CreateProxy<T>()` | Creates a `DispatchProxy` implementing interface `T` |
| `TryComplete(ResponseEnvelope)` | Matches a response to a pending call by `InReplyToRequestId`. Returns `false` if no matching call found |
| `CancelAll()` | Cancels all pending calls |
| `Dispose()` | `CancelAll()` + prevents new calls |

### Handler Side: StreamRpcDispatcher

Implement the interface:

```csharp
public class OrderServiceHandler : IOrderService
{
    private readonly IOrderRepository _orders;

    public async Task<PlaceOrderResponse> PlaceOrder(PlaceOrderRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.ItemId))
            throw new StreamRpcException(StatusCode.InvalidArgument, "ItemId is required");

        if (!await _orders.IsAvailableAsync(request.ItemId, ct))
            throw new StreamRpcException(StatusCode.FailedPrecondition, "Item is out of stock");

        var order = await _orders.CreateAsync(request.ItemId, request.Quantity, ct);
        return new PlaceOrderResponse { OrderId = order.Id };
    }

    public async Task<OrderInfo> GetOrder(GetOrderRequest request, CancellationToken ct)
    {
        var order = await _orders.FindAsync(request.OrderId, ct)
            ?? throw new StreamRpcException(StatusCode.NotFound, "Order not found");
        return new OrderInfo { OrderId = order.Id, Status = order.Status };
    }

    public async Task CancelOrder(CancelOrderRequest request, CancellationToken ct)
    {
        await _orders.CancelAsync(request.OrderId, ct);
        // For void methods, just return Task — the caller receives status OK
    }

    public Task<HealthCheckResponse> HealthCheck(HealthCheckRequest request)
    {
        return Task.FromResult(new HealthCheckResponse
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
    }
}
```

Create a dispatcher and route incoming requests:

```csharp
var dispatcher = StreamRpcDispatcher.Create<IOrderService>(
    handler: new OrderServiceHandler(),
    sendFunc: async (envelope, ct) =>
    {
        await connection.SendAsync(new MyStreamMessage { Response = envelope }, ct);
    },
    logger);

// In the message receive loop:
if (msg.Request != null)
    await dispatcher.DispatchAsync(msg.Request, ct);
```

`Create<T>()` at creation time:
1. Scans all methods on the interface
2. Validates signatures (fail-fast on errors)
3. Builds a `TypeUrl → handler` mapping via protobuf descriptors
4. Throws `ArgumentException` on invalid contract (non-IMessage parameter, parameterless method, wrong return type)

### Error Handling

**Handler side:**

| Handler behavior | ResponseEnvelope.Status | ResponseEnvelope.Error |
|------------------|------------------------|----------------------|
| Returns result | `OK (0)` | — |
| `StreamRpcException(NotFound, "...")` | `NotFound (5)` | Exception message |
| `StreamRpcException(ResourceExhausted, "...")` | `ResourceExhausted (8)` | Exception message |
| `InvalidOperationException("...")` | `Internal (13)` | Exception message |
| Any other `Exception` | `Internal (13)` | `ex.Message` |

**Dispatcher side (before handler invocation):**

| Situation | StatusCode |
|-----------|-----------|
| `TypeUrl` not found in mapping | `Unimplemented (12)` |
| Failed to parse payload | `InvalidArgument (3)` |
| Payload is null | `InvalidArgument (3)` |

**Caller side:**

| Scenario | Exception |
|----------|----------|
| Response with `status != OK` | `StreamRpcException` with matching `StatusCode` |
| No response within timeout | `TimeoutException` |
| Call cancelled via `CancellationToken` | `OperationCanceledException` |
| Client disposed / `CancelAll()` | `OperationCanceledException` |
| Call after `Dispose()` | `ObjectDisposedException` |

```csharp
try
{
    var result = await orders.PlaceOrder(new PlaceOrderRequest { ItemId = "sku-42" }, ct);
}
catch (StreamRpcException ex) when (ex.StatusCode == StatusCode.FailedPrecondition)
{
    logger.LogWarning("Cannot place order: {Error}", ex.Message);
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

### Bidirectional RPC

Both sides can be callers and handlers simultaneously. Create `StreamRpcClient` + `StreamRpcDispatcher` on each side with different interfaces:

```csharp
// Contracts
public interface IClientToServerRpc { ... }  // client calls, server handles
public interface IServerToClientRpc { ... }  // server calls, client handles

// On the server:
var dispatcher = StreamRpcDispatcher.Create<IClientToServerRpc>(serverHandler, sendResponse);
var rpcClient = new StreamRpcClient(sendRequest, options);
var clientProxy = rpcClient.CreateProxy<IServerToClientRpc>();

// Route incoming messages:
if (msg.Request != null)
    await dispatcher.DispatchAsync(msg.Request, ct);  // request from client
if (msg.Response != null)
    rpcClient.TryComplete(msg.Response);               // client's response to our request

// Call client from server:
await clientProxy.PushNotification(new Notification { Text = "Your order shipped" }, ct);
```

---

## Full Example: ReconnectingStreamClient + RPC

A client that auto-reconnects and exposes typed RPC:

```csharp
public class OrderStreamClient
    : ReconnectingStreamClient<MyClientConnection, ServerMessage, ClientMessage>
{
    private readonly OrderService.OrderServiceClient _grpcClient;
    private StreamRpcClient? _rpcClient;

    public IOrderService? Orders { get; private set; }

    public OrderStreamClient(
        OrderService.OrderServiceClient grpcClient,
        StreamKeepAliveMonitor monitor,
        TimeProvider timeProvider,
        ILogger<OrderStreamClient> logger)
        : base(monitor, timeProvider, logger)
    {
        _grpcClient = grpcClient;
    }

    protected override string ClientName => "OrderStreamClient";

    protected override AsyncDuplexStreamingCall<ClientMessage, ServerMessage> CreateStream(
        CancellationToken ct)
        => _grpcClient.OrderStream(cancellationToken: ct);

    protected override MyClientConnection CreateConnection(
        AsyncDuplexStreamingCall<ClientMessage, ServerMessage> stream)
    {
        _rpcClient?.Dispose();
        _rpcClient = new StreamRpcClient(
            async (env, ct) => await SendAsync(new ClientMessage { Request = env }, ct),
            defaultTimeout: TimeSpan.FromSeconds(10), Logger);
        Orders = _rpcClient.CreateProxy<IOrderService>();

        return new MyClientConnection(stream, TimeProvider, Logger,
            msg =>
            {
                if (msg.Response != null)
                    _rpcClient.TryComplete(msg.Response);
            });
    }

    public override async ValueTask DisposeAsync()
    {
        _rpcClient?.Dispose();
        await base.DisposeAsync();
    }
}
```

Usage:

```csharp
// DI
services.AddSingleton<StreamKeepAliveMonitor>();
services.AddHostedService(sp => sp.GetRequiredService<StreamKeepAliveMonitor>());
services.AddSingleton<OrderStreamClient>();
services.AddHostedService(sp => sp.GetRequiredService<OrderStreamClient>());

// In code
var result = await orderClient.Orders!.PlaceOrder(
    new PlaceOrderRequest { ItemId = "sku-42", Quantity = 1 }, ct);
```

## Requirements

- .NET 8.0+
- Google.Protobuf 3.29.6+
- Grpc.AspNetCore 2.70.0+

## License

MIT
