// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;

namespace BeatmapDifficultyLookupCache
{
    /// <summary>
    /// A memory cache utilising a backing <see cref="IMemoryCache"/> in an asynchronously-safe manner.
    /// </summary>
    public class SafeMemoryCache
    {
        private readonly IMemoryCache cache;

        public SafeMemoryCache(IMemoryCache cache)
        {
            this.cache = cache;
        }

        public bool TryGetValue<T>(object key, [NotNullWhen(true)] out Task<T>? task)
        {
            lock (cache)
            {
                if (cache.TryGetValue(key, out var existing))
                {
                    switch (existing)
                    {
                        case Task t:
                            task = (Task<T>)t;
                            return true;

                        case object o:
                            task = Task.FromResult((T)o);
                            return true;

                        default:
                            throw new Exception("Invalid type.");
                    }
                }
            }

            task = null;
            return false;
        }

        public Task<T> GetOrAddAsync<T>(object key, Func<Task<T>> valueCreator, Action<ICacheEntry, T> setOptions)
        {
            lock (cache)
            {
                if (TryGetValue<T>(key, out var existingTask))
                    return existingTask;

                var task = createAdditionTask();
                cache.Set(key, task);
                return task;
            }

            Task<T> createAdditionTask() => Task.Run(async () =>
            {
                var value = await valueCreator().ConfigureAwait(false);

                lock (cache)
                {
                    // Check if the entry is still in the dictionary. It could have been removed during the above value creation via a call to Remove().
                    if (!cache.TryGetValue(key, out _))
                        return value;

                    // If the entry still exists, replace it with the new value.
                    using (var entry = cache.CreateEntry(key))
                    {
                        entry.SetValue(value);
                        setOptions(entry, value);
                    }

                    return value;
                }
            });
        }

        public void Remove(object key)
        {
            lock (cache)
                cache.Remove(key);
        }
    }
}
