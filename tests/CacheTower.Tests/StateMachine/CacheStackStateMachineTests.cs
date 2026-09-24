using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CacheTower.Tests.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CacheTower.Tests.StateMachine;

/// <summary>
/// Acceptance story for the cache tower state machine: built on a fake clock, blockable factories
/// and scripted cache layers, covering interleavings of read-through, write-through, expiry,
/// refresh, eviction and flushing. No real sleeps are used; every operation is followed by checks
/// of each layer's value, expiry and the event order.
/// </summary>
[TestClass]
public class CacheStackStateMachineTests : TestBase
{
	private static readonly DateTime TimeOrigin = new(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc);

	private static async Task AssertLayerEntry<T>(ScriptedCacheLayer layer, string cacheKey, T expectedValue, DateTime expectedExpiry)
	{
		var entry = await layer.GetAsync<T>(cacheKey);
		Assert.IsNotNull(entry, $"Expected {layer.Name} to hold an entry for {cacheKey}.");
		Assert.AreEqual(expectedValue, (T)entry.Value, $"Wrong value in {layer.Name} for {cacheKey}.");
		Assert.AreEqual(expectedExpiry, entry.Expiry, $"Wrong expiry in {layer.Name} for {cacheKey}.");
	}

	private static async Task AssertLayersEmpty(ScriptedCacheLayer layer1, ScriptedCacheLayer layer2, string cacheKey)
	{
		Assert.IsNull(await layer1.GetAsync<string>(cacheKey), $"Layer1 must not hold {cacheKey}.");
		Assert.IsNull(await layer2.GetAsync<string>(cacheKey), $"Layer2 must not hold {cacheKey}.");
	}

	[TestMethod]
	public async Task StateMachine_ReadThroughWriteThroughExpiryRefreshEvictFlush()
	{
		using var clock = new FrozenClock(TimeOrigin);
		var eventLog = new CacheEventLog();
		var layer1 = new ScriptedCacheLayer("Layer1", eventLog);
		var layer2 = new ScriptedCacheLayer("Layer2", eventLog);
		await using var cacheStack = new CacheStack(null, new(new ICacheLayer[] { layer1, layer2 }));
		const string cacheKey = nameof(StateMachine_ReadThroughWriteThroughExpiryRefreshEvictFlush);
		var settings = new CacheSettings(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5));
		var factory = new BlockingValueFactory<string>(call => $"v{call}");

		//Step 1: read-through on a miss populates every layer
		var result = await cacheStack.GetOrSetAsync<string>(cacheKey, factory.Factory, settings);
		Assert.AreEqual("v1", result);
		Assert.AreEqual(1, factory.CallCount);
		Assert.AreEqual(default(string), factory.ObservedOldValues[0], "A miss must pass the default old value to the factory.");
		await AssertLayerEntry(layer1, cacheKey, "v1", TimeOrigin.AddMinutes(10));
		await AssertLayerEntry(layer2, cacheKey, "v1", TimeOrigin.AddMinutes(10));
		eventLog.AssertContainsInOrder(
			$"Layer1:Get:{cacheKey}", $"Layer2:Get:{cacheKey}",
			$"Layer1:Set:{cacheKey}", $"Layer2:Set:{cacheKey}");

		//Step 2: memory hit is served by the top layer only
		eventLog.Clear();
		result = await cacheStack.GetOrSetAsync<string>(cacheKey, factory.Factory, settings);
		Assert.AreEqual("v1", result);
		Assert.AreEqual(1, factory.CallCount, "A memory hit must not run the factory.");
		eventLog.AssertContainsInOrder($"Layer1:Get:{cacheKey}");
		eventLog.AssertDoesNotContain($"Layer2:Get:{cacheKey}");

