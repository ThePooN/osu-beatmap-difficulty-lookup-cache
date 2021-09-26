// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading;
using osu.Game.Beatmaps;

namespace BeatmapDifficultyLookupCache
{
    public class CachedBeatmapEntry
    {
        public readonly WorkingBeatmap WorkingBeatmap;
        public readonly CancellationTokenSource CancellationSource;

        public CachedBeatmapEntry(WorkingBeatmap workingBeatmap)
        {
            WorkingBeatmap = workingBeatmap;
            CancellationSource = new CancellationTokenSource();
        }
    }
}
