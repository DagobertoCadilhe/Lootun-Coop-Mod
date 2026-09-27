using System;
using System.Collections.Generic;
using LootClicker.Entities.Characters;
using LootClicker.Services;

namespace LootunCoop.Game
{
	/// <summary>
	/// Character &lt;-&gt; bytes using the game's own save code (<c>SaveFile.SaveCharacter</c>/<c>LoadCharacter</c>), so gear, gems,
	/// loadouts, passives and ascendancies all come along. SaveFile's static buffers are saved and restored around each call.
	/// </summary>
	internal static class CharacterCodec
	{
		const byte StartCharacterTag = 1;

		public static byte[] Serialize(Character character)
		{
			var writeData = SaveFile.WriteData;
			bool bigEndian = SaveFile.IsBigEndian;
			try
			{
				SaveFile.WriteData = new List<byte>(16 * 1024);
				SaveFile.IsBigEndian = !BitConverter.IsLittleEndian;
				SaveFile.SaveCharacter(character);
				return SaveFile.WriteData.ToArray();
			}
			finally
			{
				SaveFile.WriteData = writeData;
				SaveFile.IsBigEndian = bigEndian;
			}
		}

		/// <exception cref="FormatException">The data is not a character or is truncated.</exception>
		public static Character Deserialize(byte[] data)
		{
			if (data == null || data.Length < 2 || data[0] != StartCharacterTag)
				throw new FormatException("not a character");
			var readData = SaveFile.ReadData;
			int loadIndex = SaveFile.LoadIndex;
			bool bigEndian = SaveFile.IsBigEndian;
			try
			{
				SaveFile.ReadData = data;
				SaveFile.LoadIndex = 1;
				SaveFile.IsBigEndian = !BitConverter.IsLittleEndian;
				return SaveFile.LoadCharacter() ?? throw new FormatException("unknown character class");
			}
			catch (Exception e) when (e is IndexOutOfRangeException || e is ArgumentException)
			{
				throw new FormatException("truncated character data", e);
			}
			finally
			{
				SaveFile.ReadData = readData;
				SaveFile.LoadIndex = loadIndex;
				SaveFile.IsBigEndian = bigEndian;
			}
		}

		public static string Describe(Character c) =>
			c.Name + " (" + c.Class + " lvl " + c.Level + ", " + c.MaxHealth.ToString("0") + " HP, " + c.DamagePerSecond.ToString("0") + " DPS)";
	}
}
