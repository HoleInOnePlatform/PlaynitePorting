using System;

namespace Playnite.HoleInOne
{
    // The deadline starts once, when installation completes, and survives retries.
    public sealed class HandoffDeadline
    {
        private readonly DateTimeOffset expiresAt;

        public HandoffDeadline(DateTimeOffset downloadCompletedAt)
        {
            expiresAt = downloadCompletedAt.AddMinutes(60);
        }

        public int RemainingSeconds(DateTimeOffset now) =>
            Math.Max(0, (int)Math.Ceiling((expiresAt - now).TotalSeconds));

        public bool HasExpired(DateTimeOffset now) => now >= expiresAt;
    }
}
