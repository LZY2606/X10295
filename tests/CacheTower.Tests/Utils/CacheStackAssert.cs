using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CacheTower.Tests.Utils;

/// <summary>
/// Assertion helpers for the layered cache state-machine tests. Failure messages keep the first
/// violating position plus the full event log / layer context, so a breach is diagnosable from
/// the test output alone.
/// </summary>
public static class CacheStackAssert
{
	/// <summary>
	/// Asserts the recorded events exactly match the expected sequence (order and count).
	/// </summary>
	public static void EventOrder(IReadOnlyList<string> actual, string context, params string[] expected)
	{
		CollectionAssert.AreEqual(expected, new List<string>(actual),
			$"Event order mismatch ({context}).{Environment.NewLine}Expected:{Environment.NewLine}{string.Join(Environment.NewLine, expected)}{Environment.NewLine}Actual:{Environment.NewLine}{string.Join(Environment.NewLine, actual)}");
	}

	/// <summary>
	/// Asserts the recorded events contain the expected events as an in-order subsequence.
	/// </summary>
	public static void ContainsInOrder(IReadOnlyList<string> actual, string context, params string[] expectedSubsequence)
	{
		var matched = 0;
		foreach (var entry in actual)
		{
			if (matched < expectedSubsequence.Length && entry == expectedSubsequence[matched])
			{
				matched++;
			}
		}

		Assert.AreEqual(expectedSubsequence.Length, matched,
			$"Expected in-order subsequence not found ({context}). First unmatched: '{(matched < expectedSubsequence.Length ? expectedSubsequence[matched] : "<none>")}'.{Environment.NewLine}Expected subsequence:{Environment.NewLine}{string.Join(Environment.NewLine, expectedSubsequence)}{Environment.NewLine}Actual:{Environment.NewLine}{string.Join(Environment.NewLine, actual)}");
	}

	/// <summary>
	/// Asserts a layer holds an entry for <paramref name="cacheKey"/> with the expected value and expiry.
	/// </summary>
	public static async Task LayerEntry<T>(ScriptedCacheLayer layer, string cacheKey, T expectedValue, DateTime expectedExpiry, string context)
	{
		var entry = await layer.GetAsync<T>(cacheKey);
		Assert.IsNotNull(entry, $"[{context}] {layer.Name} should contain '{cacheKey}'");
		Assert.AreEqual(expectedValue, entry.Value, $"[{context}] {layer.Name} value mismatch for '{cacheKey}'");
		Assert.AreEqual(expectedExpiry, entry.Expiry, $"[{context}] {layer.Name} expiry mismatch for '{cacheKey}'");
	}
}
