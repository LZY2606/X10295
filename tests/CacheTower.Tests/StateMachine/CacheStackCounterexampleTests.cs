using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CacheTower.Serializers;
using CacheTower.Tests.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CacheTower.Tests.StateMachine;

/// <summary>
/// Counterexample materials for the cache tower state machine: virtual clock, cancellation and
/// layered-failure scenarios. Every step enters through the public <see cref="CacheStack"/> or
/// <see cref="ICacheLayer"/> API; no internal end-state objects are fabricated.
/// Each test records both the caller-visible result and the internal invariants
/// (per-layer values, expiries, event order, factory call counts) that prove the
/// caching / layered-failure / concurrency semantics.
/// </summary>
[TestClass]
public class CacheStackCounterexampleTests : TestBase
{
	private static readonly DateTime TimeOrigin = new(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc);

	private static async Task AssertLayerEntry<T>(ScriptedCacheLayer layer, string cacheKey, T expectedValue, DateTime expectedExpiry)
	{
		var entry = await layer.GetAsync<T>(cacheKey);
		Assert.IsNotNull(entry, $"Expected {layer.Name} to hold an entry for {cacheKey}.");
		Assert.AreEqual(expectedValue, (T)entry.Value, $"Wrong value in {layer.Name} for {cacheKey}.");
		Assert.AreEqual(expectedExpiry, entry.Expiry, $"Wrong expiry in {layer.Name} for {cacheKey}.");
	}

	[TestMethod]
	public async Task MemoryHit_TopLayerServesValueWithoutFactoryOrLowerLayerTraffic()
	{
		using var clock = new FrozenClock(TimeOrigin);
		var eventLog = new CacheEventLog();
		var layer1 = new ScriptedCacheLayer("Layer1", eventLog);
		var layer2 = new ScriptedCacheLayer("Layer2", eventLog);
		await using var cacheStack = new CacheStack(null, new(new ICacheLayer[] { layer1, layer2 }));
		const string cacheKey = nameof(MemoryHit_TopLayerServesValueWithoutFactoryOrLowerLayerTraffic);

		await cacheStack.SetAsync(cacheKey, 42, TimeSpan.FromHours(1));
		eventLog.Clear();

		var factory = new BlockingValueFactory<int>(call => call);
		var result = await cacheStack.GetOrSetAsync<int>(cacheKey, factory.Factory, new CacheSettings(TimeSpan.FromHours(1)));

		Assert.AreEqual(42, result, "Caller-visible result must be the cached value.");
		Assert.AreEqual(0, factory.CallCount, "Factory must not run on a memory hit.");
		eventLog.AssertContainsInOrder($"Layer1:Get:{cacheKey}");
		eventLog.AssertDoesNotContain($"Layer2:Get:{cacheKey}");
		eventLog.AssertDoesNotContain($"Layer1:Set:{cacheKey}");
		await AssertLayerEntry(layer1, cacheKey, 42, TimeOrigin.AddHours(1));
		await AssertLayerEntry(layer2, cacheKey, 42, TimeOrigin.AddHours(1));
	}

	[TestMethod]
	public async Task BackPopulation_LowerLayerHitPromotesEntryToTopLayer()
	{
		using var clock = new FrozenClock(TimeOrigin);
		var eventLog = new CacheEventLog();
		var layer1 = new ScriptedCacheLayer("Layer1", eventLog);
		var layer2 = new ScriptedCacheLayer("Layer2", eventLog);
		await using var cacheStack = new CacheStack(null, new(new ICacheLayer[] { layer1, layer2 }));
		const string cacheKey = nameof(BackPopulation_LowerLayerHitPromotesEntryToTopLayer);

		await layer2.SetAsync(cacheKey, new CacheEntry<int>(42, TimeOrigin.AddHours(1)));
		eventLog.Clear();

		var factory = new BlockingValueFactory<int>(call => call);
		var result = await cacheStack.GetOrSetAsync<int>(cacheKey, factory.Factory, new CacheSettings(TimeSpan.FromHours(1)));

		Assert.AreEqual(42, result, "Caller-visible result must come from the lower layer.");
		Assert.AreEqual(0, factory.CallCount, "Factory must not run when a lower layer has the entry.");

		//Deterministic wait for the background back-population instead of a real sleep
		await layer1.WaitForAsync("Set", cacheKey);

		eventLog.AssertContainsInOrder($"Layer1:Get:{cacheKey}", $"Layer2:Get:{cacheKey}", $"Layer1:Set:{cacheKey}");
		eventLog.AssertDoesNotContain($"Layer2:Set:{cacheKey}");
		await AssertLayerEntry(layer1, cacheKey, 42, TimeOrigin.AddHours(1));
		await AssertLayerEntry(layer2, cacheKey, 42, TimeOrigin.AddHours(1));
	}

