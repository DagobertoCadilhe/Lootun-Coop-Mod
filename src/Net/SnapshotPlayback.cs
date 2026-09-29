using System;
using System.Collections.Generic;

namespace LootunCoop.Net
{
	/// <summary>
	/// Snapshot interpolation timing: plays timestamped snapshots back a fixed delay behind the newest one, so there is almost
	/// always a later snapshot to blend toward. The playback clock runs up to 10% fast or slow to hold that delay as snapshots
	/// arrive early or late. It never moves backwards: it jumps forward only when it has fallen far behind (a burst after a
	/// stall), and when starved it holds the newest snapshot.
	/// </summary>
	public sealed class SnapshotPlayback<T> where T : class
	{
		const double DriftGain = 0.001;
		const double MaxSpeedChange = 0.1;

		readonly List<(long Time, T Item)> buffer = new List<(long, T)>();
		bool playing;

		/// <summary>The moment on the sender's clock currently shown.</summary>
		public double PlaybackMs { get; private set; }
		public int Count => buffer.Count;

		/// <summary>Queues a snapshot. Ignored unless it is newer than the last one.</summary>
		public bool Add(long timeMs, T item)
		{
			if (buffer.Count > 0 && timeMs <= buffer[buffer.Count - 1].Time)
				return false;
			buffer.Add((timeMs, item));
			return true;
		}

		public void Clear()
		{
			buffer.Clear();
			playing = false;
			PlaybackMs = 0;
		}

		/// <summary>
		/// Moves playback on by <paramref name="deltaMs"/> of local time. <paramref name="from"/> is the snapshot to show,
		/// blended toward <paramref name="to"/> (null when starved) by <paramref name="t"/>. False when nothing is queued.
		/// </summary>
		public bool Advance(double deltaMs, int delayMs, out T from, out T to, out double t)
		{
			from = to = null;
			t = 0;
			if (buffer.Count == 0)
				return false;
			double target = buffer[buffer.Count - 1].Time - Math.Max(0, delayMs);
			double error = target - PlaybackMs;
			if (!playing || delayMs <= 0 || error > Math.Max(250.0, 3.0 * delayMs))
			{
				PlaybackMs = playing ? Math.Max(PlaybackMs, target) : target;
				playing = true;
			}
			else
				PlaybackMs += deltaMs * (1.0 + Clamp(error * DriftGain, -MaxSpeedChange, MaxSpeedChange));

			int passed = 0;
			while (passed + 1 < buffer.Count && buffer[passed + 1].Time <= PlaybackMs)
				passed++;
			if (passed > 0)
				buffer.RemoveRange(0, passed);
			from = buffer[0].Item;
			if (buffer.Count > 1)
			{
				to = buffer[1].Item;
				t = Clamp((PlaybackMs - buffer[0].Time) / (buffer[1].Time - buffer[0].Time), 0, 1);
			}
			return true;
		}

		static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
	}
}
