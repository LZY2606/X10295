using System;
using System.Threading.Tasks;

namespace CacheTower.Tests.Utils;

/// <summary>
/// An <see cref="ICacheChangeExtension"/> that records cache-stack change notifications into the
/// shared <see cref="CacheEventLog"/> so tests can assert the order of layer operations relative
/// to extension notifications.
/// </summary>
public sealed class RecordingCacheExtension : ICacheChangeExtension
{
	private readonly CacheEventLog log;

	public RecordingCacheExtension(CacheEventLog log)
	{
		this.log = log;
	}

	public void Register(ICacheStack cacheStack)
	{
	}

	public ValueTask OnCacheUpdateAsync(string cacheKey, DateTime expiry, CacheUpdateType updateType)
	{
		log.Add("EXT", $"update:{updateType}", cacheKey);
		return default;
	}

	public ValueTask OnCacheEvictionAsync(string cacheKey)
	{
		log.Add("EXT", "eviction", cacheKey);
		return default;
	}

	public ValueTask OnCacheFlushAsync()
	{
		log.Add("EXT", "flush", string.Empty);
		return default;
	}
}
