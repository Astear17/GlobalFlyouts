using System;
using System.Collections.Generic;
using System.Linq;

namespace ModernFlyouts.Core.Media.Control
{
    public sealed class MediaSessionSelectionOptions
    {
        public string PinnedAppUserModelId { get; init; } = string.Empty;

        public PinnedAppPriorityMode PinnedAppPriorityMode { get; init; } = PinnedAppPriorityMode.PreferPinnedOnlyWhenPlaying;

        public MediaAppFilteringMode AppFilteringMode { get; init; } = MediaAppFilteringMode.Disabled;

        public IReadOnlyList<string> AppFilterEntries { get; init; } = Array.Empty<string>();

        public static MediaSessionSelectionOptions FromDelimitedList(
            string pinnedAppUserModelId,
            PinnedAppPriorityMode pinnedAppPriorityMode,
            MediaAppFilteringMode appFilteringMode,
            string appFilterList)
        {
            return new MediaSessionSelectionOptions
            {
                PinnedAppUserModelId = pinnedAppUserModelId ?? string.Empty,
                PinnedAppPriorityMode = pinnedAppPriorityMode,
                AppFilteringMode = appFilteringMode,
                AppFilterEntries = SplitEntries(appFilterList)
            };
        }

        private static IReadOnlyList<string> SplitEntries(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Array.Empty<string>();
            }

            return value
                .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }
}
