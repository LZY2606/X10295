using System;
using System.Threading;

namespace CacheTower.Internal
{
	internal static class DateTimeProvider
	{
		private static Func<DateTime>? TimeSource;
		private static DateTime RealTime = DateTime.UtcNow;

		/// <summary>
		/// The current time. Normally this is <see cref="DateTime.UtcNow"/>, updated every second by an internal timer.
		/// If a test has installed a virtual time source via <see cref="SetTimeSource"/>, that source is consulted instead.
		/// </summary>
		public static DateTime Now
		{
			[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
			get => TimeSource is null ? RealTime : TimeSource();
		}

		/// <summary>
		/// Updates the current time to <see cref="DateTime.UtcNow"/>. This is automatically called by a timer every second.
		/// </summary>
		/// <remarks>
		/// This is intended to only be triggered by the internal timer or by unit tests that require it.
		/// The reason why tests need it is due to the fast turn around of setting a value and testing the outcome.
		/// Real applications aren't immediately setting a cache value manually, calling <see cref="ICacheStack.GetOrSetAsync{T}(string, Func{T, System.Threading.Tasks.Task{T}}, CacheSettings)"/> and then comparing whether the results are the same.
		/// The alternative for the tests is just "waiting" an extra second between setting a value and retrieving it however that makes the testing slower and the tests more confusing.
		/// </remarks>
		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
		public static void UpdateTime()
		{
			if (TimeSource is null)
			{
				RealTime = DateTime.UtcNow;
			}
		}

		/// <summary>
		/// Installs a virtual source of time that every Cache Tower internal clock read will use.
		/// This shares the exact same clock read as production code (entry expiry, stale/expired evaluation and cleanup) - no parallel time logic exists.
		/// Pass <see langword="null"/> to restore the normal wall clock, which also resumes updates from the internal timer.
		/// </summary>
		/// <remarks>
		/// Intended for deterministic state machine tests so they never need a real sleep. Callers must restore the wall clock in test cleanup.
		/// </remarks>
		/// <param name="timeSource">A function returning the current virtual time (must be UTC), or <see langword="null"/> to restore the real clock.</param>
		public static void SetTimeSource(Func<DateTime>? timeSource)
		{
			TimeSource = timeSource;
			if (timeSource is null)
			{
				RealTime = DateTime.UtcNow;
			}
		}

		[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0052:Remove unread private members", Justification = "Establishes timer and prevents it being garbage collected")]
		private static readonly Timer DateTimeTimer = new(state => UpdateTime(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
	}
}
