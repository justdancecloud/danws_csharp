# DanWebSocket - C# Library

A C# client and server library for the [dan-websocket](https://github.com/justdancecloud/danws_typescript) real-time state synchronization protocol (DanProtocol v3.5).

## What is it?

DanWebSocket provides a lightweight binary WebSocket client and server for real-time state synchronization. It is designed for Unity game engine and .NET 6+ applications that need low-latency, bandwidth-efficient state synchronization.

## Why use it?

- **Binary protocol** -- minimal bandwidth (a boolean update is ~13 bytes vs ~30+ for JSON)
- **Auto-flatten** -- nested objects/arrays are automatically expanded into dot-path keys
- **Variable-length encoding** -- integers and doubles use 1-3 bytes instead of fixed 4-8
- **Client + Server** -- both client and server in one library, no external dependencies
- **4 server modes** -- Broadcast, Principal, SessionTopic, SessionPrincipalTopic
- **Reconnection** -- built-in exponential backoff with jitter (client)
- **Heartbeat** -- automatic connection health monitoring
- **Topic subscriptions** -- subscribe to server-defined data topics with parameters
- **Authentication** -- optional token-based auth with timeout enforcement
- **Unity compatible** -- targets `netstandard2.1` with no external dependencies

## Installation

### Unity

1. Copy the `src/DanWebSocket/` folder into your Unity project's `Assets/Plugins/` directory
2. Or add as a Git submodule and reference the `.csproj`

### .NET Project

```xml
<ProjectReference Include="path/to/DanWebSocket.csproj" />
```

## Server Quick Start

### Broadcast Mode

The simplest mode -- all clients see the same state.

```csharp
using DanWebSocket.Api;

// Create server on port 9000
var server = new DanWebSocketServer(9000, "/", Mode.Broadcast);

// Set state -- all connected clients receive updates
server.Set("score", 42);
server.Set("status", "running");

// Read current value
var score = server.Get("score"); // 42

// List all keys
var keys = server.Keys; // ["score", "status"]

// Clear a key or all keys
server.Clear("score");
server.Clear(); // clears everything

// Events
server.OnConnection(session => {
    Console.WriteLine($"Client connected: {session.Id}");
});

// Shut down
server.Close();
```

### Principal Mode

Per-user state isolation with authentication.

```csharp
var server = new DanWebSocketServer(9000, "/", Mode.Principal);
server.EnableAuthorization(true, timeoutMs: 5000);

// Handle auth requests from clients
server.OnAuthorize((clientUuid, token) => {
    // Validate the token (your logic)
    if (IsValid(token)) {
        string username = GetUsername(token);
        server.Authorize(clientUuid, token, username);
    } else {
        server.Reject(clientUuid, "Invalid token");
    }
});

// Set per-principal state
server.Principal("alice").Set("role", "admin");
server.Principal("alice").Set("inventory", "sword");

server.Principal("bob").Set("role", "viewer");
```

### Session Topic Mode

Dynamic topic subscriptions with per-session data.

```csharp
var server = new DanWebSocketServer(9000, "/", Mode.SessionTopic);

server.Topic.OnSubscribe((session, topic) => {
    Console.WriteLine($"Client subscribed to: {topic.Name}");
    Console.WriteLine($"Params: {string.Join(", ", topic.Params)}");

    // Set topic-scoped data for this session
    topic.Payload.Set("title", "Game Room");
    topic.Payload.Set("players", 4);

    // Set up periodic updates
    topic.SetDelayedTask(1000); // fires every 1s
    topic.SetCallback((eventType, t, s) => {
        if (eventType == TopicEventType.DelayedTask) {
            t.Payload.Set("tick", DateTime.UtcNow.Ticks);
        }
    });
});

server.Topic.OnUnsubscribe((session, topic) => {
    Console.WriteLine($"Client unsubscribed from: {topic.Name}");
});
```

### Session Principal Topic Mode

Combines authentication with dynamic topics.

