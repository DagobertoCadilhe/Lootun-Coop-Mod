namespace LootunCoop.Net
{
	/// <summary>Wire constants. Bump <see cref="Version"/> on any incompatible change to framing or message layouts.</summary>
	public static class Protocol
	{
		public const int Version = 1;
		public const int DefaultPort = 28777;
		public const int HostPlayerId = 0;
		public const int MaxPlayersLimit = 4;
		public const int MaxNameLength = 24;

		/// <summary>Upper bound for one frame (type byte + payload). Protects against garbage/hostile length prefixes.</summary>
		public const int MaxFrameBytes = 8 * 1024 * 1024;

		/// <summary>Types below this are handled by the net layer; types at or above it are passed to the game layer.</summary>
		public const byte FirstGameMessage = 32;
	}

	public enum MessageType : byte
	{
		Hello = 1,
		Welcome = 2,
		Reject = 3,
		PlayerJoined = 4,
		PlayerLeft = 5,
		Ping = 6,
		Pong = 7,
		Goodbye = 8,

		Chat = Protocol.FirstGameMessage,
	}
}