		//Step 3: advancing the virtual clock past the stale point serves the stale value
		//and refreshes in the background
		clock.Advance(TimeSpan.FromMinutes(6));
		eventLog.Clear();
		var refreshGate = factory.BlockNextCall();
		result = await cacheStack.GetOrSetAsync<string>(cacheKey, factory.Factory, settings);
		Assert.AreEqual("v1", result, "A stale hit must return the stale value to the caller.");
		await refreshGate.Entered;
		Assert.AreEqual("v1", factory.ObservedOldValues[1], "The background refresh must receive the stale value as old value.");
		refreshGate.Release();
		await layer1.WaitForAsync("Set", cacheKey);
		Assert.AreEqual(2, factory.CallCount, "The stale hit must trigger exactly one background refresh.");
		await AssertLayerEntry(layer1, cacheKey, "v2", TimeOrigin.AddMinutes(16));
		await AssertLayerEntry(layer2, cacheKey, "v2", TimeOrigin.AddMinutes(16));
		eventLog.AssertContainsInOrder($"Layer1:Get:{cacheKey}", $"Layer1:Set:{cacheKey}", $"Layer2:Set:{cacheKey}");

		//Step 4: advancing past expiry forces a foreground refresh
		clock.Advance(TimeSpan.FromMinutes(11));
		eventLog.Clear();
		result = await cacheStack.GetOrSetAsync<string>(cacheKey, factory.Factory, settings);
		Assert.AreEqual("v3", result, "An expired entry must be refreshed in the foreground.");
		Assert.AreEqual(3, factory.CallCount);
		await AssertLayerEntry(layer1, cacheKey, "v3", TimeOrigin.AddMinutes(27));
		await AssertLayerEntry(layer2, cacheKey, "v3", TimeOrigin.AddMinutes(27));

		//Step 5: write-through updates every layer
		eventLog.Clear();
		await cacheStack.SetAsync(cacheKey, "v4", TimeSpan.FromMinutes(20));
		await AssertLayerEntry(layer1, cacheKey, "v4", TimeOrigin.AddMinutes(37));
		await AssertLayerEntry(layer2, cacheKey, "v4", TimeOrigin.AddMinutes(37));
		eventLog.AssertContainsInOrder($"Layer1:Set:{cacheKey}", $"Layer2:Set:{cacheKey}");
		Assert.AreEqual(3, factory.CallCount, "Write-through must not involve the factory.");

		//Step 6: eviction removes the entry from every layer
		eventLog.Clear();
		await cacheStack.EvictAsync(cacheKey);
		await AssertLayersEmpty(layer1, layer2, cacheKey);
		eventLog.AssertContainsInOrder($"Layer1:Evict:{cacheKey}", $"Layer2:Evict:{cacheKey}");

