using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CacheTower.Tests.Utils;

/// <summary>
/// A thread-safe, ordered log of cache layer events shared between <see cref="ScriptedCacheLayer"/> instances.
/// Provides deterministic waiting on events (no real sleeps) and order assertions that report the first violation with full context.
/// </summary>
public sealed class CacheEventLog
{
	private readonly object Sync = new();
	private readonly List<string> Entries = new();
	private readonly List<KeyValuePair<string, TaskCompletionSource>> Waiters = new();

	public void Record(string entry)
	{
		lock (Sync)
		{
			Entries.Add(entry);
			for (var i = Waiters.Count - 1; i >= 0; i--)
			{
				if (Waiters[i].Key == entry)
				{
					Waiters[i].Value.TrySetResult();
					Waiters.RemoveAt(i);
				}
			}
		}
	}

	public IReadOnlyList<string> Snapshot()
	{
		lock (Sync)
		{
			return Entries.ToArray();
		}
	}

	public void Clear()
	{
		lock (Sync)
		{
			Entries.Clear();
		}
	}

	/// <summary>
	/// Waits until the given event has been recorded. Completes immediately if it already has been.
	/// </summary>
	public Task WaitForAsync(string entry)
	{
		lock (Sync)
		{
			if (Entries.Contains(entry))
			{
				return Task.CompletedTask;
			}
			var completionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			Waiters.Add(new(entry, completionSource));
			return completionSource.Task;
		}
	}

	/// <summary>
	/// Asserts the expected events appear in the recorded log in the given order (other events may be interleaved).
	/// On failure, the message pinpoints the first missing event and includes the full recorded log for context.
	/// </summary>
	public void AssertContainsInOrder(params string[] expected)
	{
		var snapshot = Snapshot();
		var position = 0;
		foreach (var expectedEntry in expected)
		{
			var found = false;
			while (position < snapshot.Count)
			{
				if (snapshot[position] == expectedEntry)
				{
					found = true;
					position++;
					break;
				}
				position++;
			}
			if (!found)
			{
				Assert.Fail(
					"Expected event '{0}' was not found in order. " +
					"Expected sequence: [{1}]. Recorded log: [{2}].",
					expectedEntry, string.Join(", ", expected), string.Join(", ", snapshot));
			}
		}
	}

	/// <summary>
	/// Asserts the recorded log contains no event equal to <paramref name="unexpected"/>.
	/// </summary>
	public void AssertDoesNotContain(string unexpected)
	{
		var snapshot = Snapshot();
		if (snapshot.Contains(unexpected))
		{
			Assert.Fail("Unexpected event '{0}' was recorded. Recorded log: [{1}].", unexpected, string.Join(", ", snapshot));
		}
	}
}
