using System;
using System.Collections.Generic;
using System.Linq;
using LootClicker.Entities.Characters;
using LootClicker.Entities.Skills;
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

		/// <summary>
		/// Serialization with every experience value zeroed: equal fingerprints mean the same build, however much XP was gained.
		/// The character is restored afterwards.
		/// </summary>
		public static byte[] Fingerprint(Character c)
		{
			int exp = c.CurrentExperience;
			var skills = c.SkillDictionary.Values.Concat(c.FactionSkillDictionary.Values.Cast<MasterySkill>()).Where(s => s != null).Distinct()
				.Select(s => (s, s.CurrentExperience)).ToList();
			var ascendancies = c.Ascendancies.Values.Where(a => a != null).Select(a => (a, a.CurrentExperience)).ToList();
			try
			{
				c.CurrentExperience = 0;
				foreach (var (s, _) in skills)
					s.CurrentExperience = 0;
				foreach (var (a, _) in ascendancies)
					a.CurrentExperience = 0;
				return Serialize(c);
			}
			finally
			{
				c.CurrentExperience = exp;
				foreach (var (s, e) in skills)
					s.CurrentExperience = e;
				foreach (var (a, e) in ascendancies)
					a.CurrentExperience = e;
			}
		}

		public static string Describe(Character c) =>
			c.Name + " (" + c.Class + " lvl " + c.Level + ", " + c.MaxHealth.ToString("0") + " HP, " + c.DamagePerSecond.ToString("0") + " DPS)";
	}
}
