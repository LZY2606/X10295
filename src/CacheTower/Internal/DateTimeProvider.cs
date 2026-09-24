using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace CacheTower.Internal
{
internal static class DateTimeProvider
{
	private static DateTime CurrentTime = DateTime.UtcNow;

	/// <summary>
	/// Test-only clock override. When set, <see cref="Now"/> is resolved from this function instead of the internal timer.
	/// </summary>
	/// <remarks>
	/// This is not a parallel time implementation: production code keeps reading the single <see cref="Now"/> property,
	/// so the override flows through the exact same expiry/staleness logic used in production.
	/// Visibility is limited to <c>CacheTower.Tests</c> via <c>InternalsVisibleTo</c>.
	/// </remarks>
	private static Func<DateTime>? ClockOverride;

	/// <summary>
	/// The current <see cref="DateTime.UtcNow"/>, updated every second.
	/// </summary>
	public static DateTime Now => ClockOverride?.Invoke() ?? CurrentTime;

	/// <summary>
	/// Updates <see cref="Now"/> to the current <see cref="DateTime.UtcNow"/> value. This is automatically called by a timer every second.
	/// </summary>
		/// <remarks>
		/// This is intended to only be triggered by the internal timer or by unit tests that require it.
		/// The reason why tests need it is due to the fast turn around of setting a value and testing the outcome.
		/// Real applications aren't immediately setting a cache value manually, calling <see cref="ICacheStack.GetOrSetAsync{T}(string, Func{T, System.Threading.Tasks.Task{T}}, CacheSettings)"/> and then comparing whether the results are the same.
	/// The alternative for the tests is just "waiting" an extra second between setting a value and retrieving it however that makes the testing slower and the tests more confusing.
	/// </remarks>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static void UpdateTime() => CurrentTime = DateTime.UtcNow;

	/// <summary>
	/// Test-only hook that replaces the clock source used by <see cref="Now"/>. Pass <see langword="null"/> to restore the default clock.
	/// This shares the same <see cref="Now"/> read path as production code (cache entry expiry, stale checks and layer cleanup).
	/// </summary>
	internal static void OverrideClock(Func<DateTime>? clock) => ClockOverride = clock;

		[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0052:Remove unread private members", Justification = "Establishes timer and prevents it being garbage collected")]
		private static readonly Timer DateTimeTimer = new(state => UpdateTime(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
	}
}
