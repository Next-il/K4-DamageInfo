using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Utils;

namespace K4ryuuDamageInfo
{
	/// <summary>
	/// The card side of the plugin: turns the damage the plugin already collects into a <see cref="DamageCard"/>
	/// and hands it to <see cref="DamageHud"/>, which only draws. It replaces the "Statistics" block in chat
	/// (the ruled header, one line per opponent, the ruled footer); the console lines and the on-hit center
	/// message are untouched.
	///
	/// <para><b>Same numbers as the chat block:</b> damage and hits each way per opponent and their HP left,
	/// with the same filters (show-only-killer, friendly fire). The killer's row is marked and comes first.
	/// One difference: the chat block printed nothing when the player had dealt no damage, and the card also
	/// shows damage taken, so "Killed by kobi" appears even after a round where you never hit anyone.</para>
	///
	/// <para><b>When:</b> on death it stays until the respawn (or the next round start). At round end the
	/// players still alive see it for Hud.RoundEndSeconds; a dead player who has not had one gets it until the
	/// respawn. In Hud.Mode "deathmatch" it is one line about the killer, on death only.</para>
	/// </summary>
	public partial class DamageInfoPlugin
	{
		private DamageHud? _hud;

		/// <summary>The round the card names, taken at round start.</summary>
		private int _roundNumber;

		private bool HudOn => Config.Hud.Enabled && _hud is { Enabled: true };

		private bool DeathmatchMode => string.Equals(Config.Hud.Mode, "deathmatch", StringComparison.OrdinalIgnoreCase);

		private enum CardResult { Shown, Nothing, Failed }

		private void HudLoad()
		{
			if (!Config.Hud.Enabled)
				return;

			_hud = new DamageHud(this);
			_hud.Init(string.Equals(Config.Hud.Position, "chat", StringComparison.OrdinalIgnoreCase) ? "chat" : "bottom");

			if (!_hud.Enabled)
				return;

			RegisterEventHandler<EventPlayerTeam>(OnHudPlayerTeam);
			RegisterEventHandler<EventPlayerDisconnect>(OnHudPlayerDisconnect);
		}

		private void HudUnload()
		{
			_hud?.Dispose();
			_hud = null;
		}

		private HookResult OnHudPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
		{
			// Gone to spectators: there is no respawn coming to take the card away
			if (@event.Userid is { IsValid: true } player && @event.Team <= (int)CsTeam.Spectator)
				_hud?.Hide(player);

			return HookResult.Continue;
		}

		private HookResult OnHudPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
		{
			// A leaver's slot must not hand the card to whoever joins next
			if (@event.Userid is { IsValid: true } player)
				_hud?.Forget(player.Slot);

			return HookResult.Continue;
		}

		// ------------------------------------------------------------------ the card

		private CardResult ShowCard(CCSPlayerController player, PlayerData data, bool roundEnd)
		{
			var killer = KillerOf(player, data);

			if (DeathmatchMode)
			{
				// One line about the killer, on death only: no killer (a fall, the bomb) is nothing to say
				if (roundEnd || killer == null)
					return CardResult.Nothing;

				return _hud!.Show(player, DeathmatchCard(player, data, killer), null) ? CardResult.Shown : CardResult.Failed;
			}

			var rows = BuildRows(player, data, killer);
			if (rows.Count == 0)
				return CardResult.Nothing;

			var card = new DamageCard
			{
				Dealt = rows.Sum(r => r.Dealt),
				Taken = rows.Sum(r => r.Taken),
				Rows = rows.Take(DamageHud.RowPool).ToList(),
				DealtLabel = T(player, "phrases.hud.dealt", "Dealt"),
				TakenLabel = T(player, "phrases.hud.taken", "Taken"),
				OpponentLabel = T(player, "phrases.hud.opponent", "Opponent"),
				HealthLabel = T(player, "phrases.hud.hp", "HP"),
			};

			float? seconds = null;

			if (!roundEnd || !player.PawnIsAlive)
			{
				card.Killed = true;

				if (killer != null)
				{
					card.Eyebrow = T(player, "phrases.hud.killedby", "Killed by");
					card.Title = killer.PlayerName;
					card.Weapon = WeaponArt(data.KillerWeapon);
					card.Headshot = data.KillerHeadshot;
				}
				else
				{
					card.Eyebrow = T(player, "phrases.hud.died", "You died");
					card.Title = T(player, "phrases.hud.yourdamage", "Your damage");
				}
			}
			else
			{
				int round = _roundNumber > 0 ? _roundNumber : Math.Max(1, GameRules?.TotalRoundsPlayed ?? 1);
				card.Eyebrow = T(player, "phrases.hud.round", "Round {0}", round);
				card.Title = card.Taken == 0
					? T(player, "phrases.hud.nodamagetaken", "No damage taken")
					: T(player, "phrases.hud.yourdamage", "Your damage");
				seconds = Config.Hud.RoundEndSeconds;
			}

			return _hud!.Show(player, card, seconds) ? CardResult.Shown : CardResult.Failed;
		}

