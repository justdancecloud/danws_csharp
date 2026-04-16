using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DanWebSocket.Api;
using DanWebSocket.Protocol;
using Xunit;

namespace DanWebSocket.Tests
{
    public class ServerTests : IDisposable
    {
        private readonly List<IDisposable> _disposables = new List<IDisposable>();
        private static int _nextPort = 19100;

        private int GetPort()
        {
            return Interlocked.Increment(ref _nextPort);
        }

        public void Dispose()
        {
            foreach (var d in _disposables) { try { d.Dispose(); } catch { } }
        }

        private DanWebSocketServer CreateServer(int port, Mode mode = Mode.Broadcast, long ttlMs = 600_000)
        {
            var server = new DanWebSocketServer(port, "/", mode, ttlMs, flushIntervalMs: 50);
            _disposables.Add(server);
            return server;
        }

        private DanWebSocketClient CreateClient(int port)
        {
            var client = new DanWebSocketClient($"ws://localhost:{port}/", new ClientOptions { Debug = false });
            _disposables.Add(client);
            return client;
        }

        private static bool WaitFor(Func<bool> condition, int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return true;
                Thread.Sleep(50);
            }
            return condition();
        }

        // ---- Broadcast Tests ----

        [Fact]
        public void Broadcast_SetAndGet()
        {
            int port = GetPort();
            var server = CreateServer(port, Mode.Broadcast);
            server.Set("score", 42);
            Assert.Equal(42, server.Get("score"));
        }

        [Fact]
        public void Broadcast_ClientReceivesSetValue()
        {
            int port = GetPort();
            var server = CreateServer(port, Mode.Broadcast);
            server.Set("greeting", "hello");

            var client = CreateClient(port);
            bool ready = false;
            client.OnReady += () => ready = true;
            client.Connect();

            Assert.True(WaitFor(() => ready), "Client should reach ready state");
            Assert.True(WaitFor(() => client.Get("greeting") != null), "Client should receive greeting");
            Assert.Equal("hello", client.Get("greeting"));
        }

        [Fact]
        public void Broadcast_ServerPushReceived()
        {
            int port = GetPort();
            var server = CreateServer(port, Mode.Broadcast);

            var client = CreateClient(port);
            bool ready = false;
            string? receivedKey = null;
            object? receivedValue = null;

            client.OnReady += () => ready = true;
            client.OnReceive += (key, value) =>
            {
                if (key == "dynamic")
                {
                    receivedKey = key;
                    receivedValue = value;
                }
            };
            client.Connect();
            Assert.True(WaitFor(() => ready), "Client should be ready");

            // Set a value after client is ready
            server.Set("dynamic", "world");

            Assert.True(WaitFor(() => receivedKey != null), "Client should receive dynamic key");
            Assert.Equal("dynamic", receivedKey);
            Assert.Equal("world", receivedValue);
        }

        [Fact]
        public void Broadcast_Keys()
        {
            int port = GetPort();
            var server = CreateServer(port, Mode.Broadcast);
            server.Set("a", 1);
            server.Set("b", 2);
            var keys = server.Keys;
            Assert.Contains("a", keys);
            Assert.Contains("b", keys);
        }

        [Fact]
        public void Broadcast_ClearKey()
        {
            int port = GetPort();
            var server = CreateServer(port, Mode.Broadcast);
            server.Set("x", 10);
            Assert.Equal(10, server.Get("x"));
            server.Clear("x");
            // After clear, the value should be gone (null)
            Assert.Null(server.Get("x"));
        }

        // ---- Principal Tests ----

        [Fact]
        public void Principal_AuthFlowAndIsolation()
        {
            int port = GetPort();
            var server = CreateServer(port, Mode.Principal);
            server.EnableAuthorization(true, 5000);

            // Setup auth handler
            server.OnAuthorize((uuid, token) =>
            {
                if (token == "alice-token")
                    server.Authorize(uuid, token, "alice");
                else if (token == "bob-token")
                    server.Authorize(uuid, token, "bob");
            });

            // Set per-principal state
            server.Principal("alice").Set("role", "admin");
            server.Principal("bob").Set("role", "viewer");

            // Alice client
            var alice = CreateClient(port);
            bool aliceReady = false;
            alice.OnReady += () => aliceReady = true;
            alice.OnConnect += () => alice.Authorize("alice-token");
            alice.Connect();

            Assert.True(WaitFor(() => aliceReady), "Alice should reach ready");
            Assert.True(WaitFor(() => alice.Get("role") != null), "Alice should get role");
            Assert.Equal("admin", alice.Get("role"));

            // Bob client
            var bob = CreateClient(port);
            bool bobReady = false;
            bob.OnReady += () => bobReady = true;
            bob.OnConnect += () => bob.Authorize("bob-token");
            bob.Connect();

            Assert.True(WaitFor(() => bobReady), "Bob should reach ready");
            Assert.True(WaitFor(() => bob.Get("role") != null), "Bob should get role");
            Assert.Equal("viewer", bob.Get("role"));
        }

