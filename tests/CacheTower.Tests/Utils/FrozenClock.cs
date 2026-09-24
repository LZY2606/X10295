using System;
using CacheTower.Internal;

namespace CacheTower.Tests.Utils;

/// <summary>
/// Freezes the shared <see cref="DateTimeProvider"/> clock (the same clock the production
/// caching paths read) at a deterministic time, allowing tests to advance time virtually
/// instead of using real sleeps. Disposing resumes the normal clock behavior.
/// </summary>
public sealed class FrozenClock : IDisposable
{
	public FrozenClock(DateTime startTime)
	{
		DateTimeProvider.FreezeTime(startTime);
	}

	/// <summary>
	/// The current virtual time.
	/// </summary>
	public DateTime Now => DateTimeProvider.Now;

	/// <summary>
	/// Moves the virtual clock forward.
	/// </summary>
	public void Advance(TimeSpan duration) => DateTimeProvider.AdvanceTime(duration);

	public void Dispose() => DateTimeProvider.ResumeTime();
}