		/// <summary>"Killed by kobi [deagle] [hs]" and "You dealt 54 in 2 hits. kobi has 46 HP left."</summary>
		private DamageCard DeathmatchCard(CCSPlayerController player, PlayerData data, CCSPlayerController killer)
		{
			data.DamageInfo.GivenDamage.TryGetValue(killer.Slot, out var given);
			int dealt = given?.TotalDamage ?? 0;
			int hits = given?.Hits ?? 0;
			int health = HealthOf(killer);

			object[] args = [dealt, Hits(player, hits), killer.PlayerName, health];

			var line = (dealt > 0, health > 0) switch
			{
				(true, true) => Line(player, "phrases.hud.dm.dealt", "You dealt *{0}* in *{1}*. {2} has *{3} HP* left.", args),
				(true, false) => Line(player, "phrases.hud.dm.dealt.dead", "You dealt *{0}* in *{1}*. {2} is dead too.", args),
				(false, true) => Line(player, "phrases.hud.dm.none", "You dealt no damage. {2} has *{3} HP* left.", args),
				(false, false) => Line(player, "phrases.hud.dm.none.dead", "You dealt no damage. {2} is dead too.", args),
			};

			return new DamageCard
			{
				Killed = true,
				Compact = true,
				Eyebrow = T(player, "phrases.hud.killedby", "Killed by"),
				Title = killer.PlayerName,
				Weapon = WeaponArt(data.KillerWeapon),
				Headshot = data.KillerHeadshot,
				Line = line,
			};
		}

		/// <summary>One row per opponent, with the chat block's filters: the killer first, then by damage dealt.</summary>
		private List<DamageRow> BuildRows(CCSPlayerController player, PlayerData data, CCSPlayerController? killer)
		{
			var info = data.DamageInfo;
			var slots = new List<int>();

			foreach (var slot in info.GivenDamage.Keys.Concat(info.TakenDamage.Keys))
			{
				if (Config.ShowOnlyKiller && data.VictimKiller != slot)
					continue;

				if (!slots.Contains(slot))
					slots.Add(slot);
			}

			var rows = new List<DamageRow>(slots.Count);

			foreach (var slot in slots)
			{
				info.GivenDamage.TryGetValue(slot, out var given);
				info.TakenDamage.TryGetValue(slot, out var taken);

				var other = Utilities.GetPlayerFromSlot(slot);
				string name = (other != null && other.IsValid) ? other.PlayerName : "Unknown";
				int health = HealthOf(other);

				rows.Add(new DamageRow(
					Name: name,
					Dealt: given?.TotalDamage ?? 0,
					DealtHits: Hits(player, given?.Hits ?? 0),
					Taken: taken?.TotalDamage ?? 0,
					TakenHits: Hits(player, taken?.Hits ?? 0),
					Health: health > 0 ? health.ToString() : T(player, "phrases.dead", "DEAD"),
					Dead: health <= 0,
					Killer: killer != null && slot == killer.Slot));
			}

			return rows
				.OrderByDescending(r => r.Killer)
				.ThenByDescending(r => r.Dealt)
				.ThenByDescending(r => r.Taken)
				.ToList();
		}

		/// <summary>Who killed this player, when it was someone else who is still on the server.</summary>
		private static CCSPlayerController? KillerOf(CCSPlayerController player, PlayerData data)
		{
			if (data.KillerSlot < 0 || data.KillerSlot == player.Slot)
				return null;

			var killer = Utilities.GetPlayerFromSlot(data.KillerSlot);
			return killer is { IsValid: true } ? killer : null;
		}

		/// <summary>The same HP the chat line printed: the pawn's health, 0 when there is no pawn.</summary>
		private static int HealthOf(CCSPlayerController? player) =>
			(player?.PlayerPawn is { IsValid: true } pawn) ? pawn.Value!.Health : 0;

		/// <summary>player_death's "weapon" as one of the layout's wpn- classes, or null for no art.</summary>
		private static string? WeaponArt(string? weapon)
		{
			if (string.IsNullOrWhiteSpace(weapon))
				return null;

			var name = weapon.Trim().ToLowerInvariant();
			if (name.StartsWith("weapon_"))
				name = name["weapon_".Length..];

			if (DamageHud.Weapons.Contains(name))
				return name;

			// A knife the list does not have yet still gets a knife
			return name.StartsWith("knife") || name.Contains("bayonet") ? "knife" : null;
		}

		// ------------------------------------------------------------------ text

		private string Hits(CCSPlayerController player, int hits) =>
			hits == 1 ? T(player, "phrases.hud.hit", "{0} hit", hits) : T(player, "phrases.hud.hits", "{0} hits", hits);

		/// <summary>The player's phrase, or the English fallback when the server's lang files don't have it yet.</summary>
		private string Raw(CCSPlayerController player, string key, string fallback)
		{
			string text;
			try
			{
				text = Localizer.ForPlayer(player, key);
			}
			catch (Exception)
			{
				text = key;
			}

			return string.IsNullOrEmpty(text) || text == key ? fallback : text;
		}

		private string T(CCSPlayerController player, string key, string fallback, params object[] args)
		{
			var text = Raw(player, key, fallback);
			if (args.Length == 0)
				return text;

			try
			{
				return string.Format(text, args);
			}
			catch (FormatException)
			{
				return string.Format(fallback, args);
			}
		}

		/// <summary>
		/// A phrase with *bold* parts as the compact line's segments: plain, bold, plain, ... The phrase is split
		/// before the names go in, so a '*' in a player's name stays text.
		/// </summary>
		private List<string> Line(CCSPlayerController player, string key, string fallback, object[] args)
		{
			List<string> Split(string template)
			{
				var parts = template.Split('*').Select(part => string.Format(part, args)).ToList();
				if (parts.Count > DamageHud.LineSegments)
				{
					// More bold runs than the layout has labels: the rest goes into the last one, plain
					var rest = string.Concat(parts.Skip(DamageHud.LineSegments - 1));
					parts = [.. parts.Take(DamageHud.LineSegments - 1), rest];
				}

				return parts;
			}

			try
			{
				return Split(Raw(player, key, fallback));
			}
			catch (FormatException)
			{
				return Split(fallback);
			}
		}
	}
}
