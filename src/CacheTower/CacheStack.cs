using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CacheTower.Extensions;
using CacheTower.Internal;
using CacheTower.Serializers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CacheTower;

/// <summary>
/// Options for configuring a <see cref="CacheStack"/>.
/// </summary>
public readonly record struct CacheStackOptions
{
	/// <summary>
	/// The cache layers to use for the cache stack.
	/// </summary>
	public ICacheLayer[] CacheLayers { get; }

	/// <summary>
	/// Creates a new <see cref="CacheStackOptions"/> for configuring a <see cref="CacheStack"/>.
	/// </summary>
	/// <param name="cacheLayers">The cache layers to use for the cache stack. The layers should be ordered from the highest priority to the lowest. At least one cache layer is required.</param>
	/// <exception cref="ArgumentException"></exception>
	public CacheStackOptions(params ICacheLayer[] cacheLayers)
	{
		if (cacheLayers is null or { Length: 0 })
		{
			throw new ArgumentException("There must be at least one cache layer", nameof(cacheLayers));
		}

		CacheLayers = cacheLayers;
	}

	/// <summary>
	/// The cache extensions to use for the cache stack.
	/// </summary>
	public ICacheExtension[] Extensions { get; init; } = Array.Empty<ICacheExtension>();
}

/// <summary>
/// A <see cref="CacheStack"/> is the backbone of caching for Cache Tower. This manages coordination between the various cache layers, manages the cache extensions and handles background refreshing.
/// </summary>
public class CacheStack : ICacheStack, IFlushableCacheStack, IExtendableCacheStack, IAsyncDisposable
{
	private bool Disposed;

	private readonly CacheEntryKeyLock KeyLock = new();
	private readonly ILogger<CacheStack> Logger;
	private readonly ICacheLayer[] CacheLayers;
	private readonly ExtensionContainer Extensions;

	/// <summary>
	/// Creates a new <see cref="CacheStack"/> with the provided <paramref name="options"/>.
	/// </summary>
	/// <param name="logger">The internal logger to use. If none is provided, a null logger will be used instead.</param>
	/// <param name="options">The <see cref="CacheStackOptions"/> to configure this cache stack.</param>
	public CacheStack(ILogger<CacheStack>? logger, CacheStackOptions options)
	{
		Logger = logger ?? NullLogger<CacheStack>.Instance;
		CacheLayers = options.CacheLayers;

		Extensions = new ExtensionContainer(options.Extensions);
		Extensions.Register(this);
	}

	/// <summary>
	/// Creates a new <see cref="CacheStack"/> with the given <paramref name="cacheLayers"/> and <paramref name="extensions"/>.
	/// </summary>
	/// <param name="logger">The internal logger to use.</param>
	/// <param name="cacheLayers">The cache layers to use for the current cache stack. The layers should be ordered from the highest priority to the lowest. At least one cache layer is required.</param>
	/// <param name="extensions">The cache extensions to use for the current cache stack.</param>
	[Obsolete("Use constructor with 'CacheStackOptions'")]
	public CacheStack(ILogger<CacheStack>? logger, ICacheLayer[] cacheLayers, ICacheExtension[] extensions) : this(logger, new(cacheLayers) { Extensions = extensions })
	{
	}

	/// <summary>
	/// Helper for throwing if disposed.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	protected void ThrowIfDisposed()
	{
		if (Disposed)
		{
			ThrowDisposedException();
		}

		static void ThrowDisposedException() => throw new ObjectDisposedException(nameof(CacheStack));
	}

	/// <inheritdoc/>
	public IReadOnlyList<ICacheLayer> GetCacheLayers()
	{
		return CacheLayers;
	}

	/// <inheritdoc/>
	public async ValueTask FlushAsync()
	{
		ThrowIfDisposed();

		for (int i = 0, l = CacheLayers.Length; i < l; i++)
		{
			var layer = CacheLayers[i];
			await layer.FlushAsync().ConfigureAwait(false);
		}

		await Extensions.OnCacheFlushAsync().ConfigureAwait(false);
	}

