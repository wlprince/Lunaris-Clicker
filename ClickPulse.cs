using System;

namespace MellowClick
{
    internal enum PulseResult { Cancelled, Sent, Failed }
    internal delegate PulseResult EmitPulse(bool right, int releaseMilliseconds, int holdMilliseconds, Func<int, bool> wait);

    internal sealed class ClickPulse
    {
        private readonly Func<bool, bool, bool> send;
        internal ClickPulse(Func<bool, bool, bool> send) { this.send = send; }
        internal PulseResult Emit(bool right, int releaseMilliseconds, int holdMilliseconds, Func<int, bool> wait)
        {
            // Games can sample button state once per frame. Expose both states
            // for real time instead of putting UP/DOWN/UP in one input batch.
            if (!send(right, false)) return PulseResult.Failed;
            if (!wait(releaseMilliseconds)) return PulseResult.Cancelled;
            bool pressed = false, released = false;
            try
            {
                pressed = send(right, true);
                if (pressed) wait(holdMilliseconds);
            }
            finally
            {
                // Always release, including cancellation, focus loss and failed DOWN.
                released = send(right, false);
            }
            return pressed && released ? PulseResult.Sent : PulseResult.Failed;
        }
    }
}
