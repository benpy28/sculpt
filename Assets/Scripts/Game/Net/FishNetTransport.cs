using System;
using System.Reflection;
using FishNet.Broadcast;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Managing.Object;
using FishNet.Managing.Timing;
using FishNet.Transporting;
using UnityEngine;

namespace Sculpt.Game.Net
{
    /// <summary>One opaque SCULPT packet. Core owns the format; FishNet only carries the bytes.</summary>
    public struct SculptPacket : IBroadcast
    {
        public byte[] Data;
    }

    /// <summary>
    /// FishNet used as a plain transport: connections plus reliable/unreliable byte packets over Tugboat (UDP).
    /// Built entirely from code (no prefabs, no NetworkObjects). Incoming data is read at the start of our frame and
    /// outgoing data is flushed right after our ticks, so the network adds no frames of delay.
    /// </summary>
    public sealed class FishNetTransport : IDisposable
    {
        public const ushort DefaultPort = 7770;

        public event Action<int> ServerClientConnected;
        public event Action<int> ServerClientDisconnected;
        public event Action<int, byte[], bool> ServerReceived;   // connection, data, reliable
        public event Action ClientConnected;
        public event Action ClientDisconnected;
        public event Action<byte[], bool> ClientReceived;        // data, reliable

        public NetworkManager Manager { get; private set; }
        public bool IsServer => Manager != null && Manager.ServerManager.Started;
        public bool IsClient => Manager != null && Manager.ClientManager.Started;
        public string LastError { get; private set; } = "";

        private MethodInfo _iterIn, _iterOut;
        private bool _pumpWarned;
        private FieldInfo _socketNetManager;
        private bool _flushWarned;

        /// <summary>
        /// Wake the UDP library's send thread right after our packets are queued (see <see cref="FlushSockets"/>).
        /// Off only for comparing (-noflush on the command line).
        /// </summary>
        public static bool FlushImmediately = !Array.Exists(Environment.GetCommandLineArgs(), a => string.Equals(a, "-noflush", StringComparison.OrdinalIgnoreCase));

        public FishNetTransport()
        {
            var go = new GameObject("FishNet");
            go.SetActive(false); // configure before NetworkManager.Awake runs
            Manager = go.AddComponent<NetworkManager>();
            Manager.SpawnablePrefabs = ScriptableObject.CreateInstance<DefaultPrefabObjects>();
            go.SetActive(true);
            UnityEngine.Object.DontDestroyOnLoad(go);

            Manager.TimeManager.SetTickRate(60);
            Manager.TimeManager.SetPhysicsMode(PhysicsMode.Disabled); // Core does all collision
            Manager.ServerManager.SetFrameRate(0);                      // never cap our frame rate
            Manager.ClientManager.SetFrameRate(0);

            Manager.ServerManager.OnRemoteConnectionState += OnRemoteState;
            Manager.ServerManager.OnAuthenticationResult += OnAuthenticated;
            Manager.ClientManager.OnAuthenticated += OnClientAuthenticated;
            Manager.ClientManager.OnClientConnectionState += OnClientState;
            Manager.ServerManager.RegisterBroadcast<SculptPacket>(OnServerPacket);
            Manager.ClientManager.RegisterBroadcast<SculptPacket>(OnClientPacket);

            // TransportManager.IterateIncoming/IterateOutgoing are internal; FishNet otherwise pumps only on its own tick.
            Type tm = Manager.TransportManager.GetType();
            const BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            _iterIn = tm.GetMethod("IterateIncoming", f, null, new[] { typeof(bool) }, null);
            _iterOut = tm.GetMethod("IterateOutgoing", f, null, new[] { typeof(bool) }, null);
            if (_iterIn == null || _iterOut == null)
                Debug.LogWarning("[SCULPT] FishNet pump methods not found; falling back to FishNet's own 60 Hz pump (adds up to a frame of delay).");
        }

        public bool StartServer(ushort port)
        {
            Manager.TransportManager.Transport.SetPort(port);
            bool ok = Manager.ServerManager.StartConnection(port);
            if (!ok) LastError = $"Could not start server on port {port}.";
            return ok;
        }

        public void StopServer()
        {
            if (Manager != null && Manager.ServerManager.Started) Manager.ServerManager.StopConnection(true);
        }

        public bool StartClient(string address, ushort port)
        {
            bool ok = Manager.ClientManager.StartConnection(address, port);
            if (!ok) LastError = $"Could not connect to {address}:{port}.";
            return ok;
        }

        public void StopClient()
        {
            if (Manager != null && Manager.ClientManager.Started) Manager.ClientManager.StopConnection();
        }

