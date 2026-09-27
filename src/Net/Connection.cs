using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace LootunCoop.Net
{
	/// <summary>Something that happened on a background socket thread, handed to the main thread through a queue.</summary>
	internal struct NetEvent
	{
		public Connection Connection;
		public MessageType Type;
		public byte[] Payload;
		/// <summary>True when the connection closed; <see cref="Reason"/> says why.</summary>
		public bool Closed;
		public string Reason;
	}

	internal static class Clock
	{
		static readonly Stopwatch watch = Stopwatch.StartNew();

		public static long NowMs => watch.ElapsedMilliseconds;
	}

	/// <summary>
	/// One TCP peer. Frames are [int32 LE length][byte type][payload], length counting type + payload.
	/// A reader thread and a writer thread per connection; nothing here touches Unity or game state.
	/// </summary>
	internal sealed class Connection
	{
		readonly TcpClient tcp;
		readonly NetworkStream stream;
		readonly ConcurrentQueue<NetEvent> inbox;
		readonly BlockingCollection<byte[]> outbox = new BlockingCollection<byte[]>();
		int closed;
		long lastReceivedMs;

		public readonly string RemoteAddress;
		public readonly long CreatedMs = Clock.NowMs;
		/// <summary>Owner-defined state (host: the player id once the handshake succeeded).</summary>
		public object Tag;

		public long LastReceivedMs => Interlocked.Read(ref lastReceivedMs);
		public bool IsClosed => Volatile.Read(ref closed) != 0;

		public Connection(TcpClient tcp, ConcurrentQueue<NetEvent> inbox)
		{
			this.tcp = tcp;
			this.inbox = inbox;
			tcp.NoDelay = true;
			stream = tcp.GetStream();
			RemoteAddress = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
			lastReceivedMs = Clock.NowMs;
		}

		public void Start()
		{
			new Thread(ReadLoop) { IsBackground = true, Name = "LootunCoop recv " + RemoteAddress }.Start();
			new Thread(WriteLoop) { IsBackground = true, Name = "LootunCoop send " + RemoteAddress }.Start();
		}

		public void Send(MessageType type, byte[] payload)
		{
			if (IsClosed || outbox.IsAddingCompleted)
				return;
			int len = 1 + (payload?.Length ?? 0);
			if (len > Protocol.MaxFrameBytes)
				throw new ArgumentException("payload too large: " + len + " bytes");
			var frame = new byte[4 + len];
			frame[0] = (byte)len;
			frame[1] = (byte)(len >> 8);
			frame[2] = (byte)(len >> 16);
			frame[3] = (byte)(len >> 24);
			frame[4] = (byte)type;
			if (payload != null)
				Buffer.BlockCopy(payload, 0, frame, 5, payload.Length);
			try
			{
				outbox.Add(frame);
			}
			catch (InvalidOperationException)
			{
				// Raced with Close/CloseAfterFlush.
			}
		}

		/// <summary>Sends what is already queued (e.g. a Reject or Goodbye), then closes.</summary>
		public void CloseAfterFlush()
		{
			outbox.CompleteAdding();
		}

		/// <summary>Closes immediately. Queues exactly one Closed event for the owner, whoever closes first.</summary>
		public void Close(string reason)
		{
			if (Interlocked.Exchange(ref closed, 1) != 0)
				return;
			outbox.CompleteAdding();
			try
			{
				tcp.Close();
			}
			catch
			{
			}
			inbox.Enqueue(new NetEvent { Connection = this, Closed = true, Reason = reason });
		}

		void ReadLoop()
		{
			var header = new byte[4];
			try
			{
				while (!IsClosed)
				{
					if (!ReadExactly(header, 4))
					{
						Close("connection closed by remote");
						return;
					}
					int len = header[0] | header[1] << 8 | header[2] << 16 | header[3] << 24;
					if (len < 1 || len > Protocol.MaxFrameBytes)
					{
						Close("invalid frame length " + len);
						return;
					}
					var body = new byte[len];
					if (!ReadExactly(body, len))
					{
						Close("connection closed by remote");
						return;
					}
					var payload = new byte[len - 1];
					Buffer.BlockCopy(body, 1, payload, 0, payload.Length);
					Interlocked.Exchange(ref lastReceivedMs, Clock.NowMs);
					inbox.Enqueue(new NetEvent { Connection = this, Type = (MessageType)body[0], Payload = payload });
				}
			}
			catch (Exception e) when (e is IOException || e is SocketException || e is ObjectDisposedException)
			{
				Close(IsClosed ? "closed" : "connection lost (" + e.GetType().Name + ": " + e.Message + ")");
			}
		}

		bool ReadExactly(byte[] buffer, int count)
		{
			int read = 0;
			while (read < count)
			{
				int n = stream.Read(buffer, read, count - read);
				if (n <= 0)
					return false;
				read += n;
			}
			return true;
		}

		void WriteLoop()
		{
			try
			{
				foreach (var frame in outbox.GetConsumingEnumerable())
				{
					if (IsClosed)
						return;
					stream.Write(frame, 0, frame.Length);
				}
				// CompleteAdding was called: either Close (no-op below) or CloseAfterFlush (everything sent, now close).
				Close("closed");
			}
			catch (Exception e) when (e is IOException || e is SocketException || e is ObjectDisposedException)
			{
				Close(IsClosed ? "closed" : "send failed (" + e.GetType().Name + ": " + e.Message + ")");
			}
		}
	}
}
