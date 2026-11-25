using System;

namespace Sculpt.Core.Diagnostics
{
    /// <summary>What was pressed, for the overlay's label.</summary>
    public enum ClickKind : byte { Fire, Jump, Punch, Heal, Other }

    /// <summary>
    /// One press followed from the hardware to the screen and to the server and back. Every time is in milliseconds on
    /// the client's own monotonic clock; the only number that comes from the server is how long it held the command
    /// (a duration, so the two clocks never need to agree).
    /// </summary>
    public struct ClickSample
    {
        public ClickKind Kind;
        /// <summary>When the operating system stamped the input event (equal to <see cref="ReadMs"/> when unknown).</summary>
        public double HardwareMs;
        /// <summary>When the game read it (start of that frame's input sampling).</summary>
        public double ReadMs;
        /// <summary>When the simulation tick that used it ran, and which tick that was.</summary>
        public double TickMs;
        public int Tick;
        /// <summary>When that tick's command packet was handed to the network.</summary>
        public double SentMs;
        /// <summary>When the frame showing the predicted result had been handed to the graphics card.</summary>
        public double DrawnMs;
        /// <summary>When the server's state for that tick arrived, and when the frame showing it was handed over.</summary>
        public double AckMs, AckDrawnMs;
        /// <summary>How long the server held the command: waiting for its tick plus simulating and sending. Negative = not reported.</summary>
        public double ServerMs;
        public bool Ticked, Sent, Drawn, Acked, AckDrawn;

        public bool ServerKnown => ServerMs >= 0;
        /// <summary>Operating system event to the game reading it (waiting for the frame to start).</summary>
        public double InputWaitMs => ReadMs - HardwareMs;
        /// <summary>Read to the simulation tick that uses it (waiting for the next tick).</summary>
        public double TickWaitMs => TickMs - ReadMs;
        /// <summary>Local PC: click read to packet sent.</summary>
        public double LocalMs => SentMs - ReadMs;
        /// <summary>Hardware event to the predicted frame handed to the graphics card (what you feel when you click).</summary>
        public double ClickToFrameMs => DrawnMs - HardwareMs;
        /// <summary>Packet sent to the server's answer arriving.</summary>
        public double RoundTripMs => AckMs - SentMs;
        /// <summary>The two network legs together (round trip minus the server's share).</summary>
        public double NetworkMs => Math.Max(0, RoundTripMs - Math.Max(0, ServerMs));
        /// <summary>Network up, taken as half of <see cref="NetworkMs"/> (the legs can't be told apart without synced clocks).</summary>
        public double NetUpMs => NetworkMs * 0.5;
        /// <summary>Network down (the other half) plus getting that answer onto a frame.</summary>
        public double DownAndDisplayMs => NetworkMs * 0.5 + (AckDrawnMs - AckMs);
        /// <summary>Hardware event to the server-confirmed result on a frame.</summary>
        public double TotalMs => AckDrawnMs - HardwareMs;
    }

    /// <summary>
    /// Follows presses through the client and the server round trip for the latency overlay (Ben: per click, local PC,
    /// network up, server processing including the tick wait, network down plus display; last value and running average).
    /// The game calls the stage methods in frame order; this class only matches them up and keeps the numbers.
    /// </summary>
    public sealed class LatencyProbe
    {
        /// <summary>A press that has not been answered after this long is forgotten (disconnect, dead player, packet storm).</summary>
        public double TimeoutMs = 3000;
        /// <summary>Running averages cover this many newest clicks.</summary>
        public const int AverageWindow = 32;

        private readonly ClickSample[] _pending = new ClickSample[16];
        private int _pendingCount;
        private bool _waiting;          // a press was read that no tick has used yet
        private ClickSample _waitingSample;

        private readonly ClickSample[] _done = new ClickSample[AverageWindow];
        private int _doneCount, _doneNext;
        private readonly ClickSample[] _local = new ClickSample[AverageWindow];
        private int _localCount, _localNext;

        /// <summary>The newest press whose predicted frame is out (local numbers valid; the server part may be missing yet).</summary>
        public ClickSample LastLocal { get; private set; }
        public bool HasLocal => _localCount > 0;
        /// <summary>The newest fully answered press.</summary>
        public ClickSample Last { get; private set; }
        public bool HasLast => _doneCount > 0;
        public int Completed { get; private set; }
        /// <summary>Presses whose predicted frame went out (each updates <see cref="LastLocal"/>).</summary>
        public int LocalCompleted { get; private set; }
        public int TimedOut { get; private set; }
        /// <summary>Presses still on their way.</summary>
        public int Pending => _pendingCount + (_waiting ? 1 : 0);

        public void Clear()
        {
            _pendingCount = 0;
            _waiting = false;
            _doneCount = _doneNext = 0;
            _localCount = _localNext = 0;
            Completed = 0;
            LocalCompleted = 0;
            TimedOut = 0;
        }

        /// <summary>
        /// A press was read this frame. <paramref name="hardwareMs"/> is the operating system's time for the event; pass
        /// NaN (or anything later than <paramref name="readMs"/>) when unknown. A second press before a tick ran rides
        /// with the first (the earliest press is the one the player has been waiting on).
        /// </summary>
        public void Press(ClickKind kind, double hardwareMs, double readMs)
        {
            if (_waiting) return;
            if (!(hardwareMs <= readMs)) hardwareMs = readMs;
            _waiting = true;
            _waitingSample = new ClickSample { Kind = kind, HardwareMs = hardwareMs, ReadMs = readMs, ServerMs = -1 };
        }

        /// <summary>The waiting press, if any, was dropped by the game (input blocked, not allowed): forget it.</summary>
        public void CancelWaiting() => _waiting = false;

