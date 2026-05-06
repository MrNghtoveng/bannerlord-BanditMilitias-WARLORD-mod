using System;
using System.Collections.Generic;

namespace BanditMilitias.Infrastructure
{
    public sealed class ComputationCache<T>
    {
        private readonly Dictionary<string, (T Value, DateTime Expiry)> _cache = new();
        private readonly TimeSpan _ttl;

        public ComputationCache(TimeSpan ttl)
        {
            _ttl = ttl;
        }

        public bool TryGet(string key, out T value)
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                if (DateTime.Now < entry.Expiry)
                {
                    value = entry.Value;
                    return true;
                }
                _cache.Remove(key);
            }
            value = default!;
            return false;
        }

        public void Set(string key, T value)
        {
            _cache[key] = (value, DateTime.Now.Add(_ttl));
        }

        public void Clear() => _cache.Clear();
    }
}
