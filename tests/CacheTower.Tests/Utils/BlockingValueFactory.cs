using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CacheTower.Tests.Utils;

/// <summary>
/// A value factory for <see cref="ICacheStack.GetOrSetAsync{T}"/> that counts invocations,
/// records the old values it was called with, and can be scripted to block or fail.
/// Enables deterministic concurrency tests without real sleeps and duplicate-factory-call assertions.
/// </summary>
/// <typeparam name="T">The cached value type.</typeparam>
public sealed class BlockingValueFactory<T>
{
	private readonly object Sync = new();
	private readonly Func<int, T> ValueSource;
	private readonly Queue<Exception> Failures = new();
	private readonly Queue<OperationGate> Gates = new();
	private readonly List<T> ObservedOldValuesList = new();
	private int CallCountField;

	public BlockingValueFactory(Func<int, T> valueSource)
	{
		ValueSource = valueSource;
	}

	/// <summary>
	/// How many times the factory has been invoked. Used to catch duplicate factory calls.
	/// </summary>
	public int CallCount => Volatile.Read(ref CallCountField);

	/// <summary>
	/// The old values the factory was called with, in call order.
	/// </summary>
	public IReadOnlyList<T> ObservedOldValues
	{
		get
		{
			lock (Sync)
			{
				return ObservedOldValuesList.ToArray();
			}
		}
	}

	/// <summary>
	/// The next call to the factory throws <paramref name="exception"/>.
	/// </summary>
	public void FailNextCall(Exception exception)
	{
		lock (Sync)
		{
			Failures.Enqueue(exception);
		}
	}

	/// <summary>
	/// Blocks the next call to the factory behind a gate.
	/// </summary>
	public OperationGate BlockNextCall()
	{
		var gate = new OperationGate();
		lock (Sync)
		{
			Gates.Enqueue(gate);
		}
		return gate;
	}

	/// <summary>
	/// The factory delegate to pass to <see cref="ICacheStack.GetOrSetAsync{T}"/>.
	/// </summary>
	public async Task<T> Factory(T oldValue)
	{
		var callNumber = Interlocked.Increment(ref CallCountField);
		Exception failure;
		OperationGate gate;
		lock (Sync)
		{
			ObservedOldValuesList.Add(oldValue);
			failure = Failures.Count > 0 ? Failures.Dequeue() : null;
			gate = Gates.Count > 0 ? Gates.Dequeue() : null;
		}
		if (gate != null)
		{
			await gate.WaitAsync().ConfigureAwait(false);
		}
		if (failure != null)
		{
			throw failure;
		}
		return ValueSource(callNumber);
	}
}
