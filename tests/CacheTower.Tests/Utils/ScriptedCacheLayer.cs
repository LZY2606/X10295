using System;
using System.Threading;
using System.Threading.Tasks;
using CacheTower.Providers.Memory;
using CacheTower.Serializers;

namespace CacheTower.Tests.Utils;

/// <summary>
/// A scriptable <see cref="ICacheLayer"/> used by the state-machine and fault-injection tests.
/// It wraps a real <see cref="MemoryCacheLayer"/> (so storage semantics are the production ones),
/// records every operation into a shared <see cref="CacheEventLog"/> (used for deterministic
/// synchronization and event-order assertions) and supports scripted set-failures.
/// </summary>
public sealed class ScriptedCacheLayer : ILocalCacheLayer
{
	private readonly MemoryCacheLayer inner = new();
	private readonly CacheEventLog log;
	private int remainingSetFailures;
	private Exception setFailure = new CacheSerializationException("Scripted set failure");
	private int setAttempts;
	private int setSuccesses;

	public ScriptedCacheLayer(string name, CacheEventLog log)
	{
		Name = name;
		this.log = log;
	}

	public string Name { get; }

	/// <summary>Total number of <see cref="SetAsync{T}"/> calls attempted against this layer.</summary>
	public int SetAttempts => Volatile.Read(ref setAttempts);

	/// <summary>Number of <see cref="SetAsync{T}"/> calls that completed without a scripted failure.</summary>
	public int SetSuccesses => Volatile.Read(ref setSuccesses);

	/// <summary>
	/// Scripts the next <paramref name="count"/> set operations on this layer to throw
	/// <paramref name="failure"/> (defaults to <see cref="CacheSerializationException"/>, matching
	/// the error model that <see cref="CacheStack"/> treats as a layer-level failure).
	/// </summary>
	public void FailNextSets(int count, Exception? failure = null)
	{
		setFailure = failure ?? new CacheSerializationException("Scripted set failure");
		Volatile.Write(ref remainingSetFailures, count);
	}

	public ValueTask<CacheEntry<T>?> GetAsync<T>(string cacheKey)
	{
		log.Add(Name, "get", cacheKey);
		return inner.GetAsync<T>(cacheKey);
	}

	public async ValueTask SetAsync<T>(string cacheKey, CacheEntry<T> cacheEntry)
	{
		Interlocked.Increment(ref setAttempts);
		log.Add(Name, "set", cacheKey);
		if (Interlocked.Decrement(ref remainingSetFailures) >= 0)
		{
			log.Add(Name, "set!failed", cacheKey);
			throw setFailure;
		}
		Interlocked.Increment(ref setSuccesses);
		await inner.SetAsync(cacheKey, cacheEntry).ConfigureAwait(false);
	}

	public ValueTask EvictAsync(string cacheKey)
	{
		log.Add(Name, "evict", cacheKey);
		return inner.EvictAsync(cacheKey);
	}

	public ValueTask FlushAsync()
	{
		log.Add(Name, "flush", string.Empty);
		return inner.FlushAsync();
	}

	public ValueTask CleanupAsync()
	{
		log.Add(Name, "cleanup", string.Empty);
		return inner.CleanupAsync();
	}

	public ValueTask<bool> IsAvailableAsync(string cacheKey)
	{
		log.Add(Name, "isavailable", cacheKey);
		return inner.IsAvailableAsync(cacheKey);
	}
}
