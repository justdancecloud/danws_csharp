# DanWebSocket - C# Client Library

A C# client library for the [dan-websocket](https://github.com/justdancecloud/danws_typescript) real-time state synchronization protocol (DanProtocol v3.5).

## What is it?

DanWebSocket provides a lightweight binary WebSocket client for receiving real-time state updates from a DanProtocol server. It is designed for Unity game engine and .NET 6+ applications that need low-latency, bandwidth-efficient state synchronization.

## Why use it?

- **Binary protocol** -- minimal bandwidth (a boolean update is ~13 bytes vs ~30+ for JSON)
- **Auto-flatten** -- nested objects/arrays are automatically expanded into dot-path keys
- **Variable-length encoding** -- integers and doubles use 1-3 bytes instead of fixed 4-8
- **Reconnection** -- built-in exponential backoff with jitter
- **Heartbeat** -- automatic connection health monitoring
- **Topic subscriptions** -- subscribe to server-defined data topics with parameters
- **Unity compatible** -- targets `netstandard2.1` with no external dependencies

## Installation

### Unity

1. Copy the `src/DanWebSocket/` folder into your Unity project's `Assets/Plugins/` directory
2. Or add as a Git submodule and reference the `.csproj`

### .NET Project

```xml
<ProjectReference Include="path/to/DanWebSocket.csproj" />
```

## Quick Start

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

## Topic Subscriptions

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

## Array Access

```csharp
// Access flattened arrays
var scores = client.Array("scores");
Console.WriteLine($"Length: {scores.Length}");
Console.WriteLine($"First: {scores[0]}");

// Convert to list
var list = scores.ToList();
```

## Authentication

```csharp
client.OnConnect += () => {
    client.Authorize("my-auth-token");
};
```

## Reconnection

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
