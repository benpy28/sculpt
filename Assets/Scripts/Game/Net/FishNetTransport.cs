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
