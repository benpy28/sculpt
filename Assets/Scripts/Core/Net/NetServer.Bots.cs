using System.Collections.Generic;

namespace Sculpt.Core.Net
{
    /// <summary>
    /// Server-side players with no connection (the practice dummy). They are ordinary players to everything else:
    /// snapshots, shots, punches, grenades, heals. Their commands come from their brain each tick instead of the
    /// network, and nothing is ever sent to them.
    /// </summary>
    public sealed partial class NetServer
    {
        /// <summary>Bot connection ids count down from here (real connections are 0 and up; -1 means "everyone").</summary>
        // Far below any real or local connection id (the Game's local player uses -1000).
        public const int FirstBotConnection = -1000000;
        private int _nextBotConnection = FirstBotConnection;
        private readonly Dictionary<int, PracticeDummy> _dummies = new Dictionary<int, PracticeDummy>();

        public static bool IsBotConnection(int connectionId) => connectionId <= FirstBotConnection;

        /// <summary>Practice dummies by player id.</summary>
        public IReadOnlyDictionary<int, PracticeDummy> Dummies => _dummies;

        /// <summary>Adds a practice dummy at its post (it spawns and respawns there). Returns its server player.</summary>
        public ServerPlayer AddDummy(PracticeDummy brain, int team)
        {
            ServerPlayer p = AddPlayer(_nextBotConnection--, team, brain.Post, brain.PostYaw);
            _dummies[p.Id] = brain;
            return p;
        }

        /// <summary>Writes each bot's command for this tick into its input ring, as if it had arrived.</summary>
        private void FeedBots()
        {
            if (_dummies.Count == 0) return;
            foreach (ServerPlayer p in _players)
            {
                if (!_dummies.TryGetValue(p.Id, out PracticeDummy brain)) continue;
                InputCmd cmd = brain.Next(Tick, p.State, Walls, Healing, p.Id, Movement);
                int i = Tick & (_ringSize - 1);
                p.Ring[i] = cmd;
                p.Has[i] = true;
            }
        }

        /// <summary>Every send goes through here: bots have no connection.</summary>
        private void Send(int connectionId, byte[] data, bool reliable)
        {
            if (IsBotConnection(connectionId)) return;
            _send(connectionId, data, reliable);
        }
    }
}
