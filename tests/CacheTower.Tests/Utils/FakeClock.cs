using System;
using CacheTower.Internal;

namespace CacheTower.Tests.Utils;

/// <summary>
/// A manually advanced clock that replaces the time source of <see cref="DateTimeProvider"/> for the
/// lifetime of the instance. Disposing restores the default clock so other tests are unaffected.
/// </summary>
/// <remarks>
/// The override flows through the same internal <c>DateTimeProvider.Now</c> read path that production
/// code uses for cache entry expiry, stale checks and layer cleanup - no parallel implementation.
/// </remarks>
public sealed class FakeClock : IDisposable
{
	private DateTime current;

	public FakeClock() : this(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))
	{
	}

	private FakeClock(DateTime start)
	{
		current = start;
		DateTimeProvider.OverrideClock(() => current);
	}

	public DateTime Now => current;

	public void Advance(TimeSpan duration) => current += duration;

	public void Dispose() => DateTimeProvider.OverrideClock(null);
}
