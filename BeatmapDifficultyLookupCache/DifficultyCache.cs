// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using osu.Framework.IO.Network;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Difficulty.Skills;
using osu.Game.Rulesets.Mods;
using osu.Game.Utils;

namespace BeatmapDifficultyLookupCache
{
    public class DifficultyCache
    {
        private static readonly List<Ruleset> available_rulesets = getRulesets();

        private readonly IConfiguration config;
        private readonly SafeMemoryCache cache;
        private readonly ILogger logger;

        public DifficultyCache(IConfiguration config, SafeMemoryCache cache, ILogger<DifficultyCache> logger)
        {
            this.config = config;
            this.cache = cache;
            this.logger = logger;
        }

        private static readonly DifficultyAttributes empty_attributes = new DifficultyAttributes(Array.Empty<Mod>(), Array.Empty<Skill>(), -1);

        public async Task<DifficultyAttributes> GetDifficultyAsync(DifficultyRequest request)
        {
            if (request.BeatmapId == 0)
                return empty_attributes;

            CachedBeatmapEntry? beatmapEntry = null;

            return await cache.GetOrAddAsync(getRequestKey(request), async () =>
            {
                try
                {
                    beatmapEntry = await getBeatmap(request.BeatmapId);

                    var ruleset = available_rulesets.First(r => r.RulesetInfo.ID == request.RulesetId);
                    var mods = request.Mods.Select(m => m.ToMod(ruleset)).ToArray();
                    var difficultyCalculator = ruleset.CreateDifficultyCalculator(beatmapEntry.WorkingBeatmap);

                    logger.LogInformation("Computing difficulty (beatmap: {BeatmapId}, ruleset: {RulesetId}, mods: {Mods})",
                        request.BeatmapId,
                        request.RulesetId,
                        request.Mods.Select(m => m.ToString()));

                    return difficultyCalculator.Calculate(mods);
                }
                catch (Exception e)
                {
                    logger.LogWarning("Request failed with \"{Message}\"", e.Message);
                    return empty_attributes;
                }
            }, (e, value) =>
            {
                e.SetPriority(CacheItemPriority.Normal);

                if (value == empty_attributes)
                    e.SetSlidingExpiration(TimeSpan.FromDays(1));

                if (beatmapEntry != null)
                    e.AddExpirationToken(new CancellationChangeToken(beatmapEntry.CancellationSource.Token));
            });
        }

        public async Task PurgeAsync(int beatmapId)
        {
            logger.LogInformation("Purging (beatmap: {BeatmapId})", beatmapId);

            if (cache.TryGetValue<CachedBeatmapEntry>(getBeatmapKey(beatmapId), out var task))
            {
                // Expire the beatmap and any associated difficulty attributes.
                (await task).CancellationSource.Cancel();

                cache.Remove(getBeatmapKey(beatmapId));
            }
        }

        private async Task<CachedBeatmapEntry> getBeatmap(int beatmapId) => await cache.GetOrAddAsync(getBeatmapKey(beatmapId), async () =>
        {
            logger.LogInformation("Downloading beatmap ({BeatmapId})", beatmapId);

            var req = new WebRequest(string.Format(config["Beatmaps:DownloadPath"], beatmapId))
            {
                AllowInsecureRequests = true
            };

            await req.PerformAsync();

            if (req.ResponseStream.Length == 0)
                throw new Exception($"Retrieved zero-length beatmap ({beatmapId})!");

            return new CachedBeatmapEntry(new LoaderWorkingBeatmap(req.ResponseStream));
        }, (e, value) =>
        {
            e.SetPriority(CacheItemPriority.Low);
            e.SetSlidingExpiration(TimeSpan.FromMinutes(1));
            e.AddExpirationToken(new CancellationChangeToken(value.CancellationSource.Token));
        });

        private static string getBeatmapKey(int beatmapId) => $"beatmap: {beatmapId}";

        private static string getRequestKey(DifficultyRequest request)
        {
            var hashCode = new HashCode();

            hashCode.Add(request.BeatmapId);
            hashCode.Add(request.RulesetId);

            // Todo: Temporary (APIMod doesn't implement GetHashCode()).
            foreach (var m in request.Mods.OrderBy(m => m.Acronym))
            {
                hashCode.Add(m.Acronym);

                foreach (var (key, value) in m.Settings.OrderBy(s => s.Key))
                {
                    hashCode.Add(key);
                    hashCode.Add(ModUtils.GetSettingUnderlyingValue(value));
                }
            }

            return $"request: {hashCode.ToHashCode()}";
        }

        private static List<Ruleset> getRulesets()
        {
            const string ruleset_library_prefix = "osu.Game.Rulesets";

            var rulesetsToProcess = new List<Ruleset>();

            foreach (string file in Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, $"{ruleset_library_prefix}.*.dll"))
            {
                try
                {
                    var assembly = Assembly.LoadFrom(file);
                    Type type = assembly.GetTypes().First(t => t.IsPublic && t.IsSubclassOf(typeof(Ruleset)));
                    rulesetsToProcess.Add((Ruleset)Activator.CreateInstance(type)!);
                }
                catch
                {
                    throw new Exception($"Failed to load ruleset ({file})");
                }
            }

            return rulesetsToProcess;
        }
    }
}