```csharp
var server = new DanWebSocketServer(9000, "/", Mode.SessionPrincipalTopic);
server.EnableAuthorization(true);

server.OnAuthorize((uuid, token) => {
    server.Authorize(uuid, token, "player1");
});

server.Topic.OnSubscribe((session, topic) => {
    topic.Payload.Set("game", "started");
});
```

### Rate Limiting and Metrics

```csharp
server.SetMaxConnections(100);    // 0 = unlimited
server.SetMaxFramesPerSec(60);    // 0 = unlimited

var metrics = server.GetMetrics();
Console.WriteLine($"Active: {metrics.ActiveSessions}");
Console.WriteLine($"Pending: {metrics.PendingSessions}");
Console.WriteLine($"Frames In: {metrics.FramesIn}");
Console.WriteLine($"Frames Out: {metrics.FramesOut}");
```

## Client Quick Start

```csharp
using DanWebSocket.Api;

// Create client
var client = new DanWebSocketClient("ws://localhost:9000");

// Listen for events
client.OnReady += () => {
    Console.WriteLine("Connected and ready!");
    Console.WriteLine($"Temperature: {client.Get("sensor.temperature")}");
};

client.OnReceive += (key, value) => {
    Console.WriteLine($"{key} = {value}");
};

client.OnUpdate += () => {
    // Fires once per server batch -- ideal for rendering
    Console.WriteLine("State updated");
};

client.OnError += (err) => {
    Console.WriteLine($"Error [{err.Code}]: {err.Message}");
};

// Connect
client.Connect();
```

## Topic Subscriptions (Client)

```csharp
// Subscribe to a topic with parameters
client.Subscribe("board", new Dictionary<string, object?> {
    { "roomId", "abc123" }
});

// Get a topic handle for scoped access
var board = client.Topic("board");
board.OnReceive((key, value) => {
    Console.WriteLine($"Board {key} = {value}");
});

// Read topic data
var title = board.Get("title");
var items = board.Keys;

// Unsubscribe
client.Unsubscribe("board");
```

## Array Access (Client)

```csharp
// Access flattened arrays
var scores = client.Array("scores");
Console.WriteLine($"Length: {scores.Length}");
Console.WriteLine($"First: {scores[0]}");

// Convert to list
var list = scores.ToList();
```

## Authentication (Client)

```csharp
client.OnConnect += () => {
    client.Authorize("my-auth-token");
};
```

## Reconnection (Client)

Reconnection is enabled by default with exponential backoff:

```csharp
var client = new DanWebSocketClient("ws://localhost:9000", new ClientOptions {
    Reconnect = new ReconnectOptions {
        Enabled = true,
        MaxRetries = 10,
        BaseDelay = 1000,
        MaxDelay = 30000,
        BackoffMultiplier = 2.0,
        Jitter = true
    }
});

client.OnReconnecting += (attempt, delay) => {
    Console.WriteLine($"Reconnecting attempt {attempt} in {delay}ms...");
};

client.OnReconnect += () => {
    Console.WriteLine("Reconnected!");
};
```

## Supported Data Types

| Type | C# Type |
|------|---------|
| Null | `null` |
| Bool | `bool` |
| Uint8 | `int` (0-255) |
| Uint16 | `int` (0-65535) |
| Uint32 | `int` |
| Uint64 | `long` |
| Int32 | `int` |
| Int64 | `long` |
| Float32 | `double` |
| Float64 | `double` |
| String | `string` |
| Binary | `byte[]` |
| Timestamp | `DateTimeOffset` |
| VarInteger | `int` |
| VarDouble | `double` |
| VarFloat | `double` |

## Protocol Compatibility

This library implements DanProtocol v3.5 and is wire-compatible with:
- [dan-websocket (TypeScript)](https://www.npmjs.com/package/dan-websocket)
- [dan-websocket (Java)](https://central.sonatype.com/artifact/io.github.justdancecloud/dan-websocket)

## License

MIT
