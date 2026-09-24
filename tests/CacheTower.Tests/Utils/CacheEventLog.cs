using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace CacheTower.Tests.Utils;

/// <summary>
/// Records cache-layer and extension events into a single, stable, totally ordered log.
/// Each entry is assigned a monotonically increasing sequence number so events from multiple
/// layers/extensions can be merged into one deterministic order for assertions.
/// </summary>
public sealed class CacheEventLog
{
	private long sequence;
	private readonly ConcurrentQueue<(long Sequence, string Entry)> entries = new();

	public void Add(string source, string operation, string cacheKey)
	{
		var order = Interlocked.Increment(ref sequence);
		entries.Enqueue((order, $"{source}.{operation}({cacheKey})"));
	}

	/// <summary>
	/// Returns a stable, ordered snapshot of the recorded events.
	/// </summary>
	public IReadOnlyList<string> Snapshot()
	{
		return entries.OrderBy(entry => entry.Sequence).Select(entry => entry.Entry).ToArray();
	}

	/// <summary>
	/// Counts how many times an exact event has been recorded.
	/// </summary>
	public int CountOf(string eventName)
	{
		var count = 0;
		foreach (var entry in entries)
		{
			if (entry.Entry == eventName)
			{
				count++;
			}
		}
		return count;
	}

	public void Clear()
	{
		while (entries.TryDequeue(out _)) { }
	}

	public string Describe() => string.Join(Environment.NewLine, Snapshot());
}
