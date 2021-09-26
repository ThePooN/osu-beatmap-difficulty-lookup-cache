// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using osu.Framework.Extensions.TypeExtensions;
using osu.Framework.IO.Network;
using osu.Game.Beatmaps;
using osu.Game.Online.API;
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

        private readonly ConcurrentDictionary<DifficultyRequest, CancellationTokenSource> requestExpirationSources = new ConcurrentDictionary<DifficultyRequest, CancellationTokenSource>();
        private readonly ConcurrentDictionary<int, CancellationTokenSource> beatmapExpirationSources = new ConcurrentDictionary<int, CancellationTokenSource>();

        private readonly IConfiguration config;
        private readonly IMemoryCache cache;
        private readonly ILogger logger;

        public DifficultyCache(IConfiguration config, IMemoryCache cache, ILogger<DifficultyCache> logger)
        {
            this.config = config;
            this.cache = cache;
            this.logger = logger;
        }

        private static readonly DifficultyAttributes empty_attributes = new DifficultyAttributes(Array.Empty<Mod>(), Array.Empty<Skill>(), -1);

        public async Task<DifficultyAttributes> GetDifficulty(DifficultyRequest request)
        {
            if (request.BeatmapId == 0)
                return empty_attributes;

            var ruleset = available_rulesets.First(r => r.RulesetInfo.ID == request.RulesetId);

            // Recreate the request, this time with any irrelevant mods trimmed out, for caching purposes.
            request = new DifficultyRequest
            {
                BeatmapId = request.BeatmapId,
                RulesetId = request.RulesetId,
                Mods = trimNonDifficultyAdjustmentMods(ruleset, request.Mods)
            };

            return await cache.GetOrCreateAsync(request, async entry =>
            {
                logger.LogInformation("Computing difficulty (beatmap: {BeatmapId}, ruleset: {RulesetId}, mods: {Mods})",
                    request.BeatmapId,
                    request.RulesetId,
                    request.Mods.Select(m => m.ToString()));

                var requestExpirationSource = requestExpirationSources[request] = new CancellationTokenSource();

                entry.SetPriority(CacheItemPriority.Normal);
                entry.AddExpirationToken(new CancellationChangeToken(requestExpirationSource.Token));

                try
                {
                    var mods = request.Mods.Select(m => m.ToMod(ruleset)).ToArray();
                    var beatmap = await getBeatmap(request.BeatmapId);

                    var difficultyCalculator = ruleset.CreateDifficultyCalculator(beatmap);
                    return difficultyCalculator.Calculate(mods);
                }
                catch (Exception e)
                {
                    entry.SetSlidingExpiration(TimeSpan.FromDays(1));
                    logger.LogWarning($"Request failed with \"{e.Message}\"");
                    return empty_attributes;
                }
            });
        }

        public void Purge(int? beatmapId, int? rulesetId)
        {
            logger.LogInformation("Purging (beatmap: {BeatmapId}, ruleset: {RulesetId})", beatmapId, rulesetId);

            foreach (var (req, source) in requestExpirationSources)
            {
                if (beatmapId != null && req.BeatmapId != beatmapId)
                    continue;

                if (rulesetId != null && req.RulesetId != rulesetId)
                    continue;

                source.Cancel();
            }

            if (beatmapId.HasValue)
            {
                if (beatmapExpirationSources.TryGetValue(beatmapId.Value, out var source))
                    source.Cancel();
            }
            else
            {
                foreach (var (_, source) in beatmapExpirationSources)
                    source.Cancel();
            }
        }

        private Task<WorkingBeatmap> getBeatmap(int beatmapId)
        {
            return cache.GetOrCreateAsync<WorkingBeatmap>($"{beatmapId}.osu", async entry =>
            {
                logger.LogInformation("Downloading beatmap ({BeatmapId})", beatmapId);

                var beatmapExpirationSource = beatmapExpirationSources[beatmapId] = new CancellationTokenSource();

                entry.SetPriority(CacheItemPriority.Low);
                entry.SetSlidingExpiration(TimeSpan.FromMinutes(1));
                entry.AddExpirationToken(new CancellationChangeToken(beatmapExpirationSource.Token));

                var req = new WebRequest(string.Format(config["Beatmaps:DownloadPath"], beatmapId))
                {
                    AllowInsecureRequests = true
                };

                await req.PerformAsync(beatmapExpirationSource.Token);

                if (req.ResponseStream.Length == 0)
                    throw new Exception($"Retrieved zero-length beatmap ({beatmapId})!");

                return new LoaderWorkingBeatmap(req.ResponseStream);
            });
        }

        /// <summary>
        /// Trims all mods from a given <see cref="Mod"/> array which do not adjust difficulty.
        /// </summary>
        // Todo: Combine with osu-tools?
        private static APIMod[] trimNonDifficultyAdjustmentMods(Ruleset ruleset, IEnumerable<APIMod> apiMods)
        {
            var dummyDifficultyCalculator = ruleset.CreateDifficultyCalculator(new DummyWorkingBeatmap(null, null)
            {
                BeatmapInfo =
                {
                    Ruleset = ruleset.RulesetInfo,
                    BaseDifficulty = new BeatmapDifficulty()
                }
            });

            var difficultyAdjustmentMods = ModUtils.FlattenMods(dummyDifficultyCalculator.CreateDifficultyAdjustmentModCombinations())
                                                   .SelectMany(m => m.GetType().EnumerateBaseTypes())
                                                   .Where(t => t.IsClass)
                                                   .Distinct()
                                                   .ToArray();

            return apiMods
                   // A few special cases... (ToMod() throws an exception here).
                   .Where(m => !string.IsNullOrWhiteSpace(m.Acronym) && m.Acronym != "ScoreV2")
                   // Instantiate the API-provided mod...
                   .Select(m => (apiMod: m, mod: m.ToMod(ruleset)))
                   // ... and check whether it derives any classes which the difficulty adjustment mods also derive.
                   .Where(kvp => difficultyAdjustmentMods.Any(t => t.IsInstanceOfType(kvp.mod)))
                   // Finally, pull out the API mod.
                   .Select(kvp => kvp.apiMod).ToArray();
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
