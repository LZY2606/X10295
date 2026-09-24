using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CacheTower.Tests.Utils;

/// <summary>
/// Deterministic synchronization helpers for tests. Waiting is event-driven (no real sleeps):
/// the timeout only guards against a broken implementation hanging the test run.
/// </summary>
public static class TestFlow
{
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

	/// <summary>
	/// Waits until <paramref name="eventName"/> has been recorded at least <paramref name="count"/> times.
	/// </summary>
	public static async Task WaitForEventCountAsync(CacheEventLog log, string eventName, int count, string context)
	{
		var stopwatch = Stopwatch.StartNew();
		while (log.CountOf(eventName) < count)
		{
			if (stopwatch.Elapsed > Timeout)
			{
				Assert.Fail($"Timed out waiting for event '{eventName}' x{count} ({context}). Events so far:{Environment.NewLine}{log.Describe()}");
			}
			await Task.Yield();
		}
	}

	/// <summary>
	/// Waits for a signal that is expected to complete. The timeout is only a deadlock guard, never a synchronization mechanism.
	/// </summary>
	public static async Task AwaitSignalAsync(Task signal, string context)
	{
		var completed = await Task.WhenAny(signal, Task.Delay(Timeout)).ConfigureAwait(false);
		Assert.AreSame(signal, completed, $"Timed out waiting for signal: {context}");
		await signal; // propagate any exception from the signal itself
	}
}
