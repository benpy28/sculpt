using System.Collections.Generic;

namespace Sculpt.Core.Net
{
    /// <summary>The server side of the match (real players and duel bots count; the practice dummy doesn't) (most kills in 5 minutes): phases, scores, and the reset between matches.</summary>
    public sealed partial class NetServer
    {
        public readonly MatchConfig MatchConfig = new MatchConfig();
        public readonly MatchState Match = new MatchState();
        private bool _matchDirty = true;
        private TileState[] _tileStart;
        private int[] _tileStartHealth;
        private List<GroundItem> _itemsStart;

        /// <summary>In a two-player match, the one other match player on the other team (0 if there isn't exactly one).</summary>
        private int TheOpponent(ServerPlayer victim)
        {
            int found = 0, n = 0;
            foreach (ServerPlayer p in _players)
            {
                if (p == victim || !CountsForMatch(p)) continue;
                n++;
                if (p.Team != victim.Team) found = p.Id;
            }
            return n == 1 ? found : 0;
        }

        private int HumanCount()
        {
            int n = 0;
            foreach (ServerPlayer p in _players) if (CountsForMatch(p)) n++;
            return n;
        }

        /// <summary>Remembers the map as it is now (call after map setup): every reset puts it back like this.</summary>
        private void CaptureMatchStart()
        {
            if (_itemsStart != null) return;
            _itemsStart = new List<GroundItem>(Items.All);
            if (Walls == null) return;
            _tileStart = new TileState[Walls.Count];
            _tileStartHealth = new int[Walls.Count];
            for (int i = 0; i < Walls.Count; i++) { _tileStart[i] = Walls[i].State; _tileStartHealth[i] = Walls[i].Health; }
        }

        /// <summary>Start of each tick: move the match along.</summary>
        private void StepMatch()
        {
            if (!MatchConfig.Enabled)
            {
                if (Match.Phase != MatchPhase.Off) SetPhase(MatchPhase.Off, 0);
                return;
            }
            CaptureMatchStart();
            bool enough = HumanCount() >= MatchConfig.MinPlayers;
            switch (Match.Phase)
            {
                case MatchPhase.Off:
                case MatchPhase.Waiting:
                    if (enough) BeginCountdown();
                    else if (Match.Phase != MatchPhase.Waiting) SetPhase(MatchPhase.Waiting, 0);
                    break;
                case MatchPhase.Countdown:
                    if (!enough) SetPhase(MatchPhase.Waiting, 0);
                    else if (Tick >= Match.PhaseEndTick) SetPhase(MatchPhase.Playing, Tick + MatchConfig.DurationTicks);
                    break;
                case MatchPhase.Playing:
                    if (!enough) SetPhase(MatchPhase.Waiting, 0);
                    else if (Tick >= Match.PhaseEndTick) SetPhase(MatchPhase.Ended, Tick + MatchConfig.ResultsTicks);
                    break;
                case MatchPhase.Ended:
                    if (Tick < Match.PhaseEndTick) break;
                    if (enough) BeginCountdown();
                    else SetPhase(MatchPhase.Waiting, 0);
                    break;
            }
        }

        private void SetPhase(MatchPhase phase, int endTick)
        {
            Match.Phase = phase;
            Match.PhaseStartTick = Tick;
            Match.PhaseEndTick = endTick;
            _matchDirty = true;
        }

        /// <summary>A fresh match: the map goes back to how it started, everyone respawns at full health, scores clear.</summary>
        private void BeginCountdown()
        {
            ResetMap();
            foreach (ServerPlayer p in _players)
            {
                p.MatchKills = p.MatchDeaths = 0;
                p.ForceRespawn = true;
            }
            SetPhase(MatchPhase.Countdown, Tick + MatchConfig.CountdownTicks);
        }

        /// <summary>Puts every tile and ground item back as they were at the start (tile changes go out as usual).</summary>
        public void ResetMap()
        {
            CaptureMatchStart();
            foreach (ServerPlayer p in _players) Healing?.Cancel(p.Id, HealResult.Stopped, null, Tick);
            if (Walls != null && _tileStart != null)
                for (int i = 0; i < _tileStart.Length; i++)
                {
                    if (Walls.IsFollower(i)) continue; // followers come back with their links
                    if (Walls[i].State != _tileStart[i] || Walls[i].Health != _tileStartHealth[i] || Walls[i].Open != 0)
                        Walls.Set(i, _tileStart[i], _tileStartHealth[i], TileCause.Built);
                }
            Items.ReplaceAll(_itemsStart);
        }

        private void OnMatchKill(ServerPlayer victim, ServerPlayer killer)
        {
            if (!Match.CountsKills) return;
            victim.MatchDeaths++;
            if (killer != null) killer.MatchKills++;
            _matchDirty = true;
        }

        /// <summary>End of tick: the match message to everyone when anything changed.</summary>
        private void SendMatch()
        {
            if (!_matchDirty) return;
            _matchDirty = false;
            FillScores();
            _w.Clear();
            MatchProtocol.Write(_w, Match, Tick);
            byte[] data = _w.ToArray();
            foreach (ServerPlayer to in _players) Send(to.ConnectionId, data, true);
        }

        private void FillScores()
        {
            Match.Scores.Clear();
            foreach (ServerPlayer p in _players)
                if (CountsForMatch(p))
                    Match.Scores.Add(new MatchScore { PlayerId = p.Id, Team = p.Team, Kills = p.MatchKills, Deaths = p.MatchDeaths });
            Match.Version++;
        }

        private void SendMatchTo(ServerPlayer p)
        {
            FillScores();
            _w.Clear();
            MatchProtocol.Write(_w, Match, Tick);
            Send(p.ConnectionId, _w.ToArray(), true);
        }
    }
}