        public void SendToServer(byte[] data, bool reliable)
        {
            if (!IsClient) return;
            Manager.ClientManager.Broadcast(new SculptPacket { Data = data }, reliable ? Channel.Reliable : Channel.Unreliable);
        }

        public void SendToClient(int connectionId, byte[] data, bool reliable)
        {
            if (!IsServer) return;
            if (!Manager.ServerManager.Clients.TryGetValue(connectionId, out NetworkConnection conn)) return;
            Manager.ServerManager.Broadcast(conn, new SculptPacket { Data = data }, true, reliable ? Channel.Reliable : Channel.Unreliable);
        }

        /// <summary>Call at the very start of the frame, before ticking.</summary>
        public void PumpIncoming()
        {
            if (Manager == null || (!IsServer && !IsClient)) return;
            Pump(_iterIn);
        }

        /// <summary>Call right after the frame's ticks so packets leave this frame.</summary>
        public void PumpOutgoing()
        {
            if (Manager == null || (!IsServer && !IsClient)) return;
            Pump(_iterOut);
            if (FlushImmediately) FlushSockets();
        }

        /// <summary>
        /// The UDP library under FishNet's default transport (LiteNetLib inside Tugboat) does not write a packet to the
        /// socket when it is sent: it queues it, and its own thread writes the queue out every 15 ms. Left alone, every
        /// input packet and every snapshot waits 0 to 15 ms (7.5 on average) before it even leaves the machine, in
        /// both directions, and the uneven wait looks like jitter to the netcode. Waking that thread as soon as our
        /// packets are queued sends them now. Its field is internal, hence the reflection; if it is ever renamed we
        /// fall back to the old behaviour and say so once.
        /// </summary>
        private void FlushSockets()
        {
            try
            {
                if (!(Manager.TransportManager.Transport is FishNet.Transporting.Tugboat.Tugboat tugboat)) return;
                if (_socketNetManager == null)
                {
                    _socketNetManager = typeof(FishNet.Transporting.Tugboat.CommonSocket).GetField("NetManager", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (_socketNetManager == null) throw new MissingFieldException("CommonSocket.NetManager");
                }
                if (IsServer && tugboat.ServerSocket != null) (_socketNetManager.GetValue(tugboat.ServerSocket) as LiteNetLib.NetManager)?.TriggerUpdate();
                if (IsClient && tugboat.ClientSocket != null) (_socketNetManager.GetValue(tugboat.ClientSocket) as LiteNetLib.NetManager)?.TriggerUpdate();
            }
            catch (Exception e)
            {
                if (!_flushWarned) Debug.LogWarning("[SCULPT] Could not flush the UDP send queue at once (packets wait up to 15 ms): " + e.Message);
                _flushWarned = true;
                FlushImmediately = false;
            }
        }

        private void Pump(MethodInfo m)
        {
            if (m == null) return;
            try
            {
                if (IsServer) m.Invoke(Manager.TransportManager, new object[] { true });
                if (IsClient) m.Invoke(Manager.TransportManager, new object[] { false });
            }
            catch (Exception e)
            {
                if (!_pumpWarned) Debug.LogWarning("[SCULPT] FishNet pump failed, using FishNet's own pump: " + e.Message);
                _pumpWarned = true;
                _iterIn = _iterOut = null;
            }
        }

        private void OnRemoteState(NetworkConnection conn, RemoteConnectionStateArgs args)
        {
            if (args.ConnectionState == RemoteConnectionState.Stopped) ServerClientDisconnected?.Invoke(conn.ClientId);
        }

        private void OnAuthenticated(NetworkConnection conn, bool ok)
        {
            if (ok) ServerClientConnected?.Invoke(conn.ClientId);
        }

        private void OnClientAuthenticated() => ClientConnected?.Invoke();

        private void OnClientState(ClientConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Stopped) ClientDisconnected?.Invoke();
        }

        private void OnServerPacket(NetworkConnection conn, SculptPacket p, Channel channel)
        {
            if (p.Data != null) ServerReceived?.Invoke(conn.ClientId, p.Data, channel == Channel.Reliable);
        }

        private void OnClientPacket(SculptPacket p, Channel channel)
        {
            if (p.Data != null) ClientReceived?.Invoke(p.Data, channel == Channel.Reliable);
        }

        public void Dispose()
        {
            if (Manager == null) return;
            StopClient();
            StopServer();
            UnityEngine.Object.Destroy(Manager.gameObject);
            Manager = null;
        }
    }
}
