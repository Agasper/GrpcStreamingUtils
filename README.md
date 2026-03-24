# Niarru.GrpcStreamingUtils

Библиотека для .NET 8, упрощающая работу с gRPC bidirectional streams. Берёт на себя рутину: управление жизненным циклом соединения, сериализация записей, keep-alive, автоматический реконнект с backoff, и — поверх всего этого — типизированный RPC-over-stream фреймворк.

## Какую проблему решает

gRPC bidirectional streaming — мощный инструмент, но работать с ним напрямую неудобно:

- **Конкурентная запись** — `IServerStreamWriter` и `IClientStreamWriter` не потокобезопасны. Два одновременных `WriteAsync` — и stream сломан. Приходится вручную оборачивать каждую запись в `SemaphoreSlim`.
- **Keep-alive** — gRPC HTTP/2 ping'и работают на транспортном уровне, но не спасают от «зависших» соединений на уровне приложения. Нужно слать свои ping-сообщения и отслеживать idle timeout.
- **Переподключение** — при обрыве stream на клиенте нужно переподключаться, причём с exponential backoff, чтобы не заDDoSить сервер.
- **Жизненный цикл** — нужно аккуратно обрабатывать закрытие stream: cancellation, gRPC errors, нормальное завершение, dispose. Легко потерять ресурсы или получить мёртвый stream без уведомления.
- **RPC поверх stream** — если хочется request/response семантику поверх bidirectional stream (а не отдельные unary gRPC вызовы), приходится вручную писать корреляцию запрос/ответ, диспетчеризацию, обработку ошибок и таймаутов.

Эта библиотека решает всё вышеперечисленное в двух слоях:

1. **Connection layer** — потокобезопасные обёртки над gRPC stream, keep-alive мониторинг, автоматический реконнект
2. **RPC layer** — типизированный request/response поверх stream через интерфейсы и `DispatchProxy`

Слои независимы: можно использовать только connections без RPC.

---

## Установка

```bash
dotnet add package Niarru.GrpcStreamingUtils
```

---

## Слой 1: Connection Management

### Проблема

Типичный код работы с gRPC duplex stream:

```csharp
// Небезопасно: два одновременных WriteAsync сломают stream
var stream = client.BiDirectionalStream();
await stream.RequestStream.WriteAsync(msg1); // из потока A
await stream.RequestStream.WriteAsync(msg2); // из потока B — гонка!
```

### Решение: StreamConnectionBase

`StreamConnectionBase<TIncoming, TOutgoing>` — абстрактная обёртка, реализующая `IStreamConnection`:

- **Потокобезопасная запись** — все `SendAsync` сериализуются через `SemaphoreSlim`
- **Цикл чтения** — `RunAsync` читает stream до завершения, вызывая `OnMessageReceivedAsync` на каждое сообщение
- **Lifecycle events** — `OnConnectionClosed(CloseReason)` при нормальном закрытии, таймауте или ошибке
- **Интеграция с keep-alive** — автоматически обновляет время последнего сообщения
- **Корректный dispose** — cancel, очистка ресурсов, ожидание завершения записей

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

### ClientStreamConnection — клиентская сторона

Оборачивает `AsyncDuplexStreamingCall<TOutgoing, TIncoming>`:

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

    // Какое сообщение отправлять как ping
    protected override ClientMessage CreatePingMessage()
        => new ClientMessage { Ping = new Ping() };

    // Вызывается на каждое входящее сообщение
    protected override Task OnMessageReceivedAsync(ServerMessage message, CancellationToken ct)
    {
        _onMessage(message);
        return Task.CompletedTask;
    }

    // Опционально: реакция на закрытие
    protected override void OnConnectionClosed(StreamConnectionClosedArgs args)
    {
        if (args.Reason == CloseReason.Error)
            Console.WriteLine($"Connection lost: {args.Exception?.Message}");
    }
}
```

Использование:

```csharp
var stream = grpcClient.BiDirectionalStream(cancellationToken: ct);
var connection = new MyClientConnection(stream, options, TimeProvider.System, logger,
    msg => Console.WriteLine($"Got: {msg}"));

