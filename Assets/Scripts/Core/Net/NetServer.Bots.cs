using System.Collections.Generic;

namespace Sculpt.Core.Net
{
    /// <summary>A bot's brain: this tick's input command for its server player, made only from what a player could know.</summary>
    public interface IBotBrain
    {
        /// <summary>True: it counts as a player for the match (it can start one, and has a score). False: scenery (the practice dummy).</summary>
        bool CountsAsPlayer { get; }
        InputCmd Next(NetServer server, ServerPlayer me);
    }

    /// <summary>
    /// Server-side players with no connection (the practice dummy, the duel bot). They are ordinary players to everything else:
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
        private readonly Dictionary<int, IBotBrain> _brains = new Dictionary<int, IBotBrain>();

        public static bool IsBotConnection(int connectionId) => connectionId <= FirstBotConnection;

        /// <summary>Practice dummies by player id.</summary>
        public IReadOnlyDictionary<int, PracticeDummy> Dummies => _dummies;

        /// <summary>Adds a practice dummy at its post (it spawns and respawns there). Returns its server player.</summary>
        public ServerPlayer AddDummy(PracticeDummy brain, int team)
        {
            ServerPlayer p = AddPlayer(_nextBotConnection--, team, brain.Post, brain.PostYaw, Replay.JoinKind.Dummy, false);
            _dummies[p.Id] = brain;
            return p;
        }

        /// <summary>Adds a bot player with its own brain at a spawn (it respawns there). Returns its server player.</summary>
        public ServerPlayer AddBot(IBotBrain brain, int team, Vec3d spawn, double spawnYaw)
        {
            ServerPlayer p = AddPlayer(_nextBotConnection--, team, spawn, spawnYaw, Replay.JoinKind.Bot, brain.CountsAsPlayer);
            _brains[p.Id] = brain;
            _matchDirty = true;
            return p;
        }

        /// <summary>Bot brains by player id (the duel bot).</summary>
        public IReadOnlyDictionary<int, IBotBrain> Bots => _brains;

        /// <summary>A real player or a bot that plays the match (not the practice dummy).</summary>
        private bool CountsForMatch(ServerPlayer p) =>
            !IsBotConnection(p.ConnectionId) || (_brains.TryGetValue(p.Id, out IBotBrain b) && b.CountsAsPlayer);

        /// <summary>Writes each bot's command for this tick into its input ring, as if it had arrived.</summary>
        private void FeedBots()
        {
            if (_dummies.Count == 0 && _brains.Count == 0) return;
            foreach (ServerPlayer p in _players)
            {
                InputCmd cmd;
                if (_dummies.TryGetValue(p.Id, out PracticeDummy dummy)) cmd = dummy.Next(Tick, p.State, Walls, Healing, p.Id, Movement);
                else if (_brains.TryGetValue(p.Id, out IBotBrain brain)) { cmd = brain.Next(this, p); cmd.Tick = Tick; }
                else continue;
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
