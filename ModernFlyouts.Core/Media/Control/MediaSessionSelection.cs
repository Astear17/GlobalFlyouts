namespace ModernFlyouts.Core.Media.Control
{
    public sealed record MediaSessionSelection(MediaSessionSnapshot Snapshot, string Reason)
    {
        public static MediaSessionSelection Empty { get; } = new(null, "No eligible media session");
    }
}