	/// <inheritdoc/>
	public async ValueTask CleanupAsync()
	{
		ThrowIfDisposed();

		for (int i = 0, l = CacheLayers.Length; i < l; i++)
		{
			var layer = CacheLayers[i];
			await layer.CleanupAsync().ConfigureAwait(false);
		}
	}

	/// <inheritdoc/>
	public async ValueTask EvictAsync(string cacheKey)
	{
		ThrowIfDisposed();

		if (cacheKey == null)
		{
			throw new ArgumentNullException(nameof(cacheKey));
		}

		for (int i = 0, l = CacheLayers.Length; i < l; i++)
		{
			var layer = CacheLayers[i];
			await layer.EvictAsync(cacheKey).ConfigureAwait(false);
		}

		await Extensions.OnCacheEvictionAsync(cacheKey).ConfigureAwait(false);
	}

	/// <inheritdoc/>
	public async ValueTask<CacheEntry<T>> SetAsync<T>(string cacheKey, T value, TimeSpan timeToLive)
	{
		ThrowIfDisposed();

		if (cacheKey == null)
		{
			throw new ArgumentNullException(nameof(cacheKey));
		}

		var cacheEntry = new CacheEntry<T>(value, timeToLive);
		await InternalSetAsync(cacheKey, cacheEntry, CacheUpdateType.AddOrUpdateEntry).ConfigureAwait(false);
		return cacheEntry;
	}

	/// <inheritdoc/>
	public async ValueTask SetAsync<T>(string cacheKey, CacheEntry<T> cacheEntry)
	{
		ThrowIfDisposed();

		if (cacheKey == null)
		{
			throw new ArgumentNullException(nameof(cacheKey));
		}

		if (cacheEntry == null)
		{
			throw new ArgumentNullException(nameof(cacheEntry));
		}

		await InternalSetAsync(cacheKey, cacheEntry, CacheUpdateType.AddOrUpdateEntry).ConfigureAwait(false);
	}

	private async ValueTask InternalSetAsync<T>(string cacheKey, CacheEntry<T> cacheEntry, CacheUpdateType cacheUpdateType)
	{
		CacheEntry<T>?[]? previousEntries = null;
		var failedLayerIndex = -1;
		Exception? layerFailure = null;
		for (int i = 0, l = CacheLayers.Length; i < l; i++)
		{
			var layer = CacheLayers[i];

			//Read the current entry first (the same public read path the rest of the stack uses)
			//so a failed write can be restored to exactly what the layer held beforehand.
			CacheEntry<T>? previousEntry;
			try
			{
				previousEntry = await layer.GetAsync<T>(cacheKey).ConfigureAwait(false);
			}
			catch (CacheSerializationException)
			{
				//The previous entry cannot be read on this layer - there is nothing restorable.
				previousEntry = null;
			}

			try
			{
				await layer.SetAsync(cacheKey, cacheEntry).ConfigureAwait(false);
			}
			catch (CacheSerializationException ex)
			{
				//Serialization failures mean this entry can never be represented on this layer -
				//neither the previous nor the new value is cacheable here, so skipping the layer is correct.
				Logger.LogWarning(ex, "Unable to set CacheKey={CacheKey} on CacheLayer={CacheLayer} due to an exception. This will result in cache misses on this layer.", cacheKey, layer.GetType());
				continue;
			}
			catch (Exception ex)
			{
				//A non-serialization failure indicates the layer is unhealthy rather than the value being uncacheable.
				//Snapshot the pre-write state of every layer so the partial update can be undone completely.
				failedLayerIndex = i;
				layerFailure = ex;
				Logger.LogError(ex, "Failed to set CacheKey={CacheKey} on CacheLayer={CacheLayer}. Rolling back the partial cache update.", cacheKey, layer.GetType());
				break;
			}

			if (previousEntry is not null)
			{
				previousEntries ??= new CacheEntry<T>?[CacheLayers.Length];
				previousEntries[i] = previousEntry;
			}
		}

		if (failedLayerIndex < 0)
		{
			await Extensions.OnCacheUpdateAsync(cacheKey, cacheEntry.Expiry, cacheUpdateType).ConfigureAwait(false);
		}
		else
		{
			//Restore every layer to exactly what it held before this call: a previous entry is written back,
			//otherwise the new partial value is evicted. No layer is left half-updated and no change event fires.
			for (var i = 0; i <= failedLayerIndex; i++)
			{
				try
				{
					var previousEntry = previousEntries?[i];
					if (previousEntry is not null)
					{
						await CacheLayers[i].SetAsync(cacheKey, previousEntry).ConfigureAwait(false);
					}
					else
					{
						await CacheLayers[i].EvictAsync(cacheKey).ConfigureAwait(false);
					}
				}
				catch (Exception rollbackEx)
				{
					Logger.LogError(rollbackEx, "Failed to roll back CacheKey={CacheKey} on CacheLayer={CacheLayer} after a partial cache update. The layer is unavailable until the cache entry is set again.", cacheKey, CacheLayers[i].GetType());
				}
			}

			//Surface the original failure from the cache layer - the rollback only removes the half-updated state.
			System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(layerFailure!).Throw();
		}
	}

