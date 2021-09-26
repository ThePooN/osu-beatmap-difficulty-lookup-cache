// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;

namespace BeatmapDifficultyLookupCache
{
    public class SafeMemoryCache
    {
        private readonly Dictionary<object, Task> inProgressTasks = new Dictionary<object, Task>();

        private readonly IMemoryCache cache;

        public SafeMemoryCache(IMemoryCache cache)
        {
            this.cache = cache;
        }

        public bool TryGetValue<T>(object key, [NotNullWhen(true)] out Task<T>? task)
        {
            lock (inProgressTasks)
            {
                if (inProgressTasks.TryGetValue(key, out var existing))
                {
                    task = (Task<T>)existing;
                    return true;
                }

                if (cache.TryGetValue(key, out var cached))
                {
                    task = Task.FromResult((T)cached);
                    return true;
                }

                task = null;
                return false;
            }
        }

        public Task<T> GetOrAddAsync<T>(object key, Func<Task<T>> valueCreator, Action<ICacheEntry, T> setOptions)
        {
            lock (inProgressTasks)
            {
                if (TryGetValue<T>(key, out var existingTask))
                    return existingTask;

                var task = createAdditionTask();
                inProgressTasks[key] = task;
                return task;
            }

            Task<T> createAdditionTask() => Task.Run(async () =>
            {
                var value = await valueCreator().ConfigureAwait(false);

                lock (inProgressTasks)
                {
                    if (!inProgressTasks.ContainsKey(key))
                        return value;

                    using (var entry = cache.CreateEntry(key))
                    {
                        entry.SetValue(value);
                        setOptions(entry, value);
                    }

                    inProgressTasks.Remove(key);

                    return value;
                }
            });
        }

        public void Remove(object key)
        {
            lock (inProgressTasks)
            {
                inProgressTasks.Remove(key);
                cache.Remove(key);
            }
        }
    }
}
