using System;
using System.Threading;
using System.Threading.Tasks;

namespace CacheTower.Tests.Utils;

/// <summary>
/// A value factory wrapper that counts invocations (to catch duplicate factory runs),
/// can be blocked and released on demand, and can be scripted to fail a number of times.
/// </summary>
public sealed class CountingFactory<T>
{
	private readonly Func<T, T> nextValue;
	private int callCount;
	private int remainingFailures;
	private Exception failure = new InvalidOperationException("Scripted factory failure");
	private TaskCompletionSource<object?>? gate;

	public CountingFactory(Func<T, T> nextValue)
	{
		this.nextValue = nextValue;
	}

	/// <summary>How many times the factory delegate has been invoked.</summary>
	public int CallCount => Volatile.Read(ref callCount);

	/// <summary>Completes when the factory delegate is entered (only valid after <see cref="Arm"/>).</summary>
	public Task Entered { get; private set; } = Task.CompletedTask;

	/// <summary>
	/// Arms the factory: the next invocation blocks inside the factory until <see cref="Release"/> is called.
	/// </summary>
	public void Arm()
	{
		gate = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
		Entered = gate.Task;
	}

	/// <summary>Releases a factory invocation blocked by <see cref="Arm"/>.</summary>
	public void Release() => gate?.TrySetResult(null);

	/// <summary>
	/// Scripts the next <paramref name="count"/> factory invocations to throw
	/// <paramref name="exception"/> (defaults to <see cref="InvalidOperationException"/>).
	/// </summary>
	public void FailNext(int count, Exception? exception = null)
	{
		failure = exception ?? new InvalidOperationException("Scripted factory failure");
		Volatile.Write(ref remainingFailures, count);
	}

	/// <summary>The factory delegate to pass to <c>GetOrSetAsync</c>.</summary>
	public Func<T, Task<T>> Factory => async oldValue =>
	{
		Interlocked.Increment(ref callCount);
		Entered.TrySetResult(null);
		if (gate is not null)
		{
			await gate.Task.ConfigureAwait(false);
		}
		if (Interlocked.Decrement(ref remainingFailures) >= 0)
		{
			throw failure;
		}
		return nextValue(oldValue);
	};
}
