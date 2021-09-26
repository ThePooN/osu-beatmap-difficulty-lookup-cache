// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using osu.Game.Rulesets.Difficulty;

namespace BeatmapDifficultyLookupCache.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class AttributesController : Controller
    {
        private readonly DifficultyCache cache;

        public AttributesController(DifficultyCache cache)
        {
            this.cache = cache;
        }

        [HttpPost]
        public async Task<DifficultyAttributes?> Post([FromBody] DifficultyRequest request)
            => await cache.GetDifficultyAsync(request);
    }
}
