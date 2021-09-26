// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading;
using osu.Game.Beatmaps;

namespace BeatmapDifficultyLookupCache
{
    /// <summary>
    /// An entry stored to the cache which contains a <see cref="osu.Game.Beatmaps.WorkingBeatmap"/>,
    /// and a <see cref="CancellationTokenSource"/> representing the <see cref="osu.Game.Beatmaps.WorkingBeatmap"/>'s lifetime.
    /// </summary>
    public class CachedBeatmapEntry
    {
        /// <summary>
        /// The <see cref="osu.Game.Beatmaps.WorkingBeatmap"/>.
        /// </summary>
        public readonly WorkingBeatmap WorkingBeatmap;

        /// <summary>
        /// The <see cref="CancellationTokenSource"/>.
        /// Other entries stored to the cache should take on this <see cref="CancellationTokenSource"/> for expiry.
        /// </summary>
        public readonly CancellationTokenSource CancellationSource;

        public CachedBeatmapEntry(WorkingBeatmap workingBeatmap)
        {
            WorkingBeatmap = workingBeatmap;
            CancellationSource = new CancellationTokenSource();
        }
    }
}