	/// <inheritdoc/>
	public async ValueTask<CacheEntry<T>?> GetAsync<T>(string cacheKey)
	{
		ThrowIfDisposed();

		if (cacheKey == null)
		{
			throw new ArgumentNullException(nameof(cacheKey));
		}

		for (var layerIndex = 0; layerIndex < CacheLayers.Length; layerIndex++)
		{
			var layer = CacheLayers[layerIndex];
			if (await layer.IsAvailableAsync(cacheKey).ConfigureAwait(false))
			{
				try
				{
					var cacheEntry = await layer.GetAsync<T>(cacheKey).ConfigureAwait(false);
					if (cacheEntry != default)
					{
						return cacheEntry;
					}
				}
				catch (CacheSerializationException ex)
				{
					Logger.LogWarning(ex, "Unable to retrieve CacheKey={CacheKey} from CacheLayer={CacheLayer} due to an exception. This layer will be skipped.", cacheKey, layer.GetType());
				}
			}
		}

		return default;
	}

	/// <inheritdoc/>
	public async ValueTask<CacheEntry<T>?> GetAsync<T>(string cacheKey, bool backPopulate)
	{
		if (!backPopulate)
		{
			return await GetAsync<T>(cacheKey).ConfigureAwait(false);
		}

		ThrowIfDisposed();

		if (cacheKey == null)
		{
			throw new ArgumentNullException(nameof(cacheKey));
		}

		var cacheEntryPoint = await GetWithLayerIndexAsync<T>(cacheKey).ConfigureAwait(false);
		if (cacheEntryPoint == default)
		{
			return default;
		}

		if (cacheEntryPoint.LayerIndex == 0)
		{
			return cacheEntryPoint.CacheEntry;
		}

		_ = BackPopulateCacheAsync(cacheEntryPoint.LayerIndex, cacheKey, cacheEntryPoint.CacheEntry);

		return cacheEntryPoint.CacheEntry;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private async ValueTask<(int LayerIndex, CacheEntry<T> CacheEntry)> GetWithLayerIndexAsync<T>(string cacheKey)
	{
		for (var layerIndex = 0; layerIndex < CacheLayers.Length; layerIndex++)
		{
			var layer = CacheLayers[layerIndex];
			if (await layer.IsAvailableAsync(cacheKey).ConfigureAwait(false))
			{
				try
				{
					var cacheEntry = await layer.GetAsync<T>(cacheKey).ConfigureAwait(false);
					if (cacheEntry != default)
					{
						return (layerIndex, cacheEntry);
					}
				}
				catch (CacheSerializationException ex)
				{
					Logger.LogWarning(ex, "Unable to retrieve CacheKey={CacheKey} from CacheLayer={CacheLayer} due to an exception. This layer will be skipped.", cacheKey, layer.GetType());
				}
			}
		}

		return default;
	}

	/// <inheritdoc/>
	public ValueTask<T> GetOrSetAsync<T>(string cacheKey, Func<T, Task<T>> valueFactory, CacheSettings settings)
		=> GetOrSetAsync(cacheKey, valueFactory, settings, default);

	/// <summary>
	/// Attempts to retrieve the value for the given <paramref name="cacheKey"/>, falling back to <paramref name="valueFactory"/> when unavailable.
	/// </summary>
	/// <remarks>
	/// Cancelling <paramref name="cancellationToken"/> only detaches the calling wait - it never cancels shared refresh work.
	/// The factory is always invoked at most once for a concurrent set of callers and its produced entry is still
	/// published to every cache layer and shared with any other waiters. This keeps call cancellation independent
	/// from the lifecycle of the shared cache refresh, leaving no temporary key-lock state behind.
	/// </remarks>
	/// <typeparam name="T">The type of value in the cache entry.</typeparam>
	/// <param name="cacheKey">The cache entry's key.</param>
	/// <param name="valueFactory">The value factory called when no cache entry is available.</param>
	/// <param name="settings">Cache control settings.</param>
	/// <param name="cancellationToken">The token used to detach this caller from an in-progress miss or expired refresh.</param>
	/// <returns>The item from the cache that corresponds to the given <paramref name="cacheKey"/>.</returns>
	public async ValueTask<T> GetOrSetAsync<T>(string cacheKey, Func<T, Task<T>> valueFactory, CacheSettings settings, CancellationToken cancellationToken)
	{
		ThrowIfDisposed();

		if (cacheKey == null)
		{
			throw new ArgumentNullException(nameof(cacheKey));
		}

		if (valueFactory == null)
		{
			throw new ArgumentNullException(nameof(valueFactory));
		}

		cancellationToken.ThrowIfCancellationRequested();

		var currentTime = DateTimeProvider.Now;
		var cacheEntryPoint = await GetWithLayerIndexAsync<T>(cacheKey).ConfigureAwait(false);
		var cacheEntryStatus = CacheEntryStatus.Stale;
		if (cacheEntryPoint != default)
		{
			if (cacheEntryPoint.CacheEntry.Expiry > currentTime)
			{
				var cacheEntry = cacheEntryPoint.CacheEntry;
				if (settings.StaleAfter.HasValue && cacheEntry.GetStaleDate(settings) < currentTime)
				{
					//If the cache entry is stale, refresh the value in the background
					_ = RefreshValueAsync(cacheKey, valueFactory, settings, cacheEntryStatus);
				}
				else if (cacheEntryPoint.LayerIndex > 0)
				{
					//If a lower-level cache is missing the latest data, attempt to set it in the background
					_ = BackPopulateCacheAsync(cacheEntryPoint.LayerIndex, cacheKey, cacheEntry);
				}

				return cacheEntry.Value!;
			}
			else
			{
				//Refresh the value in the current thread because we only have expired data (we never return expired data)
				cacheEntryStatus = CacheEntryStatus.Expired;
			}
		}
		else
		{
			//Refresh the value in the current thread because we have no existing data
			cacheEntryStatus = CacheEntryStatus.Miss;
		}

		//Only a miss or an expired entry parks the caller in the shared refresh.
		//Detaching the token must not cancel the refresh itself - observe the shared result independently.
		var refreshTask = RefreshValueAsync(cacheKey, valueFactory, settings, cacheEntryStatus).AsTask();
		var completedTask = await Task.WhenAny(refreshTask, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
		if (!ReferenceEquals(completedTask, refreshTask))
		{
			//Observe the shared work when it completes so its eventual result or failure is never lost as unobserved state.
			_ = refreshTask.ContinueWith(
				static t => { var observed = t.Exception; },
				TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously
			);
			throw new OperationCanceledException(cancellationToken);
		}

		return (await refreshTask.ConfigureAwait(false))!.Value!;
	}

	private async ValueTask BackPopulateCacheAsync<T>(int fromIndexExclusive, string cacheKey, CacheEntry<T> cacheEntry)
	{
		if (KeyLock.AcquireLock(cacheKey))
		{
			try
			{
				for (; --fromIndexExclusive >= 0;)
				{
					var previousLayer = CacheLayers[fromIndexExclusive];
					if (await previousLayer.IsAvailableAsync(cacheKey).ConfigureAwait(false))
					{
						await previousLayer.SetAsync(cacheKey, cacheEntry).ConfigureAwait(false);
					}
				}
			}
			finally
			{
				KeyLock.ReleaseLock(cacheKey, cacheEntry);
			}
		}
	}

	private async ValueTask<CacheEntry<T>?> RefreshValueAsync<T>(string cacheKey, Func<T, Task<T>> asyncValueFactory, CacheSettings settings, CacheEntryStatus entryStatus)
	{
		if (KeyLock.AcquireLock(cacheKey))
		{
			try
			{
				var previousEntry = await GetAsync<T>(cacheKey).ConfigureAwait(false);
				if (previousEntry != default && entryStatus == CacheEntryStatus.Miss && previousEntry.Expiry >= DateTimeProvider.Now)
				{
					//The Cache Stack will always return an unexpired value if one exists.
					//If we are told to refresh because one doesn't and we find one, we return the existing value, ignoring the refresh.
					//This can happen due to the race condition of getting the values out of the cache.
					//We can only do any of this because we have the local lock.
					KeyLock.ReleaseLock(cacheKey, previousEntry);
					return previousEntry;
				}

				await using var distributedLock = await Extensions.AwaitAccessAsync(cacheKey).ConfigureAwait(false);

				CacheEntry<T>? cacheEntry;
				if (distributedLock.IsLockOwner)
				{
					var oldValue = previousEntry != default ? previousEntry.Value : default;
					var refreshedValue = await asyncValueFactory(oldValue!).ConfigureAwait(false);
					cacheEntry = new CacheEntry<T>(refreshedValue, settings.TimeToLive);
					var cacheUpdateType = entryStatus switch
					{
						CacheEntryStatus.Miss => CacheUpdateType.AddEntry,
						_ => CacheUpdateType.AddOrUpdateEntry
					};
					await InternalSetAsync(cacheKey, cacheEntry, cacheUpdateType).ConfigureAwait(false);

					KeyLock.ReleaseLock(cacheKey, cacheEntry);
				}
				else
				{
					cacheEntry = await GetAsync<T>(cacheKey).ConfigureAwait(false);
				}

				return cacheEntry;
			}
			catch (Exception e)
			{
				KeyLock.ReleaseLock(cacheKey, e);
				throw;
			}
		}
		else if (entryStatus != CacheEntryStatus.Stale)
		{
			//Last minute check to confirm whether waiting is required
			var currentEntry = await GetAsync<T>(cacheKey).ConfigureAwait(false);
			if (currentEntry != null && currentEntry.GetStaleDate(settings) > DateTimeProvider.Now)
			{
				KeyLock.ReleaseLock(cacheKey, currentEntry);
				return currentEntry;
			}

			//Lock until we are notified to be unlocked
			return await KeyLock.WaitAsync(cacheKey).ConfigureAwait(false) as CacheEntry<T>;
		}

		return default;
	}

	/// <summary>
	/// Disposes the current instance of <see cref="CacheStack"/> and all associated layers and extensions.
	/// </summary>
	/// <returns></returns>
	public async ValueTask DisposeAsync()
	{
		if (Disposed)
		{
			return;
		}

		foreach (var layer in CacheLayers)
		{
			if (layer is IDisposable disposableLayer)
			{
				disposableLayer.Dispose();
			}
			else if (layer is IAsyncDisposable asyncDisposableLayer)
			{
				await asyncDisposableLayer.DisposeAsync().ConfigureAwait(false);
			}
		}

		await Extensions.DisposeAsync().ConfigureAwait(false);

		Disposed = true;
	}
}