	[TestMethod]
	[DataRow(2)]
	[DataRow(8)]
	[DataRow(32)]
	public async Task ConcurrentMiss_InvokesFactoryExactlyOnce(int callerCount)
	{
		using var clock = new FrozenClock(TimeOrigin);
		var eventLog = new CacheEventLog();
		var layer1 = new ScriptedCacheLayer("Layer1", eventLog);
		var layer2 = new ScriptedCacheLayer("Layer2", eventLog);
		await using var cacheStack = new CacheStack(null, new(new ICacheLayer[] { layer1, layer2 }));
		const string cacheKey = nameof(ConcurrentMiss_InvokesFactoryExactlyOnce);

		var factory = new BlockingValueFactory<int>(call => 100 + call);
		var factoryGate = factory.BlockNextCall();

		var callers = Enumerable.Range(0, callerCount)
			.Select(_ => cacheStack.GetOrSetAsync<int>(cacheKey, factory.Factory, new CacheSettings(TimeSpan.FromHours(1))).AsTask())
			.ToArray();

		await factoryGate.Entered;
		foreach (var caller in callers)
		{
			Assert.IsFalse(caller.IsCompleted, "No caller may complete while the shared factory work is blocked.");
		}

		factoryGate.Release();
		var results = await Task.WhenAll(callers);

		Assert.AreEqual(1, factory.CallCount, $"Factory must run exactly once for {callerCount} concurrent misses.");
		foreach (var result in results)
		{
			Assert.AreEqual(101, result, "Every caller must observe the same shared-work result.");
		}
		await AssertLayerEntry(layer1, cacheKey, 101, TimeOrigin.AddHours(1));
		await AssertLayerEntry(layer2, cacheKey, 101, TimeOrigin.AddHours(1));
		eventLog.AssertContainsInOrder($"Layer1:Set:{cacheKey}", $"Layer2:Set:{cacheKey}");
	}

