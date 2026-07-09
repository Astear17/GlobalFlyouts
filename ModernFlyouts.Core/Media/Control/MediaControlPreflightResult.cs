namespace ModernFlyouts.Core.Media.Control
{
    public sealed record MediaControlPreflightResult(bool IsAvailable, string DiagnosticMessage, int SessionCount)
    {
        public static MediaControlPreflightResult Pass(int sessionCount, string diagnosticMessage = "Enhanced media backend available")
        {
            return new MediaControlPreflightResult(true, diagnosticMessage, sessionCount);
        }

        public static MediaControlPreflightResult Fail(string diagnosticMessage)
        {
            return new MediaControlPreflightResult(false, diagnosticMessage, 0);
        }
    }
}
