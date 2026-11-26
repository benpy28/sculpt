using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Sculpt.Core.Net;
using Sculpt.Game.Bootstrap;
using UnityEngine;

namespace Sculpt.Game.Net
{
    /// <summary>
    /// The host side of LAN room codes (<see cref="LanRoom"/>): while this game hosts, it answers "who has room K7Q2?" on
    /// the discovery port with its game port, map and mode. Idle (no socket) unless hosting. One code per run of the
    /// game, so friends can type the same code again after the host starts a new match. Discovery only: it never touches
    /// the match connection.
    /// </summary>
    public sealed class LanRoomBeacon : MonoBehaviour
    {
        private static string _code;
        /// <summary>This run's room code (made on first use).</summary>
        public static string Code => _code ??= LanRoom.NewCode(new System.Random(Environment.TickCount ^ System.Diagnostics.Process.GetCurrentProcess().Id));
        /// <summary>True while the beacon is answering (hosting and the discovery port was free).</summary>
        public static bool Answering { get; private set; }
        /// <summary>Why the beacon couldn't open (another game hosting on this PC, say), or empty.</summary>
        public static string Problem { get; private set; } = "";

        private NetSession _session;
        private UdpClient _udp;
        private readonly byte[] _buf = new byte[64];

        public void Init(NetSession session) => _session = session;

        private void Update()
        {
            bool host = _session != null && _session.Mode == NetMode.Host;
            if (host && _udp == null && Problem.Length == 0) Open();
            else if (!host && _udp != null) Close();
            if (_udp == null) return;
            try
            {
                // Non-blocking: answer whatever arrived since last frame, at most a few per frame.
                for (int n = 0; n < 8 && _udp.Available > 0; n++)
                {
                    IPEndPoint from = null;
                    byte[] got = _udp.Receive(ref from);
                    if (!LanRoom.TryReadAsk(got, got.Length, out string code, out uint nonce) || (code != Code && code != LanRoom.AnyCode)) continue;
                    byte[] answer = LanRoom.Answer(Reply(nonce));
                    _udp.Send(answer, answer.Length, from);
                }
            }
            catch (Exception e)
            {
                // A reset from a closed port (Windows reports those on UDP) or similar: keep answering.
                if (!(e is SocketException)) Debug.LogWarning("[SCULPT] Room code: " + e.Message);
            }
        }

        private LanRoomReply Reply(uint nonce)
        {
            var map = GameBootstrap.CurrentMap == GameBootstrap.MapChoice.BattleRoyale ? LanRoomMap.BattleRoyale
                : GameBootstrap.CurrentMap == GameBootstrap.MapChoice.Breach ? LanRoomMap.Breach : LanRoomMap.Duel;
            int players = 0;
            if (_session.Server != null)
                lock (_session.ServerLock) // the server thread adds and removes bots between ticks
                    foreach (ServerPlayer p in _session.Server.Players) if (!NetServer.IsBotConnection(p.ConnectionId)) players++;
            return new LanRoomReply
            {
                Code = Code, Nonce = nonce, Protocol = Protocol.Version, GamePort = _session.Port, Map = map,
                BrMode = map == LanRoomMap.BattleRoyale ? GameBootstrap.NextBrMode : BrMode.None,
                Players = (byte)Math.Min(255, players),
                MaxPlayers = (byte)(map == LanRoomMap.BattleRoyale ? 32 : 2),
            };
        }

        private void Open()
        {
            try
            {
                var udp = new UdpClient(AddressFamily.InterNetwork);
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, LanRoom.DiscoveryPort));
                udp.EnableBroadcast = true;
                _udp = udp;
                Answering = true;
                Debug.Log($"[SCULPT] Room code {Code}: answering on UDP port {LanRoom.DiscoveryPort}.");
            }
            catch (Exception e)
            {
                Problem = "Room code unavailable (port " + LanRoom.DiscoveryPort + " is in use): join by IP.";
                Debug.LogWarning("[SCULPT] Room code: couldn't open UDP port " + LanRoom.DiscoveryPort + ": " + e.Message);
            }
        }

        private void Close()
        {
            try { _udp?.Close(); } catch (Exception) { }
            _udp = null;
            Answering = false;
        }

        private void OnDestroy()
        {
            Close();
            Problem = "";
        }
    }
}
