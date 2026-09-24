using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace CacheTower.Internal
{
	internal static class DateTimeProvider
	{
		private static bool IsTimeFrozen;

		/// <summary>
		/// The current <see cref="DateTime.UtcNow"/>, updated every second.
		/// </summary>
		public static DateTime Now { get; private set; } = DateTime.UtcNow;

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
		public static void UpdateTime()
		{
			if (!IsTimeFrozen)
			{
				Now = DateTime.UtcNow;
			}
		}

		/// <summary>
		/// Freezes <see cref="Now"/> at the given <paramref name="time"/>, acting as a virtual clock for tests.
		/// While frozen, the automatic timer and <see cref="UpdateTime"/> no longer change <see cref="Now"/>.
		/// This is the same <see cref="Now"/> value consumed by the production caching paths; no parallel clock is introduced.
		/// </summary>
		/// <remarks>
		/// This is intended to only be used by unit tests that require deterministic time without real sleeps.
		/// Tests must call <see cref="ResumeTime"/> when finished so other tests and the automatic timer behave normally.
		/// </remarks>
		/// <param name="time">The time to freeze <see cref="Now"/> at. This is rounded down to the second to match cache entry expiry precision.</param>
		internal static void FreezeTime(DateTime time)
		{
			IsTimeFrozen = true;
			Now = new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, time.Second, DateTimeKind.Utc);
		}

		/// <summary>
		/// Advances the frozen time by the given <paramref name="duration"/>. Requires <see cref="FreezeTime"/> to have been called first.
		/// </summary>
		/// <param name="duration">How far to move the virtual clock forward.</param>
		internal static void AdvanceTime(TimeSpan duration)
		{
			if (!IsTimeFrozen)
			{
				throw new InvalidOperationException("Time must be frozen via FreezeTime before it can be advanced.");
			}
			Now += duration;
		}

		/// <summary>
		/// Resumes the automatic time updates, restoring <see cref="Now"/> to the current <see cref="DateTime.UtcNow"/> value.
		/// </summary>
		internal static void ResumeTime()
		{
			IsTimeFrozen = false;
			UpdateTime();
		}

		[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0052:Remove unread private members", Justification = "Establishes timer and prevents it being garbage collected")]
		private static readonly Timer DateTimeTimer = new(state => UpdateTime(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
	}
}
