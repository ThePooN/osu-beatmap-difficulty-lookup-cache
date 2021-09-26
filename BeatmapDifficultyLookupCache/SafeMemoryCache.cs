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

        /// <summary>
        /// Creates a new <see cref="SafeMemoryCache"/>.
        /// </summary>
        /// <param name="cache">The backing <see cref="IMemoryCache"/>.</param>
        public SafeMemoryCache(IMemoryCache cache)
        {
            this.cache = cache;
        }

        /// <summary>
        /// Attempts to retrieve a value from the cache.
        /// The value is wrapped in a <see cref="Task{T}"/> which may not immediately resolve to the final value.
        /// </summary>
        /// <param name="key">The key for the value.</param>
        /// <param name="task">The returned value, wrapped in a task. A non-null value indicates that the object is either completely available or is in the process of being created.</param>
        /// <typeparam name="T">The value type.</typeparam>
        /// <returns>Whether the value is completely available or is in the process of being created. <paramref name="task"/> cannot be <c>null</c> if this value is <c>true</c>.</returns>
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
                            // This cannot happen.
                            throw new Exception("Invalid type.");
                    }
                }
            }

            task = null;
            return false;
        }

        /// <summary>
        /// Retrieves a value from the cache, or adds it if not existent.
        /// </summary>
        /// <param name="key">The key for the value.</param>
        /// <param name="valueCreator">A function which creates the value if not existing.</param>
        /// <param name="setOptions">A function which sets the <see cref="ICacheEntry"/> parameters after the value has been created.</param>
        /// <typeparam name="T">The value type.</typeparam>
        /// <returns>A task containing the value.</returns>
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

        /// <summary>
        /// Removes a value from the cache.
        /// </summary>
        /// <param name="key">The key for the value.</param>
        public void Remove(object key)
        {
            lock (cache)
                cache.Remove(key);
        }
    }
}