	[TestMethod]
	public async Task FactoryFailure_ReleasesWaitersAndRetryWithSameInputSucceeds()
	{
		using var clock = new FrozenClock(TimeOrigin);
		var eventLog = new CacheEventLog();
		var layer1 = new ScriptedCacheLayer("Layer1", eventLog);
		var layer2 = new ScriptedCacheLayer("Layer2", eventLog);
		await using var cacheStack = new CacheStack(null, new(new ICacheLayer[] { layer1, layer2 }));
		const string cacheKey = nameof(FactoryFailure_ReleasesWaitersAndRetryWithSameInputSucceeds);
		var settings = new CacheSettings(TimeSpan.FromHours(1));

		var factory = new BlockingValueFactory<int>(call => 100 + call);
		var expectedFailure = new InvalidOperationException("Scripted factory failure");
		factory.FailNextCall(expectedFailure);
		var factoryGate = factory.BlockNextCall();

		var firstCaller = cacheStack.GetOrSetAsync<int>(cacheKey, factory.Factory, settings).AsTask();
		await factoryGate.Entered;
		var waitingCaller = cacheStack.GetOrSetAsync<int>(cacheKey, factory.Factory, settings).AsTask();
		factoryGate.Release();

		var firstException = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => firstCaller);
		var waitingException = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => waitingCaller);
		Assert.AreSame(expectedFailure, firstException, "The lock owner must observe the factory failure.");
		Assert.AreSame(expectedFailure, waitingException, "Shared-work waiters must observe the same failure.");

		Assert.AreEqual(1, factory.CallCount, "The failed attempt must be counted exactly once.");
		eventLog.AssertDoesNotContain($"Layer1:Set:{cacheKey}");
		eventLog.AssertDoesNotContain($"Layer2:Set:{cacheKey}");
		Assert.IsNull(await layer1.GetAsync<int>(cacheKey), "A failed factory must not leave a half-written entry in layer 1.");
		Assert.IsNull(await layer2.GetAsync<int>(cacheKey), "A failed factory must not leave a half-written entry in layer 2.");

		//Retry with the exact same input after the recoverable failure
		var retryResult = await cacheStack.GetOrSetAsync<int>(cacheKey, factory.Factory, settings);

		Assert.AreEqual(102, retryResult, "Retry must run the factory again (second call).");
		Assert.AreEqual(2, factory.CallCount, "Retry must be a new factory call, not a replayed result.");
		await AssertLayerEntry(layer1, cacheKey, 102, TimeOrigin.AddHours(1));
		await AssertLayerEntry(layer2, cacheKey, 102, TimeOrigin.AddHours(1));
	}

	[TestMethod]
	public async Task FactoryCancellation_IsSeparatedFromSharedWorkLifecycle()
	{
		using var clock = new FrozenClock(TimeOrigin);
		var eventLog = new CacheEventLog();
		var layer1 = new ScriptedCacheLayer("Layer1", eventLog);
		var layer2 = new ScriptedCacheLayer("Layer2", eventLog);
		await using var cacheStack = new CacheStack(null, new(new ICacheLayer[] { layer1, layer2 }));
		const string cacheKey = nameof(FactoryCancellation_IsSeparatedFromSharedWorkLifecycle);
		var settings = new CacheSettings(TimeSpan.FromHours(1));

		var factory = new BlockingValueFactory<int>(call => 100 + call);
		factory.FailNextCall(new OperationCanceledException("Scripted factory cancellation"));
		var factoryGate = factory.BlockNextCall();

		var firstCaller = cacheStack.GetOrSetAsync<int>(cacheKey, factory.Factory, settings).AsTask();
		await factoryGate.Entered;
		var waitingCaller = cacheStack.GetOrSetAsync<int>(cacheKey, factory.Factory, settings).AsTask();
		factoryGate.Release();

		await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => firstCaller);
		await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => waitingCaller);
		Assert.AreEqual(1, factory.CallCount);
		Assert.IsNull(await layer1.GetAsync<int>(cacheKey), "Cancellation must not leave cached state behind.");
		Assert.IsNull(await layer2.GetAsync<int>(cacheKey), "Cancellation must not leave cached state behind.");

		//The shared work lifecycle ended with the cancellation; a later call starts fresh work
		var result = await cacheStack.GetOrSetAsync<int>(cacheKey, factory.Factory, settings);
		Assert.AreEqual(102, result);
		Assert.AreEqual(2, factory.CallCount);
		await AssertLayerEntry(layer1, cacheKey, 102, TimeOrigin.AddHours(1));
		await AssertLayerEntry(layer2, cacheKey, 102, TimeOrigin.AddHours(1));
	}

	[TestMethod]
	public async Task CallerCancellation_DoesNotCancelSharedWork()
	{
		using var clock = new FrozenClock(TimeOrigin);
		var eventLog = new CacheEventLog();
		var layer1 = new ScriptedCacheLayer("Layer1", eventLog);
		var layer2 = new ScriptedCacheLayer("Layer2", eventLog);
		await using var cacheStack = new CacheStack(null, new(new ICacheLayer[] { layer1, layer2 }));
		const string cacheKey = nameof(CallerCancellation_DoesNotCancelSharedWork);
		var settings = new CacheSettings(TimeSpan.FromHours(1));

		var factory = new BlockingValueFactory<int>(call => 100 + call);
		var factoryGate = factory.BlockNextCall();

		var cancellingCaller = cacheStack.GetOrSetAsync<int>(cacheKey, factory.Factory, settings).AsTask();
		await factoryGate.Entered;
		var observingCaller = cacheStack.GetOrSetAsync<int>(cacheKey, factory.Factory, settings).AsTask();

		//The first caller cancels its own wait; the shared work must continue regardless
		using var cancellationSource = new CancellationTokenSource();
		var cancellableWait = cancellingCaller.WaitAsync(cancellationSource.Token);
		cancellationSource.Cancel();
		await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => cancellableWait);

		factoryGate.Release();

		Assert.AreEqual(101, await observingCaller, "The waiting caller must still receive the shared-work result.");
		Assert.AreEqual(101, await cancellingCaller, "The underlying work of the cancelled caller completes independently.");
		Assert.AreEqual(1, factory.CallCount, "Caller cancellation must not trigger duplicate factory work.");
		await AssertLayerEntry(layer1, cacheKey, 101, TimeOrigin.AddHours(1));
		await AssertLayerEntry(layer2, cacheKey, 101, TimeOrigin.AddHours(1));
	}

	[TestMethod]
	public async Task PartialLayerWriteFailure_IsObservableAndHealsOnNextRead()
	{
		using var clock = new FrozenClock(TimeOrigin);
		var eventLog = new CacheEventLog();
		var layer1 = new ScriptedCacheLayer("Layer1", eventLog);
		var layer2 = new ScriptedCacheLayer("Layer2", eventLog);
		await using var cacheStack = new CacheStack(null, new(new ICacheLayer[] { layer1, layer2 }));
		const string cacheKey = nameof(PartialLayerWriteFailure_IsObservableAndHealsOnNextRead);

		layer1.FailNextSets(new CacheSerializationException("Scripted layer write failure"));
		await cacheStack.SetAsync(cacheKey, 42, TimeSpan.FromHours(1));

		eventLog.AssertContainsInOrder($"Layer1:Set:{cacheKey}", $"Layer2:Set:{cacheKey}");
		Assert.IsNull(await layer1.GetAsync<int>(cacheKey), "The failed layer must observably miss the entry.");
		await AssertLayerEntry(layer2, cacheKey, 42, TimeOrigin.AddHours(1));

		//The next read surfaces the lower layer value and heals the failed layer via back-population
		eventLog.Clear();
		var factory = new BlockingValueFactory<int>(call => call);
		var result = await cacheStack.GetOrSetAsync<int>(cacheKey, factory.Factory, new CacheSettings(TimeSpan.FromHours(1)));

		Assert.AreEqual(42, result);
		Assert.AreEqual(0, factory.CallCount, "Healing must come from the lower layer, not the factory.");
		await layer1.WaitForAsync("Set", cacheKey);
		eventLog.AssertContainsInOrder($"Layer1:Get:{cacheKey}", $"Layer2:Get:{cacheKey}", $"Layer1:Set:{cacheKey}");
		await AssertLayerEntry(layer1, cacheKey, 42, TimeOrigin.AddHours(1));
		await AssertLayerEntry(layer2, cacheKey, 42, TimeOrigin.AddHours(1));
	}

	[TestMethod]
	public async Task EvictionRace_EvictDuringInflightRefresh_FinalStateIsConsistent()
	{
		using var clock = new FrozenClock(TimeOrigin);
		var eventLog = new CacheEventLog();
		var layer1 = new ScriptedCacheLayer("Layer1", eventLog);
		var layer2 = new ScriptedCacheLayer("Layer2", eventLog);
		await using var cacheStack = new CacheStack(null, new(new ICacheLayer[] { layer1, layer2 }));
		const string cacheKey = nameof(EvictionRace_EvictDuringInflightRefresh_FinalStateIsConsistent);

		var factory = new BlockingValueFactory<int>(call => 100 + call);
		var factoryGate = factory.BlockNextCall();

		var refresh = cacheStack.GetOrSetAsync<int>(cacheKey, factory.Factory, new CacheSettings(TimeSpan.FromHours(1))).AsTask();
		await factoryGate.Entered;

		//Evict while the refresh is in flight; the refresh's write lands after the eviction
		await cacheStack.EvictAsync(cacheKey);
		factoryGate.Release();
		var result = await refresh;

		Assert.AreEqual(101, result);
		Assert.AreEqual(1, factory.CallCount);
		eventLog.AssertContainsInOrder($"Layer1:Evict:{cacheKey}", $"Layer1:Set:{cacheKey}");
		eventLog.AssertContainsInOrder($"Layer2:Evict:{cacheKey}", $"Layer2:Set:{cacheKey}");
		await AssertLayerEntry(layer1, cacheKey, 101, TimeOrigin.AddHours(1));
		await AssertLayerEntry(layer2, cacheKey, 101, TimeOrigin.AddHours(1));

		await cacheStack.EvictAsync(cacheKey);
		Assert.IsNull(await layer1.GetAsync<int>(cacheKey));
		Assert.IsNull(await layer2.GetAsync<int>(cacheKey));
	}

	[TestMethod]
	public async Task SameKey_DifferentGenericValues_WrongTypeReadsAsUnknown()
	{
		using var clock = new FrozenClock(TimeOrigin);
		var eventLog = new CacheEventLog();
		var layer1 = new ScriptedCacheLayer("Layer1", eventLog);
		var layer2 = new ScriptedCacheLayer("Layer2", eventLog);
		await using var cacheStack = new CacheStack(null, new(new ICacheLayer[] { layer1, layer2 }));
		const string cacheKey = nameof(SameKey_DifferentGenericValues_WrongTypeReadsAsUnknown);

		await cacheStack.SetAsync(cacheKey, 42, TimeSpan.FromHours(1));

		var wrongTypeEntry = await cacheStack.GetAsync<string>(cacheKey);
		Assert.IsNull(wrongTypeEntry, "A different generic type for the same key must read as unknown, not a guessed success.");

		var factory = new BlockingValueFactory<string>(call => $"string-value-{call}");
		var result = await cacheStack.GetOrSetAsync<string>(cacheKey, factory.Factory, new CacheSettings(TimeSpan.FromHours(1)));

		Assert.AreEqual("string-value-1", result, "The wrong-typed entry must be treated as a miss.");
		Assert.AreEqual(1, factory.CallCount, "The wrong-typed entry must trigger exactly one factory call.");
		Assert.IsNull(await cacheStack.GetAsync<int>(cacheKey), "The string entry replaces the int entry under the same key.");
		await AssertLayerEntry(layer1, cacheKey, "string-value-1", TimeOrigin.AddHours(1));
		await AssertLayerEntry(layer2, cacheKey, "string-value-1", TimeOrigin.AddHours(1));
	}
}