        /// <summary>The simulation tick that consumed the waiting press ran.</summary>
        public void Ticked(int tick, double nowMs)
        {
            if (!_waiting) return;
            _waiting = false;
            if (_pendingCount == _pending.Length) { RemovePending(0); TimedOut++; }
            ClickSample s = _waitingSample;
            s.Tick = tick;
            s.TickMs = nowMs;
            s.Ticked = true;
            _pending[_pendingCount++] = s;
        }

        /// <summary>This frame's packets were handed to the network.</summary>
        public void Sent(double nowMs)
        {
            for (int i = 0; i < _pendingCount; i++)
            {
                if (_pending[i].Sent) continue;
                _pending[i].Sent = true;
                _pending[i].SentMs = nowMs;
            }
        }

        /// <summary>
        /// The frame that was being built has been handed to the graphics card (call at the start of the next frame, so
        /// the time includes the render thread and any wait for the display). Presses ticked before it are now on screen
        /// as predicted; answers that arrived before it are on screen too.
        /// </summary>
        public void FrameOut(double nowMs)
        {
            for (int i = 0; i < _pendingCount; i++)
            {
                if (!_pending[i].Sent || _pending[i].Drawn) continue;
                _pending[i].Drawn = true;
                _pending[i].DrawnMs = nowMs;
                LastLocal = _pending[i];
                LocalCompleted++;
                _local[_localNext] = _pending[i];
                _localNext = (_localNext + 1) % _local.Length;
                if (_localCount < _local.Length) _localCount++;
            }
            for (int i = 0; i < _pendingCount;)
            {
                if (_pending[i].Acked && _pending[i].Drawn)
                {
                    ClickSample s = _pending[i];
                    s.AckDrawn = true;
                    s.AckDrawnMs = nowMs;
                    Last = s;
                    _done[_doneNext] = s;
                    _doneNext = (_doneNext + 1) % _done.Length;
                    if (_doneCount < _done.Length) _doneCount++;
                    Completed++;
                    RemovePending(i);
                    continue;
                }
                if (nowMs - _pending[i].ReadMs > TimeoutMs)
                {
                    TimedOut++;
                    RemovePending(i);
                    continue;
                }
                i++;
            }
            if (_waiting && nowMs - _waitingSample.ReadMs > TimeoutMs) _waiting = false;
        }

        /// <summary>
        /// The server's state for <paramref name="snapshotTick"/> arrived. Every sent press at or before that tick is
        /// answered. <paramref name="serverMs"/> is the server's reported hold for exactly <paramref name="snapshotTick"/>
        /// (negative when it sent none); it is only credited to a press of that same tick.
        /// </summary>
        public void Answered(int snapshotTick, double serverMs, double nowMs)
        {
            for (int i = 0; i < _pendingCount; i++)
            {
                if (_pending[i].Acked || !_pending[i].Sent || _pending[i].Tick > snapshotTick) continue;
                _pending[i].Acked = true;
                // A state for this tick that arrived before our packet left was built from a guessed command (we were
                // behind the server): it answers nothing, so no round trip and no server share can be read from it.
                bool guessed = nowMs < _pending[i].SentMs;
                _pending[i].AckMs = guessed ? _pending[i].SentMs : nowMs;
                _pending[i].ServerMs = _pending[i].Tick == snapshotTick && !guessed ? serverMs : -1;
            }
        }

        /// <summary>True while a sent press is waiting for the server's state; gives the oldest tick waited for.</summary>
        public bool WaitingFor(out int oldestTick)
        {
            oldestTick = int.MaxValue;
            for (int i = 0; i < _pendingCount; i++)
                if (_pending[i].Sent && !_pending[i].Acked && _pending[i].Tick < oldestTick) oldestTick = _pending[i].Tick;
            return oldestTick != int.MaxValue;
        }

        private void RemovePending(int i)
        {
            for (int k = i + 1; k < _pendingCount; k++) _pending[k - 1] = _pending[k];
            _pendingCount--;
        }

        // ---------------------------------------------------------------- Running averages (newest AverageWindow clicks)

        public double AvgInputWaitMs => AvgLocal(0);
        public double AvgTickWaitMs => AvgLocal(1);
        public double AvgLocalMs => AvgLocal(2);
        public double AvgClickToFrameMs => AvgLocal(3);
        public double AvgNetUpMs => AvgDone(0);
        /// <summary>Average over the clicks whose server hold was reported; negative when none was.</summary>
        public double AvgServerMs => AvgDone(1);
        public double AvgDownAndDisplayMs => AvgDone(2);
        public double AvgTotalMs => AvgDone(3);
        public double AvgRoundTripMs => AvgDone(4);

        private double AvgLocal(int which)
        {
            if (_localCount == 0) return 0;
            double sum = 0;
            for (int i = 0; i < _localCount; i++)
            {
                ClickSample s = _local[i];
                sum += which == 0 ? s.InputWaitMs : which == 1 ? s.TickWaitMs : which == 2 ? s.LocalMs : s.ClickToFrameMs;
            }
            return sum / _localCount;
        }

        private double AvgDone(int which)
        {
            double sum = 0;
            int n = 0;
            for (int i = 0; i < _doneCount; i++)
            {
                ClickSample s = _done[i];
                if (which == 1)
                {
                    if (!s.ServerKnown) continue;
                    sum += s.ServerMs;
                }
                else sum += which == 0 ? s.NetUpMs : which == 2 ? s.DownAndDisplayMs : which == 3 ? s.TotalMs : s.RoundTripMs;
                n++;
            }
            return n == 0 ? (which == 1 ? -1 : 0) : sum / n;
        }
    }
}