        [Fact]
        public void Principal_MultiClientSamePrincipal()
        {
            int port = GetPort();
            var server = CreateServer(port, Mode.Principal);
            server.EnableAuthorization(true, 5000);

            server.OnAuthorize((uuid, token) =>
            {
                server.Authorize(uuid, token, "team");
            });

            server.Principal("team").Set("count", 0);

            var c1 = CreateClient(port);
            bool c1Ready = false;
            c1.OnReady += () => c1Ready = true;
            c1.OnConnect += () => c1.Authorize("t1");
            c1.Connect();

            var c2 = CreateClient(port);
            bool c2Ready = false;
            c2.OnReady += () => c2Ready = true;
            c2.OnConnect += () => c2.Authorize("t2");
            c2.Connect();

            Assert.True(WaitFor(() => c1Ready && c2Ready), "Both clients should be ready");

            server.Principal("team").Set("count", 5);
            Assert.True(WaitFor(() =>
            {
                var v1 = c1.Get("count");
                var v2 = c2.Get("count");
                return v1 != null && v2 != null && (int)v1 == 5 && (int)v2 == 5;
            }), "Both clients should see count=5");
        }

        // ---- Session Topic Tests ----

        [Fact]
        public void SessionTopic_SubscribeAndPayload()
        {
            int port = GetPort();
            var server = CreateServer(port, Mode.SessionTopic);

            server.Topic.OnSubscribe((session, topic) =>
            {
                topic.Payload.Set("title", "Test Topic");
                topic.Payload.Set("status", "active");
            });

            var client = CreateClient(port);
            bool ready = false;
            client.OnReady += () =>
            {
                ready = true;
                client.Subscribe("board");
            };
            client.Connect();
            Assert.True(WaitFor(() => ready), "Client should be ready");

            var handle = client.Topic("board");
            Assert.True(WaitFor(() => handle.Get("title") != null), "Client should get topic title");
            Assert.Equal("Test Topic", handle.Get("title"));
            Assert.Equal("active", handle.Get("status"));
        }

        [Fact]
        public void SessionTopic_Unsubscribe()
        {
            int port = GetPort();
            var server = CreateServer(port, Mode.SessionTopic);

            bool unsubscribed = false;
            server.Topic.OnSubscribe((session, topic) =>
            {
                topic.Payload.Set("data", "exists");
            });
            server.Topic.OnUnsubscribe((session, topic) =>
            {
                unsubscribed = true;
            });

            var client = CreateClient(port);
            bool ready = false;
            client.OnReady += () =>
            {
                ready = true;
                client.Subscribe("room");
            };
            client.Connect();
            Assert.True(WaitFor(() => ready), "Client ready");

            var handle = client.Topic("room");
            Assert.True(WaitFor(() => handle.Get("data") != null), "Got topic data");

            client.Unsubscribe("room");
            Assert.True(WaitFor(() => unsubscribed, 3000), "Server should get unsubscribe callback");
        }

        // ---- Session Principal Topic Tests ----

        [Fact]
        public void SessionPrincipalTopic_AuthAndTopic()
        {
            int port = GetPort();
            var server = CreateServer(port, Mode.SessionPrincipalTopic);
            server.EnableAuthorization(true, 5000);

            server.OnAuthorize((uuid, token) =>
            {
                server.Authorize(uuid, token, "player1");
            });

            server.Topic.OnSubscribe((session, topic) =>
            {
                topic.Payload.Set("game", "started");
            });

            var client = CreateClient(port);
            bool ready = false;
            client.OnConnect += () => client.Authorize("play-token");
            client.OnReady += () =>
            {
                ready = true;
                client.Subscribe("game_state");
            };
            client.Connect();

            Assert.True(WaitFor(() => ready), "Client ready");
            var handle = client.Topic("game_state");
            Assert.True(WaitFor(() => handle.Get("game") != null), "Topic data received");
            Assert.Equal("started", handle.Get("game"));
        }

        // ---- Auth Reject Tests ----