		//Step 7: flush empties every layer
		eventLog.Clear();
		await cacheStack.SetAsync(cacheKey, "v5", TimeSpan.FromMinutes(5));
		await cacheStack.FlushAsync();
		await AssertLayersEmpty(layer1, layer2, cacheKey);
		eventLog.AssertContainsInOrder($"Layer1:Flush:*", $"Layer2:Flush:*");
		Assert.AreEqual(3, factory.CallCount, "Eviction and flushing must not involve the factory.");
	}

	[TestMethod]
	public async Task StateMachine_RecoverableFailureMidSequence_RetryYieldsStableResults()
	{
		var cleanRun = await RunScriptedSequence(injectFailure: false);
		var recoveredRun = await RunScriptedSequence(injectFailure: true);

		Assert.AreEqual(2, cleanRun.FactoryCallCount, "Clean run calls the factory once for the miss and once for the expired read-through.");
		Assert.AreEqual(3, recoveredRun.FactoryCallCount, "The recovered run must count the failed attempt plus the retry.");
		Assert.AreEqual(cleanRun.Result, recoveredRun.Result, "The caller-visible result after recovery must match the clean run.");
		CollectionAssert.AreEqual(cleanRun.FinalState, recoveredRun.FinalState,
			"Recovered run must reach the same per-layer terminal state as the clean run. " +
			$"Clean: [{string.Join(", ", cleanRun.FinalState)}]. Recovered: [{string.Join(", ", recoveredRun.FinalState)}].");
		CollectionAssert.AreEqual(cleanRun.EventsAfterRecoveryPoint, recoveredRun.EventsAfterRecoveryPoint,
			"Event order after the recovery point must be stable. " +
			$"Clean: [{string.Join(", ", cleanRun.EventsAfterRecoveryPoint)}]. Recovered: [{string.Join(", ", recoveredRun.EventsAfterRecoveryPoint)}].");
	}

	private sealed record ScriptedSequenceResult(string Result, int FactoryCallCount, string[] FinalState, string[] EventsAfterRecoveryPoint);

	private static async Task<ScriptedSequenceResult> RunScriptedSequence(bool injectFailure)
	{
		using var clock = new FrozenClock(TimeOrigin);
		var eventLog = new CacheEventLog();
		var layer1 = new ScriptedCacheLayer("Layer1", eventLog);
		var layer2 = new ScriptedCacheLayer("Layer2", eventLog);
		await using var cacheStack = new CacheStack(null, new(new ICacheLayer[] { layer1, layer2 }));
		var settings = new CacheSettings(TimeSpan.FromMinutes(30));
		var factory = new BlockingValueFactory<string>(call => "unstable-value");
		const string cacheKey = "StateMachine_RecoverableFailure";

		//Phase 1: establish persistent state
		await cacheStack.GetOrSetAsync<string>(cacheKey, factory.Factory, settings);
		clock.Advance(TimeSpan.FromMinutes(10));
		eventLog.Clear();

		//Phase 2: the recoverable failure happens in the middle of the sequence
		if (injectFailure)
		{
			factory.FailNextCall(new InvalidOperationException("Scripted recoverable failure"));
		}
		clock.Advance(TimeSpan.FromMinutes(25));

		string result;
		if (injectFailure)
		{
			//The entry is now expired, so the read-through runs the factory, which fails once
			await Assert.ThrowsExceptionAsync<InvalidOperationException>(
				async () => await cacheStack.GetOrSetAsync<string>(cacheKey, factory.Factory, settings));

			//The failure must not have half-updated the persistent state: both layers still
			//hold the previous, mutually consistent entry
			var layer1Entry = await layer1.GetAsync<string>(cacheKey);
			var layer2Entry = await layer2.GetAsync<string>(cacheKey);
			Assert.IsNotNull(layer1Entry, "Failed refresh must not clear the previous entry in layer 1.");
			Assert.IsNotNull(layer2Entry, "Failed refresh must not clear the previous entry in layer 2.");
			Assert.AreEqual(layer1Entry.Value, layer2Entry.Value, "Layers must remain mutually consistent after the failure.");
			Assert.AreEqual(layer1Entry.Expiry, layer2Entry.Expiry, "Layer expiries must remain aligned after the failure.");
			eventLog.AssertDoesNotContain($"Layer1:Set:{cacheKey}");

			//Retry with the exact same input
			result = await cacheStack.GetOrSetAsync<string>(cacheKey, factory.Factory, settings);
		}
		else
		{
			result = await cacheStack.GetOrSetAsync<string>(cacheKey, factory.Factory, settings);
		}

		//Phase 3: continue the sequence after the recovery point
		eventLog.Clear();
		await cacheStack.SetAsync(cacheKey, "final-value", TimeSpan.FromMinutes(30));
		await cacheStack.EvictAsync(cacheKey);
		var eventsAfterRecovery = eventLog.Snapshot().ToArray();

		var finalState = new[]
		{
			$"Layer1:{(await layer1.GetAsync<string>(cacheKey))?.Value ?? "<null>"}",
			$"Layer2:{(await layer2.GetAsync<string>(cacheKey))?.Value ?? "<null>"}"
		};
		return new(result, factory.CallCount, finalState, eventsAfterRecovery);
	}

	[TestMethod]
	[DataRow(20260924)]
	[DataRow(7)]
	[DataRow(123456)]
	public async Task StateMachine_SeededInterleaving_MatchesModel(int seed)
	{
		using var clock = new FrozenClock(TimeOrigin);
		var eventLog = new CacheEventLog();
		var layer1 = new ScriptedCacheLayer("Layer1", eventLog);
		var layer2 = new ScriptedCacheLayer("Layer2", eventLog);
		await using var cacheStack = new CacheStack(null, new(new ICacheLayer[] { layer1, layer2 }));

		var random = new Random(seed);
		var keys = new[] { "alpha", "beta", "gamma" };
		var timeToLive = TimeSpan.FromMinutes(30);
		var settings = new CacheSettings(timeToLive);
		var factory = new BlockingValueFactory<int>(call => 1000 + call);
		var model = new Dictionary<string, CacheEntry<int>>(StringComparer.Ordinal);
		var history = new List<string>();
		var nextSetValue = 0;
		var expectedFactoryCalls = 0;

		string ReproContext() =>
			$"Seed={seed}. Operation history: {string.Join(" | ", history)}. " +
			$"Minimal repro: new Random({seed}) and replay the same operation sequence.";

		for (var step = 0; step < 200; step++)
		{
			var cacheKey = keys[random.Next(keys.Length)];
			var operation = random.Next(6);
			switch (operation)
			{
				case 0: //write-through
				{
					var value = ++nextSetValue;
					history.Add($"Set({cacheKey},{value})");
					await cacheStack.SetAsync(cacheKey, value, timeToLive);
					model[cacheKey] = new CacheEntry<int>(value, clock.Now.Add(timeToLive));
					break;
				}
				case 1:
				case 2: //read-through
				{
					history.Add($"GetOrSet({cacheKey})");
					var result = await cacheStack.GetOrSetAsync<int>(cacheKey, factory.Factory, settings);
					if (model.TryGetValue(cacheKey, out var existing) && existing.Expiry > clock.Now)
					{
						Assert.AreEqual(existing.Value, result, $"Read-through returned a wrong value. {ReproContext()}");
					}
					else
					{
						expectedFactoryCalls++;
						Assert.AreEqual(1000 + expectedFactoryCalls, result, $"Read-through must use the factory on miss/expiry. {ReproContext()}");
						model[cacheKey] = new CacheEntry<int>(result, clock.Now.Add(timeToLive));
					}
					break;
				}
				case 3: //evict
				{
					history.Add($"Evict({cacheKey})");
					await cacheStack.EvictAsync(cacheKey);
					model.Remove(cacheKey);
					break;
				}
				case 4: //flush
				{
					history.Add("Flush()");
					await cacheStack.FlushAsync();
					model.Clear();
					break;
				}
				case 5: //advance the virtual clock, sometimes cleaning up expired entries
				{
					var minutes = random.Next(1, 61);
					history.Add($"Advance({minutes}m)");
					clock.Advance(TimeSpan.FromMinutes(minutes));
					if (random.Next(3) == 0)
					{
						history[^1] += "+Cleanup()";
						await cacheStack.CleanupAsync();
						foreach (var key in model.Where(pair => pair.Value.Expiry < clock.Now).Select(pair => pair.Key).ToArray())
						{
							model.Remove(key);
						}
					}
					break;
				}
			}

			Assert.AreEqual(expectedFactoryCalls, factory.CallCount, $"Factory call count drifted after step {step}. {ReproContext()}");

			foreach (var key in keys)
			{
				model.TryGetValue(key, out var expected);
				var fromStack = await cacheStack.GetAsync<int>(key);
				var fromLayer1 = await layer1.GetAsync<int>(key);
				var fromLayer2 = await layer2.GetAsync<int>(key);
				Assert.AreEqual(expected?.Value, fromStack?.Value, $"Stack value drifted for {key} after step {step}. {ReproContext()}");
				Assert.AreEqual(expected?.Expiry, fromStack?.Expiry, $"Stack expiry drifted for {key} after step {step}. {ReproContext()}");
				Assert.AreEqual(expected?.Value, fromLayer1?.Value, $"Layer1 value drifted for {key} after step {step}. {ReproContext()}");
				Assert.AreEqual(expected?.Expiry, fromLayer1?.Expiry, $"Layer1 expiry drifted for {key} after step {step}. {ReproContext()}");
				Assert.AreEqual(expected?.Value, fromLayer2?.Value, $"Layer2 value drifted for {key} after step {step}. {ReproContext()}");
				Assert.AreEqual(expected?.Expiry, fromLayer2?.Expiry, $"Layer2 expiry drifted for {key} after step {step}. {ReproContext()}");
			}
		}
	}
}
