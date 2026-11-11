using System;
using System.Diagnostics;
using System.Threading;

namespace MellowClick
{
    internal sealed class ClickEngine : IDisposable
    {
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly Thread worker;
        private readonly Func<bool> allowed;
        private readonly EmitPulse emit;
        private readonly Random random = new Random();
        private volatile bool active, right, leftHeld, rightHeld, disposed;
        private int range = 10 | (14 << 16), revision;
        private long count;
        internal event Action Failed;

        internal ClickEngine(Func<bool> allowed, Func<bool, bool> click)
            : this(allowed, delegate(bool right, int gap, int hold, Func<int, bool> wait) { return click(right) ? PulseResult.Sent : PulseResult.Failed; }) { }
        internal ClickEngine(Func<bool> allowed, EmitPulse emit)
        {
            this.allowed = allowed; this.emit = emit;
            worker = new Thread(Run) { IsBackground = true, Name = "Mellow click scheduler" };
            worker.Start();
        }
        internal bool Active { get { return active; } set { active = value; Changed(); } }
        internal bool Right { get { return right; } set { right = value; Changed(); } }
        internal bool Held { get { return right ? rightHeld : leftHeld; } }
        internal long Count { get { return Interlocked.Read(ref count); } }
        internal void SetRange(int min, int max)
        {
            if (min < 1 || max > 60 || min > max) throw new ArgumentOutOfRangeException("min", "CPS must be 1–60 and min <= max.");
            // The pair is packed into one atomic value, so a worker never sees a crossed range.
            Interlocked.Exchange(ref range, min | (max << 16));
            Changed();
        }
        internal void ObserveMouse(int message, uint flags)
        {
            if ((flags & Native.Injected) != 0) return;
            bool previousLeft = leftHeld, previousRight = rightHeld;
            switch (message)
            {
                case 0x0201: leftHeld = true; break;
                case 0x0202: leftHeld = false; break;
                case 0x0204: rightHeld = true; break;
                case 0x0205: rightHeld = false; break;
                default: return;
            }
            if (previousLeft != leftHeld || previousRight != rightHeld) Changed();
        }
        internal void ResetPhysicalState()
        {
            active = false; leftHeld = false; rightHeld = false; Changed();
        }
        private void Changed() { Interlocked.Increment(ref revision); wake.Set(); }
        internal static double IntervalMilliseconds(int min, int max, double sample)
        {
            return 1000.0 / (min + (max - min) * sample);
        }
        private double NextInterval()
        {
            int packed = Volatile.Read(ref range);
            int min = packed & 0xffff;
            int max = packed >> 16;
            return IntervalMilliseconds(min, max, random.NextDouble());
        }
        private void Run()
        {
            double due = 0;
            double interval = 0;
            int seenRevision = -1;
            var clock = Stopwatch.StartNew();
            bool preciseTimer = false;
            try
            {
            while (!disposed)
            {
                if (!active || !Held || !allowed())
                {
                    due = 0;
                    if (preciseTimer) { Native.timeEndPeriod(1); preciseTimer = false; }
                    wake.WaitOne(20);
                    continue;
                }
                int currentRevision = Volatile.Read(ref revision);
                if (due == 0 || currentRevision != seenRevision)
                {
                    if (!preciseTimer) preciseTimer = Native.timeBeginPeriod(1) == 0;
                    interval = NextInterval();
                    due = clock.Elapsed.TotalMilliseconds + interval;
                    seenRevision = currentRevision;
                }
                double remaining = due - clock.Elapsed.TotalMilliseconds;
                if (remaining > 0)
                {
                    wake.WaitOne(Math.Max(1, (int)Math.Ceiling(remaining)));
                    continue;
                }
                if (!disposed && active && Held && allowed() && currentRevision == Volatile.Read(ref revision))
                {
                    bool selectedRight = right;
                    double start = clock.Elapsed.TotalMilliseconds;
                    int gap = Math.Max(2, Math.Min(18, (int)Math.Round(interval * .28)));
                    int hold = Math.Max(3, Math.Min(30, (int)Math.Round(interval * .45)));
                    PulseResult result = emit(selectedRight, gap, hold, delegate(int milliseconds) { return WaitPhase(milliseconds, currentRevision, selectedRight); });
                    if (result == PulseResult.Failed)
                    {
                        active = false;
                        var handler = Failed; if (handler != null) handler();
                    }
                    else if (result == PulseResult.Sent) Interlocked.Increment(ref count);
                    interval = NextInterval();
                    due = Math.Max(start + interval, clock.Elapsed.TotalMilliseconds);
                }
                else due = 0;
            }
            }
            finally { if (preciseTimer) Native.timeEndPeriod(1); }
        }
        private bool WaitPhase(int milliseconds, int expectedRevision, bool expectedRight)
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                if (disposed || !active || !Held || right != expectedRight || expectedRevision != Volatile.Read(ref revision) || !allowed()) return false;
                int remaining = milliseconds - (int)clock.ElapsedMilliseconds;
                if (remaining <= 0) return true;
                wake.WaitOne(Math.Min(remaining, 3));
            }
        }
        public void Dispose()
        {
            if (disposed) return;
            active = false; disposed = true; wake.Set();
            // SendInput can wait for a hook on the UI thread. Do not join from that thread.
            ThreadPool.QueueUserWorkItem(delegate { worker.Join(); wake.Dispose(); });
        }
        internal void StopAfterUnhook()
        {
            Dispose();
            // The UI hook has already been removed, so SendInput cannot wait
            // on this UI thread. Allow a pending pulse to send its final UP.
            worker.Join(1000);
        }
    }
}