        [Fact]
        public void Auth_RejectClosesClient()
        {
            int port = GetPort();
            var server = CreateServer(port, Mode.Principal);
            server.EnableAuthorization(true, 5000);

            server.OnAuthorize((uuid, token) =>
            {
                server.Reject(uuid, "Bad token");
            });

            var client = CreateClient(port);
            DanWSException? error = null;
            bool disconnected = false;
            client.OnError += (err) => error = err;
            client.OnDisconnect += () => disconnected = true;
            client.OnConnect += () => client.Authorize("bad-token");
            client.Connect();

            Assert.True(WaitFor(() => error != null || disconnected, 3000), "Client should get error or disconnect");
            if (error != null)
            {
                Assert.Equal("AUTH_REJECTED", error.Code);
            }
        }

        // ---- Auth Timeout Tests ----

        [Fact]
        public void Auth_TimeoutClosesConnection()
        {
            int port = GetPort();
            var server = CreateServer(port, Mode.Principal);
            server.EnableAuthorization(true, 500); // 500ms timeout

            // Don't set up any authorize handler - client just won't auth

            var client = CreateClient(port);
            bool disconnected = false;
            client.OnDisconnect += () => disconnected = true;
            // Don't send auth token
            client.Connect();

            Assert.True(WaitFor(() => disconnected, 3000), "Client should be disconnected due to auth timeout");
        }

        // ---- Authorize null throws ----

        [Fact]
        public void Authorize_NullPrincipalThrows()
        {
            int port = GetPort();
            var server = CreateServer(port, Mode.Principal);
            Assert.Throws<ArgumentException>(() => server.Authorize("some-uuid", "token", null!));
        }

        // ---- Max Connections Tests ----

        [Fact]
        public void MaxConnections_Enforcement()
        {
            int port = GetPort();
            var server = CreateServer(port, Mode.Broadcast);
            server.SetMaxConnections(1);

            server.Set("val", 1);

            var c1 = CreateClient(port);
            bool c1Ready = false;
            c1.OnReady += () => c1Ready = true;
            c1.Connect();
            Assert.True(WaitFor(() => c1Ready), "First client should connect");

            var c2 = CreateClient(port);
            bool c2Ready = false;
            bool c2Disconnected = false;
            c2.OnReady += () => c2Ready = true;
            c2.OnDisconnect += () => c2Disconnected = true;
            c2.Connect();

            // Second client should either never get ready or be disconnected
            Thread.Sleep(1500);
            Assert.True(!c2Ready || c2Disconnected, "Second client should be rejected or disconnected");
        }

        // ---- Metrics Tests ----

        [Fact]
        public void Metrics_Snapshot()
        {
            int port = GetPort();
            var server = CreateServer(port, Mode.Broadcast);
            server.Set("x", 1);

            var client = CreateClient(port);
            bool ready = false;
            client.OnReady += () => ready = true;
            client.Connect();
            Assert.True(WaitFor(() => ready), "Client should be ready");

            // Wait for some frames to be exchanged
            Thread.Sleep(500);

            var metrics = server.GetMetrics();
            Assert.True(metrics.ActiveSessions > 0, "Should have active sessions");
            Assert.True(metrics.FramesIn > 0, "Should have frames in");
            Assert.True(metrics.FramesOut > 0, "Should have frames out");
        }

        // ---- Server Close Tests ----

        [Fact]
        public void ServerClose_DisconnectsAll()
        {
            int port = GetPort();
            var server = CreateServer(port, Mode.Broadcast);
            server.Set("val", 1);

            var client = CreateClient(port);
            bool ready = false;
            bool disconnected = false;
            client.OnReady += () => ready = true;
            client.OnDisconnect += () => disconnected = true;
            client.Connect();
            Assert.True(WaitFor(() => ready), "Client should be ready");

            server.Close();
            Assert.True(WaitFor(() => disconnected, 3000), "Client should be disconnected after server close");
        }

        // ---- OnConnection event ----

        [Fact]
        public void OnConnection_Fires()
        {
            int port = GetPort();
            var server = CreateServer(port, Mode.Broadcast);
            server.Set("val", 1);

            DanWebSocketSession? connectedSession = null;
            server.OnConnection(session =>
            {
                connectedSession = session;
            });

            var client = CreateClient(port);
            bool ready = false;
            client.OnReady += () => ready = true;
            client.Connect();
            Assert.True(WaitFor(() => ready), "Client should be ready");
            Assert.True(WaitFor(() => connectedSession != null), "OnConnection should fire");
            Assert.NotNull(connectedSession!.Id);
        }
    }
}