// Запуск чтения (блокирует до закрытия stream)
_ = connection.RunAsync(ct);

// Потокобезопасная отправка из любого потока
await connection.SendAsync(new ClientMessage { Text = "hello" }, ct);
await connection.SendAsync(new ClientMessage { Text = "world" }, ct);

// Закрытие
await connection.CloseAsync(ct);
await connection.DisposeAsync();
```

### ServerStreamConnection — серверная сторона

Оборачивает `IAsyncStreamReader` + `IServerStreamWriter` внутри gRPC метода:

```csharp
public class MyServerConnection : ServerStreamConnection<ClientMessage, ServerMessage>
{
    public MyServerConnection(
        IAsyncStreamReader<ClientMessage> requestStream,
        IServerStreamWriter<ServerMessage> responseStream,
        StreamingOptions options,
        TimeProvider timeProvider,
        CancellationToken grpcCallCancellation,
        ILogger logger)
        : base(requestStream, responseStream, options, timeProvider, grpcCallCancellation, logger)
    { }

    protected override ServerMessage CreatePingMessage()
        => new ServerMessage { Pong = new Pong() };

    protected override async Task OnMessageReceivedAsync(ClientMessage message, CancellationToken ct)
    {
        // Обработка входящих сообщений от клиента
        if (message.Text != null)
        {
            await SendAsync(new ServerMessage { Text = $"Echo: {message.Text}" }, ct);
        }
    }
}
```

Использование в gRPC сервисе:

```csharp
public override async Task BiDirectionalStream(
    IAsyncStreamReader<ClientMessage> requestStream,
    IServerStreamWriter<ServerMessage> responseStream,
    ServerCallContext context)
{
    var connection = new MyServerConnection(
        requestStream, responseStream, _options,
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

Причина закрытия соединения, приходит в `OnConnectionClosed`:

| Значение | Когда |
|----------|-------|
| `Normal` | Stream завершился штатно, cancellation, или gRPC Cancelled |
| `Timeout` | Не используется напрямую (таймаут обрабатывается keep-alive через cancel) |
| `Error` | Необработанное исключение при чтении stream |

---

## Keep-Alive

### Проблема

gRPC HTTP/2 keep-alive работает на уровне TCP-соединения, но не знает о состоянии вашего прикладного протокола. Клиент может «зависнуть» — не слать данные, но TCP-соединение формально живо. Или сервер может не заметить, что клиент давно отключился.

### Решение: StreamKeepAliveMonitor + StreamKeepAliveManager

Каждый `StreamConnectionBase` автоматически создаёт `StreamKeepAliveManager`, который:

- Отслеживает время последнего полученного сообщения
- По расписанию отправляет ping-сообщения (через `CreatePingMessage()`)
- При превышении `IdleTimeoutSeconds` без сообщений — закрывает соединение

`StreamKeepAliveMonitor` — `BackgroundService`, который каждую секунду обходит все зарегистрированные соединения и вызывает их keep-alive логику.

```csharp
// Регистрация в DI (или вручную):
services.AddSingleton<StreamKeepAliveMonitor>();
services.AddHostedService(sp => sp.GetRequiredService<StreamKeepAliveMonitor>());

// Регистрация соединения:
_keepAliveMonitor.Register(connection);   // начать мониторинг
_keepAliveMonitor.Unregister(connection); // прекратить мониторинг
```

Закрытые соединения автоматически удаляются из мониторинга.

---

## Автоматический реконнект

### Проблема

При обрыве gRPC stream на клиенте нужно:
- Переподключиться
- Не заDDoSить сервер при массовом отключении
- Отправить handshake (авторизация, идентификация)
- Пересоздать connection и зарегистрировать его в keep-alive

### Решение: ReconnectingStreamClient

`BackgroundService` с экспоненциальным backoff. Переопределите 3-4 метода, и получите устойчивый к обрывам клиент:

```csharp
public class ChatStreamClient : ReconnectingStreamClient<MyChatConnection, ServerMessage, ClientMessage>
{
    private readonly ChatService.ChatServiceClient _grpcClient;
    private readonly string _userId;

    public ChatStreamClient(
        ChatService.ChatServiceClient grpcClient,
        string userId,
        StreamKeepAliveMonitor keepAliveMonitor,
        StreamingOptions options,
        TimeProvider timeProvider,
        ILogger<ChatStreamClient> logger)
        : base(keepAliveMonitor, options, timeProvider, logger)
    {
        _grpcClient = grpcClient;
        _userId = userId;
    }

    // Имя клиента для логов
    protected override string ClientName => "ChatStreamClient";

    // Как создать gRPC stream
    protected override AsyncDuplexStreamingCall<ClientMessage, ServerMessage> CreateStream(
        CancellationToken ct)
        => _grpcClient.ChatStream(cancellationToken: ct);

    // Как создать connection из stream
    protected override MyChatConnection CreateConnection(
        AsyncDuplexStreamingCall<ClientMessage, ServerMessage> stream)
        => new MyChatConnection(stream, _streamingOptions, TimeProvider, Logger,
            msg => HandleMessage(msg));

    // Опционально: handshake при подключении (до RunAsync)
    protected override async Task SendHandshakeAsync(
        AsyncDuplexStreamingCall<ClientMessage, ServerMessage> stream,
        CancellationToken ct)
    {
        await stream.RequestStream.WriteAsync(
            new ClientMessage { Auth = new Auth { UserId = _userId } }, ct);
    }

    // Опционально: logging scope для всех логов клиента
    protected override IDisposable? CreateLoggingScope()
        => Logger.BeginScope(new Dictionary<string, object> { ["UserId"] = _userId });
}
```

Поведение при обрыве:
1. Stream обрывается → connection dispose → unregister из keep-alive
2. Ждёт `InitialReconnectIntervalSeconds` (по умолчанию 1с)
3. Пробует переподключиться
4. При повторном обрыве — удваивает задержку до `MaxReconnectIntervalSeconds` (по умолчанию 60с)
5. При успешном подключении — сбрасывает backoff

Клиент также предоставляет:
- `Connection` — текущий connection (или null)
- `IsConnected` — есть ли активное соединение
- `SendAsync()` — отправка через текущий connection (бросает `StreamNotEstablishedException` если не подключён)

---

## DI-регистрация и конфигурация

### AddStreaming

```csharp
builder.Services.AddStreaming(builder.Configuration);
```

Регистрирует:
- `StreamingOptions` из секции `"Streaming"` в конфигурации с валидацией
- `StreamKeepAliveMonitor` как singleton + hosted service

### StreamingOptions

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

| Параметр | По умолчанию | Диапазон | Описание |
|----------|-------------|----------|----------|
| `DefaultCommandTimeoutSeconds` | 15 | — | Таймаут RPC-вызовов по умолчанию |
| `IdleTimeoutSeconds` | 30 | — | Закрыть соединение, если нет сообщений |
| `PingIntervalSeconds` | 10 | — | Интервал отправки ping |
| `InitialReconnectIntervalSeconds` | 1 | 1–300 | Первая задержка перед реконнектом |
| `MaxReconnectIntervalSeconds` | 60 | 1–3600 | Максимальная задержка после backoff |

---

## Слой 2: RPC-over-Stream

### Проблема

Bidirectional stream — это поток сообщений без семантики request/response. Если нужен аналог unary RPC, но через stream (чтобы не открывать новые HTTP/2 stream на каждый вызов), приходится вручную:

- Присваивать каждому запросу ID
- На ответе возвращать этот ID
- Сопоставлять ответы с ожидающими вызовами
- Обрабатывать таймауты, отмены, ошибки
- Писать switch/case для диспетчеризации на принимающей стороне

### Решение

Определите интерфейс — получите типизированный клиент и автоматический диспетчер.

### Определение RPC-контракта

Интерфейс, который знают обе стороны:

```csharp
public interface IBattleServerRpc
{
    // Request/Response — возвращает Task<IMessage>
    Task<RoomCreated> CreateRoom(CreateRoom request, CancellationToken ct = default);
    Task<RoomInfo> GetRoom(GetRoomRequest request, CancellationToken ct = default);

    // Fire-and-forget — возвращает Task, сервер отправит подтверждение (status OK)
    Task DeleteRoom(DeleteRoom request, CancellationToken ct = default);

    // CancellationToken опционален
    Task<PingResult> Ping(PingRequest request);
}
```

**Правила:**
- Первый параметр — `IMessage` (protobuf-сообщение)
- Второй параметр (опционально) — `CancellationToken`
- Возвращаемый тип — `Task` или `Task<T>` где `T : IMessage`
- Каждый тип request-сообщения маппится ровно на один метод (по protobuf TypeUrl)
- Методы без параметров **не поддерживаются** — `StreamRpcDispatcher.Create` бросит `ArgumentException` при попытке зарегистрировать такой интерфейс. Если нужен вызов без данных, используйте пустое protobuf-сообщение:

```protobuf
message Empty {}  // или google.protobuf.Empty
```

```csharp
Task<StatusResponse> GetStatus(Empty request, CancellationToken ct = default);
```

### Proto: RequestEnvelope / ResponseEnvelope

Библиотека использует два envelope-сообщения для корреляции:

```protobuf
message RequestEnvelope {
  string request_id = 1;              // GUID, генерируется автоматически
  google.protobuf.Any payload = 2;    // Упакованный IMessage запроса
}

message ResponseEnvelope {
  string in_reply_to_request_id = 1;  // Совпадает с request_id
  int32 status = 2;                   // Grpc.Core.StatusCode (0 = OK)
  string error = 3;                   // Текст ошибки (при status != 0)
  google.protobuf.Any payload = 4;    // Упакованный IMessage ответа
}
```

Встройте их в свой прикладной proto:

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

### Вызывающая сторона: StreamRpcClient + CreateProxy

```csharp
var options = new StreamingOptions { DefaultCommandTimeoutSeconds = 10 };

// Создаём RPC-клиент, передавая функцию отправки
var rpcClient = new StreamRpcClient(
    sendFunc: async (envelope, ct) =>
    {
        await connection.SendAsync(new MyStreamMessage { Request = envelope }, ct);
    },
    options,
    logger);

// Получаем типизированный прокси
var rpc = rpcClient.CreateProxy<IBattleServerRpc>();

// Вызываем как обычные async-методы
var room = await rpc.CreateRoom(new CreateRoom { Name = "Arena" }, ct);
await rpc.DeleteRoom(new DeleteRoom { RoomId = room.RoomId }, ct);
```

В цикле получения сообщений передавайте ответы обратно в клиент:

```csharp
if (msg.Response != null)
    rpcClient.TryComplete(msg.Response);
```

**Жизненный цикл:** один `StreamRpcClient` на одно соединение. При обрыве — `Dispose()` (все pending вызовы отменятся), при переподключении — новый экземпляр.

| Метод | Описание |
|-------|----------|
| `CreateProxy<T>()` | Создаёт `DispatchProxy`, реализующий интерфейс `T` |
| `TryComplete(ResponseEnvelope)` | Сопоставляет ответ с ожидающим вызовом по `InReplyToRequestId`. Возвращает `false` если вызов не найден |
| `CancelAll()` | Отменяет все ожидающие вызовы |
| `Dispose()` | `CancelAll()` + запрет новых вызовов |

### Принимающая сторона: StreamRpcDispatcher

Реализуйте интерфейс:

```csharp
public class BattleServerRpcHandler : IBattleServerRpc
{
    private readonly RoomService _rooms;

    public async Task<RoomCreated> CreateRoom(CreateRoom request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Name))
            throw new StreamRpcException(StatusCode.InvalidArgument, "Room name is required");

        if (!await _rooms.HasCapacityAsync(ct))
            throw new StreamRpcException(StatusCode.ResourceExhausted, "No capacity");

        var room = await _rooms.CreateAsync(request.Name, ct);
        return new RoomCreated { RoomId = room.Id };
    }

    public async Task<RoomInfo> GetRoom(GetRoomRequest request, CancellationToken ct)
    {
        var room = await _rooms.FindAsync(request.RoomId, ct)
            ?? throw new StreamRpcException(StatusCode.NotFound, "Room not found");
        return new RoomInfo { Name = room.Name };
    }

    public async Task DeleteRoom(DeleteRoom request, CancellationToken ct)
    {
        await _rooms.DeleteAsync(request.RoomId, ct);
        // Для void-методов достаточно вернуть Task — клиент получит status OK
    }

    public Task<PingResult> Ping(PingRequest request)
    {
        return Task.FromResult(new PingResult { Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
    }
}
```

Создайте диспетчер и маршрутизируйте входящие запросы:

```csharp
var dispatcher = StreamRpcDispatcher.Create<IBattleServerRpc>(
    handler: new BattleServerRpcHandler(),
    sendFunc: async (envelope, ct) =>
    {
        await connection.SendAsync(new MyStreamMessage { Response = envelope }, ct);
    },
    logger);

// В цикле получения сообщений:
if (msg.Request != null)
    await dispatcher.DispatchAsync(msg.Request, ct);
```

`Create<T>()` при вызове:
1. Сканирует все методы интерфейса
2. Валидирует сигнатуры (fail-fast при ошибках)
3. Строит маппинг `TypeUrl → обработчик` через protobuf-дескрипторы
4. Бросает `ArgumentException` при невалидном контракте (не-IMessage параметр, метод без параметров, неправильный return type)

### Обработка ошибок

**На стороне обработчика:**

| Что бросает handler | ResponseEnvelope.Status | ResponseEnvelope.Error |
|---------------------|------------------------|----------------------|
| Возврат результата | `OK (0)` | — |
| `StreamRpcException(NotFound, "...")` | `NotFound (5)` | Текст из исключения |
| `StreamRpcException(ResourceExhausted, "...")` | `ResourceExhausted (8)` | Текст из исключения |
| `InvalidOperationException("...")` | `Internal (13)` | Текст из исключения |
| Любой другой `Exception` | `Internal (13)` | `ex.Message` |

**На стороне диспетчера (до вызова handler):**

| Ситуация | StatusCode |
|----------|-----------|
| `TypeUrl` не найден в маппинге | `Unimplemented (12)` |
| Не удалось распарсить payload | `InvalidArgument (3)` |
| Payload is null | `InvalidArgument (3)` |

**На стороне вызывающего:**

| Сценарий | Исключение |
|----------|-----------|
| Ответ с `status != OK` | `StreamRpcException` с соответствующим `StatusCode` |
| Нет ответа в течение таймаута | `TimeoutException` |
| Вызов отменён через `CancellationToken` | `OperationCanceledException` |
| Клиент disposed / `CancelAll()` | `OperationCanceledException` |
| Вызов после `Dispose()` | `ObjectDisposedException` |

```csharp
try
{
    var result = await rpc.CreateRoom(new CreateRoom { Name = "Arena" }, ct);
}
catch (StreamRpcException ex) when (ex.StatusCode == StatusCode.ResourceExhausted)
{
    logger.LogWarning("Сервер перегружен: {Error}", ex.Message);
}
catch (StreamRpcException ex)
{
    logger.LogError("RPC ошибка {StatusCode}: {Error}", ex.StatusCode, ex.Message);
}
catch (TimeoutException)
{
    logger.LogError("RPC вызов не дождался ответа");
}
```

### Двунаправленный RPC

Обе стороны могут одновременно быть и вызывающей, и принимающей. Создайте `StreamRpcClient` + `StreamRpcDispatcher` на каждой стороне с разными интерфейсами:

```csharp
// Контракты
public interface IClientToServerRpc { ... }  // клиент вызывает, сервер обрабатывает
public interface IServerToClientRpc { ... }  // сервер вызывает, клиент обрабатывает

// На сервере:
var dispatcher = StreamRpcDispatcher.Create<IClientToServerRpc>(serverHandler, sendResponse);
var rpcClient = new StreamRpcClient(sendRequest, options);
var clientProxy = rpcClient.CreateProxy<IServerToClientRpc>();

// Маршрутизация входящих:
if (msg.Request != null)
    await dispatcher.DispatchAsync(msg.Request, ct);  // запрос от клиента
if (msg.Response != null)
    rpcClient.TryComplete(msg.Response);               // ответ клиента на наш запрос

// Вызов клиента с сервера:
await clientProxy.SyncWorldState(new WorldState { ... }, ct);
```

---

## Полный пример: ReconnectingStreamClient + RPC

Клиент, который автоматически переподключается и предоставляет typed RPC:

```csharp
public class BattleStreamClient : ReconnectingStreamClient<BattleClientConnection, ServerMessage, ClientMessage>
{
    private readonly BattleService.BattleServiceClient _grpcClient;
    private readonly StreamingOptions _options;
    private StreamRpcClient? _rpcClient;

    public IBattleServerRpc? Rpc { get; private set; }

    public BattleStreamClient(
        BattleService.BattleServiceClient grpcClient,
        StreamKeepAliveMonitor monitor,
        StreamingOptions options,
        TimeProvider timeProvider,
        ILogger<BattleStreamClient> logger)
        : base(monitor, options, timeProvider, logger)
    {
        _grpcClient = grpcClient;
        _options = options;
    }

    protected override string ClientName => "BattleStreamClient";

    protected override AsyncDuplexStreamingCall<ClientMessage, ServerMessage> CreateStream(
        CancellationToken ct)
        => _grpcClient.GameStream(cancellationToken: ct);

    protected override BattleClientConnection CreateConnection(
        AsyncDuplexStreamingCall<ClientMessage, ServerMessage> stream)
    {
        _rpcClient?.Dispose();
        _rpcClient = new StreamRpcClient(
            async (env, ct) => await SendAsync(new ClientMessage { Request = env }, ct),
            _options, Logger);
        Rpc = _rpcClient.CreateProxy<IBattleServerRpc>();

        return new BattleClientConnection(stream, _options, TimeProvider, Logger,
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

Использование:

```csharp
// DI
services.AddStreaming(configuration);
services.AddSingleton<BattleStreamClient>();
services.AddHostedService(sp => sp.GetRequiredService<BattleStreamClient>());

// В коде
var room = await battleClient.Rpc!.CreateRoom(new CreateRoom { Name = "Arena" }, ct);
```

---

## Структура проекта

```
src/GrpcStreamingUtils/
├── Connection/
│   ├── IStreamConnection.cs          — интерфейс соединения
│   ├── StreamConnectionBase.cs       — абстрактная база с keep-alive и write lock
│   ├── ClientStreamConnection.cs     — клиентская обёртка над AsyncDuplexStreamingCall
│   ├── ServerStreamConnection.cs     — серверная обёртка над IAsyncStreamReader/Writer
│   ├── CloseReason.cs                — Normal / Timeout / Error
│   └── StreamConnectionClosedArgs.cs — аргументы события закрытия
├── Client/
│   └── ReconnectingStreamClient.cs   — BackgroundService с auto-reconnect
├── KeepAlive/
│   ├── StreamKeepAliveMonitor.cs     — фоновый мониторинг всех соединений
│   └── StreamKeepAliveManager.cs     — per-connection keep-alive состояние
├── Rpc/
│   ├── StreamRpcClient.cs            — корреляция запрос/ответ
│   ├── StreamRpcProxy.cs             — DispatchProxy для typed вызовов
│   ├── StreamRpcDispatcher.cs        — автодиспетчер входящих запросов
│   └── StreamRpcException.cs         — ошибка с gRPC StatusCode
├── Configuration/
│   └── StreamingOptions.cs           — все таймауты и настройки
├── Exceptions/
│   └── StreamNotEstablishedException.cs
├── Extensions/
│   └── ServiceCollectionExtensions.cs — AddStreaming()
└── Proto/
    └── streaming_rpc.proto           — RequestEnvelope, ResponseEnvelope
```

## Требования

- .NET 8.0+
- Google.Protobuf 3.29.6+
- Grpc.AspNetCore 2.70.0+

## Лицензия

MIT
