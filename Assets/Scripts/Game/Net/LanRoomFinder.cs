using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Sculpt.Core.Net;
using UnityEngine;

namespace Sculpt.Game.Net
{
    /// <summary>
    /// The joiner side of LAN room codes: asks the whole local network (and this PC, for two copies on one machine) for
    /// a code a few times a second until a host answers or it gives up. Polled from the menu each frame, no threads.
    /// The round trip of the first answer is the ping shown.
    /// </summary>
    public sealed class LanRoomFinder : IDisposable
    {
        public enum State { Looking, Found, NotFound, WrongBuild, Failed }

        public const float GiveUpSeconds = 3f;
        public const float AskEverySeconds = 0.25f;

        public readonly string Code;
        public State Now { get; private set; } = State.Looking;
        public IPAddress Host { get; private set; }
        public LanRoomReply Reply { get; private set; }
        public int PingMs { get; private set; }
        public string Error { get; private set; } = "";
        public float Seconds => (float)_clock.Elapsed.TotalSeconds;

        private UdpClient _udp;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly Dictionary<uint, double> _sentAt = new Dictionary<uint, double>();
        private readonly List<IPAddress> _targets;
        private readonly System.Random _nonces = new System.Random();
        private double _nextAsk;

        public LanRoomFinder(string code)
        {
            Code = LanRoom.Normalize(code) ?? throw new ArgumentException("not a room code", nameof(code));
            _targets = BroadcastTargets();
            try
            {
                _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
            }
            catch (Exception e)
            {
                Now = State.Failed;
                Error = "Couldn't search the network: " + e.Message;
            }
        }

        /// <summary>Call every frame while looking.</summary>
        public void Poll()
        {
            if (Now != State.Looking) return;
            double t = _clock.Elapsed.TotalSeconds;
            try
            {
                if (t >= _nextAsk && t < GiveUpSeconds)
                {
                    _nextAsk = t + AskEverySeconds;
                    uint nonce = (uint)_nonces.Next() ^ ((uint)_nonces.Next() << 16);
                    _sentAt[nonce] = t;
                    byte[] ask = LanRoom.Ask(Code, nonce);
                    foreach (IPAddress a in _targets)
                    {
                        try { _udp.Send(ask, ask.Length, new IPEndPoint(a, LanRoom.DiscoveryPort)); }
                        catch (SocketException) { } // one unreachable network shouldn't stop the others
                    }
                }
                while (_udp.Available > 0)
                {
                    IPEndPoint from = null;
                    byte[] got = _udp.Receive(ref from);
                    if (!LanRoom.TryReadAnswer(got, got.Length, out LanRoomReply r) || r.Code != Code) continue;
                    if (!_sentAt.TryGetValue(r.Nonce, out double sent)) continue; // not ours, or a stale search
                    Reply = r;
                    Host = from.Address;
                    PingMs = Math.Max(0, (int)Math.Round((_clock.Elapsed.TotalSeconds - sent) * 1000));
                    if (r.Protocol != Protocol.Version)
                    {
                        Now = State.WrongBuild;
                        Error = $"Room {Code} runs a {(r.Protocol > Protocol.Version ? "newer" : "older")} build of SCULPT: both need the same version.";
                    }
                    else Now = State.Found;
                    return;
                }
            }
            catch (SocketException) { } // Windows reports an unreachable port as a receive error: keep listening
            catch (Exception e)
            {
                Now = State.Failed;
                Error = "Couldn't search the network: " + e.Message;
                return;
            }
            if (t >= GiveUpSeconds + 0.5)
            {
                Now = State.NotFound;
                Error = $"No room {Code} found on this network. Check the code and that you're on the same Wi-Fi, or type the host's address instead.";
            }
        }

        public void Dispose()
        {
            try { _udp?.Close(); } catch (Exception) { }
            _udp = null;
        }

        /// <summary>Everywhere an ask should go: the general broadcast, each network's own broadcast address, and this PC.</summary>
        private static List<IPAddress> BroadcastTargets()
        {
            var list = new List<IPAddress> { IPAddress.Broadcast, IPAddress.Loopback };
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation u in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.IPv4Mask == null) continue;
                        byte[] ip = u.Address.GetAddressBytes(), mask = u.IPv4Mask.GetAddressBytes();
                        if (ip.Length != 4 || mask.Length != 4) continue;
                        var b = new byte[4];
                        for (int i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                        var bc = new IPAddress(b);
                        if (!list.Contains(bc)) list.Add(bc);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SCULPT] Room code: couldn't list networks: " + e.Message);
            }
            return list;
        }
    }
}
