using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CacheTower.Providers.Memory;

namespace CacheTower.Tests.Utils;

/// <summary>
/// A gate that blocks a scripted cache layer operation until the test releases it.
/// Allows deterministic concurrency control without real sleeps.
/// </summary>
public sealed class OperationGate
{
	private readonly TaskCompletionSource EnteredSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly TaskCompletionSource ReleaseSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>
	/// Completes when the gated operation has been entered.
	/// </summary>
	public Task Entered => EnteredSource.Task;

	internal async Task WaitAsync()
	{
		EnteredSource.TrySetResult();
		await ReleaseSource.Task.ConfigureAwait(false);
	}

	/// <summary>
	/// Allows the gated operation to complete.
	/// </summary>
	public void Release() => ReleaseSource.TrySetResult();

	/// <summary>
	/// Makes the gated operation fail with the given exception.
	/// </summary>
	public void Fail(Exception exception) => ReleaseSource.TrySetException(exception);
}

/// <summary>
/// A scriptable <see cref="ICacheLayer"/> for state machine and concurrency tests.
/// It delegates real storage to the production <see cref="MemoryCacheLayer"/> (so cached values,
/// expiries and type handling are the genuine production behavior) while adding:
/// an ordered event log, scripted failures and blocking gates on individual operations.
/// </summary>
public sealed class ScriptedCacheLayer : ILocalCacheLayer
{
	private readonly MemoryCacheLayer Inner = new();
	private readonly CacheEventLog EventLog;
	private readonly object Sync = new();
	private readonly Queue<Exception> SetFailures = new();
	private readonly Queue<Exception> GetFailures = new();
	private readonly Queue<OperationGate> SetGates = new();
	private readonly Queue<OperationGate> GetGates = new();

	public ScriptedCacheLayer(string name, CacheEventLog eventLog)
	{
		Name = name;
		EventLog = eventLog;
	}

	public string Name { get; }

	/// <summary>
	/// Controls the result of <see cref="IsAvailableAsync"/>.
	/// </summary>
	public bool IsAvailable { get; set; } = true;

	private string EventName(string operation, string cacheKey) => $"{Name}:{operation}:{cacheKey}";

	/// <summary>
	/// Waits until this layer has recorded the given operation for the given key.
	/// </summary>
	public Task WaitForAsync(string operation, string cacheKey) => EventLog.WaitForAsync(EventName(operation, cacheKey));

	/// <summary>
	/// The next <paramref name="count"/> <see cref="SetAsync{T}"/> calls on this layer throw <paramref name="exception"/>.
	/// </summary>
	public void FailNextSets(Exception exception, int count = 1)
	{
		lock (Sync)
		{
			for (var i = 0; i < count; i++)
			{
				SetFailures.Enqueue(exception);
			}
		}
	}

	/// <summary>
	/// The next <paramref name="count"/> <see cref="GetAsync{T}"/> calls on this layer throw <paramref name="exception"/>.
	/// </summary>
	public void FailNextGets(Exception exception, int count = 1)
	{
		lock (Sync)
		{
			for (var i = 0; i < count; i++)
			{
				GetFailures.Enqueue(exception);
			}
		}
	}

	/// <summary>
	/// Blocks the next <see cref="SetAsync{T}"/> call on this layer behind a gate.
	/// </summary>
	public OperationGate BlockNextSet() => EnqueueGate(SetGates);

	/// <summary>
	/// Blocks the next <see cref="GetAsync{T}"/> call on this layer behind a gate.
	/// </summary>
	public OperationGate BlockNextGet() => EnqueueGate(GetGates);

	private OperationGate EnqueueGate(Queue<OperationGate> gates)
	{
		var gate = new OperationGate();
		lock (Sync)
		{
			gates.Enqueue(gate);
		}
		return gate;
	}

	private Exception DequeueFailure(Queue<Exception> failures)
	{
		lock (Sync)
		{
			return failures.Count > 0 ? failures.Dequeue() : null;
		}
	}

	private OperationGate DequeueGate(Queue<OperationGate> gates)
	{
		lock (Sync)
		{
			return gates.Count > 0 ? gates.Dequeue() : null;
		}
	}

	/// <inheritdoc/>
	public ValueTask FlushAsync()
	{
		EventLog.Record(EventName("Flush", "*"));
		return Inner.FlushAsync();
	}

	/// <inheritdoc/>
	public ValueTask CleanupAsync()
	{
		EventLog.Record(EventName("Cleanup", "*"));
		return Inner.CleanupAsync();
	}

	/// <inheritdoc/>
	public ValueTask EvictAsync(string cacheKey)
	{
		EventLog.Record(EventName("Evict", cacheKey));
		return Inner.EvictAsync(cacheKey);
	}

	/// <inheritdoc/>
	public async ValueTask<CacheEntry<T>?> GetAsync<T>(string cacheKey)
	{
		EventLog.Record(EventName("Get", cacheKey));
		var failure = DequeueFailure(GetFailures);
		if (failure != null)
		{
			throw failure;
		}
		var gate = DequeueGate(GetGates);
		if (gate != null)
		{
			await gate.WaitAsync().ConfigureAwait(false);
		}
		return await Inner.GetAsync<T>(cacheKey).ConfigureAwait(false);
	}

	/// <inheritdoc/>
	public async ValueTask SetAsync<T>(string cacheKey, CacheEntry<T> cacheEntry)
	{
		EventLog.Record(EventName("Set", cacheKey));
		var failure = DequeueFailure(SetFailures);
		if (failure != null)
		{
			throw failure;
		}
		var gate = DequeueGate(SetGates);
		if (gate != null)
		{
			await gate.WaitAsync().ConfigureAwait(false);
		}
		await Inner.SetAsync(cacheKey, cacheEntry).ConfigureAwait(false);
	}

	/// <inheritdoc/>
	public ValueTask<bool> IsAvailableAsync(string cacheKey)
	{
		return new ValueTask<bool>(IsAvailable);
	}
}
