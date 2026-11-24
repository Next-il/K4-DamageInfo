using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
using PanoramaManager;

namespace K4ryuuDamageInfo;

/// <summary>One opponent on the card, already in the viewer's language.</summary>
internal sealed record DamageRow(
	string Name,
	int Dealt, string DealtHits,
	int Taken, string TakenHits,
	string Health, bool Dead,
	bool Killer);

/// <summary>What one viewer's card says. Built by the plugin, drawn by <see cref="DamageHud"/>.</summary>
internal sealed class DamageCard
{
	public bool Killed;
	public bool Compact;
	public string Eyebrow = "";
	public string Title = "";

	/// <summary>A name from <see cref="DamageHud.Weapons"/>, or null for no art.</summary>
	public string? Weapon;
	public bool Headshot;

	public int Dealt;
	public int Taken;
	public string DealtLabel = "Dealt";
	public string TakenLabel = "Taken";
	public string OpponentLabel = "Opponent";
	public string HealthLabel = "HP";

	public List<DamageRow> Rows = [];

	/// <summary>The compact line: plain, bold, plain, bold, ... (at most 7 segments).</summary>
	public List<string> Line = [];
}

/// <summary>
/// The damage card: the private panel that replaces K4-DamageInfo's "Statistics" block in chat. The layout
/// is hud/panorama/layout/custom_game/damage_hud.xml, shipped in the workshop addon.
///
/// <para><b>One layout, opened once per player and left open.</b> Its root is an invisible full-screen panel;
/// the card inside carries its own "show". The card is "hidden" (collapsed) while not in use and
/// un-collapsed a moment before it shows, so it always has a resting place to slide in from.</para>
///
/// <para><b>Everything is drawn from state.</b> PanoramaManager rebuilds a player's panel by itself after a
/// respawn, a round restart or a map change, and drops every per-viewer variable and class on the way
/// (PanelAction.Restored). Each viewer's card is a plain record here and <see cref="Restore"/> writes it
/// back. Every timed step (the entry, the bars, the exit) is a time in that record, run by
/// <see cref="Tick"/>, so nothing is left behind in a timer closure.</para>
///
/// <para>This class only draws. What a card says and when it comes and goes is the plugin's call.</para>
/// </summary>
internal sealed class DamageHud : IDisposable
{
	private const string Layout = "panorama/layout/custom_game/damage_hud.vxml_c";
	private const string RootId = "DamageRoot";
	private const string CardId = "dmg_card";
	private const string WeaponId = "dmg_wpn";

	/// <summary>Rows in the layout's pool. More opponents than this show the first 8 (the killer first).</summary>
	public const int RowPool = 8;

	/// <summary>dmg_s0..dmg_s6 in the layout: plain, bold, plain, bold, ...</summary>
	public const int LineSegments = 7;

	private const int BarSteps = 20;

	/// <summary>Un-collapse first, then "show": a transition needs the client to have seen the card at its
	/// resting offset (ToastService found one server frame is not enough).</summary>
	private const float EntrySeconds = 0.06f;

	/// <summary>The exit transition, before the card collapses or comes back with new content.</summary>
	private const float ExitSeconds = 0.35f;

	/// <summary>The preview's timing: the first row's bars start filling 0.26s after the card shows, each
	/// next row 0.06s later.</summary>
	private const float BarDelaySeconds = 0.26f;
	private const float BarStaggerSeconds = 0.06f;

	/// <summary>The wpn- classes damage_hud.css ships: the names CS2 puts in player_death's "weapon".</summary>
	public static readonly HashSet<string> Weapons =
	[
		"ak47", "aug", "awp", "bizon", "cz75a", "deagle", "elite", "famas", "fiveseven", "g3sg1", "galilar", "glock",
		"hkp2000", "m249", "m4a1", "m4a1_silencer", "mac10", "mag7", "mp5sd", "mp7", "mp9", "negev", "nova", "p250",
		"p90", "revolver", "sawedoff", "scar20", "sg556", "ssg08", "taser", "tec9", "ump45", "usp_silencer", "xm1014",
		"hegrenade", "inferno", "molotov", "incgrenade", "flashbang", "smokegrenade", "decoy", "planted_c4", "knife",
		"knife_t", "bayonet", "knife_bowie", "knife_butterfly", "knife_canis", "knife_cord", "knife_css",
		"knife_falchion", "knife_flip", "knife_gut", "knife_gypsy_jackknife", "knife_karambit", "knife_kukri",
		"knife_m9_bayonet", "knife_outdoor", "knife_push", "knife_skeleton", "knife_stiletto", "knife_survival_bowie",
		"knife_tactical", "knife_twinblade", "knife_ursus", "knife_widowmaker", "knifegg", "world", "trigger_hurt",
		"prop_exploding_barrel",
	];

	/// <summary>Every wpn- class, for a draw that has to clear whatever the client may still hold.</summary>
	private static readonly string[] WeaponClasses = Weapons.Select(w => $"wpn-{w}").ToArray();

	private sealed class Viewer
	{
		/// <summary>What the viewer should see; null when nothing.</summary>
		public DamageCard? Card;

		/// <summary>When the card goes by itself; -1 = until <see cref="Hide"/> (the respawn).</summary>
		public float Until = -1;

		/// <summary>"show" is on.</summary>
		public bool Shown;

		/// <summary>"hidden" is on (the layout authors it so).</summary>
		public bool Collapsed = true;

		/// <summary>When to write the card and turn "show" on; -1 when nothing is pending.</summary>
		public float ShowAt = -1;

		/// <summary>When to collapse a card that has finished leaving; -1 when nothing is pending.</summary>
		public float CollapseAt = -1;

		/// <summary>When the first row's bars fill; -1 when the bars are done.</summary>
		public float BarsAt = -1;
		public int BarsDone;

		/// <summary>The p-step class on each row's dealt and taken bar as last written; -1 = none (empty).</summary>
		public readonly int[] Dealt = Enumerable.Repeat(-1, RowPool).ToArray();
		public readonly int[] Taken = Enumerable.Repeat(-1, RowPool).ToArray();

		/// <summary>The wpn- class on dmg_wpn as last written.</summary>
		public string? WeaponClass;

		/// <summary>
		/// The client may hold a wpn- class this side no longer knows of: per-viewer classes live on the
		/// entity and outlast a Restored (respawn, round restart). The next draw turns every other
		/// weapon class off, or two of them sit on dmg_wpn and the stylesheet's later one wins - a
		/// knife drawn for a rifle kill (owner report 2026-11-24).
		/// </summary>
		public bool WeaponUnknown = true;

		/// <summary>Forgets every class the client may hold: after a rebuild or a fresh open they are gone.</summary>
		public void ResetClientState()
		{
			Shown = false;
			Collapsed = true;
			ShowAt = -1;
			CollapseAt = -1;
			BarsAt = -1;
			BarsDone = 0;
			Array.Fill(Dealt, -1);
			Array.Fill(Taken, -1);
			WeaponClass = null;
			WeaponUnknown = true;
		}
	}

	private readonly BasePlugin _plugin;
	private readonly Dictionary<int, Viewer> _viewers = [];
	private PanelHandle? _panel;

	public DamageHud(BasePlugin plugin)
	{
		_plugin = plugin;
	}

	public bool Enabled => _panel != null;

	/// <summary>Spawns the layout once. A second Spawn would orphan the first entity, so this is guarded.</summary>
	public void Init(string position)
	{
		if (_panel != null)
			return;

		try
		{
			Panorama.Init(_plugin);
			_panel = Panorama.Spawn(Layout, new LayoutContract
			{
				// Unique server-wide: dialog variables are keyed by panel id
				RootPanelId = RootId,
				RevealClass = "show",
				RowCount = 0,
				CaptureInput = false,
				HideHud = HideHudFlags.None,
				SharedText = false,
				SpectatingMessage = null,
			});
			_panel.SetVariant("pos", position == "chat" ? "chat" : "bottom");
			_panel.OnEvent += OnPanelEvent;
			_plugin.Logger.LogInformation("Damage HUD ready ({Position})", position == "chat" ? "over the chat" : "bottom center");
		}
		catch (Exception ex)
		{
			// The plugin works without its HUD: every caller falls back to chat when Enabled is false
			_plugin.Logger.LogWarning("Damage HUD failed to start, using chat instead: {Message}", ex.Message);
			_panel = null;
		}
	}

	public void Dispose()
	{
		_panel?.Dispose();
		_panel = null;
		_viewers.Clear();
	}

	private void OnPanelEvent(PanelEvent e)
	{
		if (e.Action == PanelAction.Restored && e.Player.IsValid)
			Restore(e.Player);
	}

	// ------------------------------------------------------------------ viewers

	private static bool IsHuman(CCSPlayerController? player) =>
		player is { IsValid: true, IsBot: false, IsHLTV: false } && player.Connected == PlayerConnectedState.Connected;

	private Viewer ViewerOf(CCSPlayerController player)
	{
		if (!_viewers.TryGetValue(player.Slot, out var viewer))
		{
			viewer = new Viewer();
			_viewers[player.Slot] = viewer;
		}

		return viewer;
	}

	/// <summary>Opens the player's session if it is not open yet. A fresh open starts from the layout as authored.</summary>
	private bool EnsureOpen(CCSPlayerController player)
	{
		if (_panel == null || !IsHuman(player))
			return false;

		if (!_panel.IsOpenFor(player))
		{
			_panel.Open(player);
			if (!_panel.IsOpenFor(player))
				return false;

			ViewerOf(player).ResetClientState();
		}

		return true;
	}

	public void Forget(int slot)
	{
		_viewers.Remove(slot);
		_panel?.Close(slot);
	}

	/// <summary>Map end: the clock restarts on the next map, so every stored time is meaningless now.</summary>
	public void Clear()
	{
		foreach (var viewer in _viewers.Values)
		{
			viewer.Card = null;
			viewer.Until = -1;
			viewer.ResetClientState();
		}
	}

	/// <summary>
	/// Writes the card back for one player, from state. Called after the library rebuilt the panel: every
	/// class it may have lost is written again and the bars fill again from empty.
	/// </summary>
	public void Restore(CCSPlayerController player)
	{
		if (_panel == null || !_panel.IsOpenFor(player))
			return;

		var viewer = ViewerOf(player);
		var now = Server.CurrentTime;

		viewer.ResetClientState();
		Cls(player, CardId, "show", false);

		if (viewer.Card != null && viewer.Until >= 0 && now >= viewer.Until)
			viewer.Card = null;

		if (viewer.Card == null)
		{
			Cls(player, CardId, "hidden", true);
			return;
		}

		Cls(player, CardId, "hidden", false);
		viewer.Collapsed = false;
		viewer.ShowAt = now + EntrySeconds;
	}

	// ------------------------------------------------------------------ showing and hiding

	/// <summary>
	/// Shows <paramref name="card"/> to the player, for <paramref name="seconds"/> or, when null, until
	/// <see cref="Hide"/>. A card already on screen leaves first and comes back with the new content.
	/// Returns false when the card cannot be shown to this player, so the caller prints its chat lines.
	/// </summary>
	public bool Show(CCSPlayerController player, DamageCard card, float? seconds)
	{
		if (!EnsureOpen(player))
			return false;

		var viewer = ViewerOf(player);
		var now = Server.CurrentTime;

		viewer.Card = card;
		viewer.Until = seconds is { } s ? now + Math.Max(0.5f, s) : -1;
		viewer.BarsAt = -1;

		if (viewer.Shown)
		{
			Cls(player, CardId, "show", false);
			viewer.Shown = false;
			EmptyBars(player, viewer);
			viewer.ShowAt = now + ExitSeconds;
		}
		else if (viewer.Collapsed)
		{
			Cls(player, CardId, "hidden", false);
			viewer.Collapsed = false;
			viewer.ShowAt = now + EntrySeconds;
		}
		else
		{
			// Still leaving, or about to enter: come in when that is done
			viewer.ShowAt = viewer.CollapseAt >= 0 ? viewer.CollapseAt : viewer.ShowAt >= 0 ? viewer.ShowAt : now + EntrySeconds;
		}

		viewer.CollapseAt = -1;
		return true;
	}

	/// <summary>Takes the player's card away (they respawned, the round started, they went to spectators).</summary>
	public void Hide(CCSPlayerController player)
	{
		if (_viewers.TryGetValue(player.Slot, out var viewer))
			Hide(player, viewer, Server.CurrentTime);
	}

	/// <summary>Every card, for everyone: a new round started.</summary>
	public void HideAll()
	{
		var now = Server.CurrentTime;

		foreach (var (slot, viewer) in _viewers)
		{
			if (viewer.Card == null && viewer.ShowAt < 0)
				continue;

			var player = Utilities.GetPlayerFromSlot(slot);
			if (player is { IsValid: true })
				Hide(player, viewer, now);
			else
				viewer.Card = null;
		}
	}

	private void Hide(CCSPlayerController player, Viewer viewer, float now)
	{
		viewer.Card = null;
		viewer.Until = -1;
		viewer.ShowAt = -1;
		viewer.BarsAt = -1;

		if (_panel == null || !_panel.IsOpenFor(player))
		{
			viewer.Shown = false;
			return;
		}

		if (viewer.Shown)
		{
			// The content stays as it is, so the card fades out showing what it showed; the bars drain with it
			Cls(player, CardId, "show", false);
			viewer.Shown = false;
			EmptyBars(player, viewer);
			viewer.CollapseAt = now + ExitSeconds;
		}
		else if (!viewer.Collapsed && viewer.CollapseAt < 0)
		{
			// Between a leave and a come-back: let the fade finish, then collapse
			viewer.CollapseAt = now + ExitSeconds;
		}
	}

	// ------------------------------------------------------------------ the clock

	/// <summary>Runs every timed step. The plugin calls this every server tick while the HUD is on.</summary>
	public void Tick()
	{
		if (_panel == null || _viewers.Count == 0)
			return;

		var now = Server.CurrentTime;

		foreach (var (slot, viewer) in _viewers)
		{
			if (viewer.Card == null && viewer.CollapseAt < 0)
				continue;

			var player = Utilities.GetPlayerFromSlot(slot);
			if (player is not { IsValid: true } || !_panel.IsOpenFor(player))
				continue;

			if (viewer.CollapseAt >= 0 && now >= viewer.CollapseAt)
			{
				viewer.CollapseAt = -1;
				if (viewer.Card == null)
				{
					Cls(player, CardId, "hidden", true);
					viewer.Collapsed = true;
				}
			}

			var card = viewer.Card;
			if (card == null)
				continue;

			if (viewer.ShowAt >= 0 && now >= viewer.ShowAt)
			{
				viewer.ShowAt = -1;
				Draw(player, viewer, card);
				Cls(player, CardId, "show", true);
				viewer.Shown = true;
				viewer.BarsDone = 0;
				viewer.BarsAt = card.Compact ? -1 : now + BarDelaySeconds;
			}

			if (viewer.Shown && viewer.BarsAt >= 0)
			{
				var rows = Math.Min(card.Rows.Count, RowPool);
				while (viewer.BarsDone < rows && now >= viewer.BarsAt + viewer.BarsDone * BarStaggerSeconds)
				{
					var row = card.Rows[viewer.BarsDone];
					Step(player, $"dr{viewer.BarsDone}_gf", viewer.Dealt, viewer.BarsDone, StepOf(row.Dealt));
					Step(player, $"dr{viewer.BarsDone}_tf", viewer.Taken, viewer.BarsDone, StepOf(row.Taken));
					viewer.BarsDone++;
				}

				if (viewer.BarsDone >= rows)
					viewer.BarsAt = -1;
			}

			if (viewer.Until >= 0 && now >= viewer.Until && viewer.ShowAt < 0)
				Hide(player, viewer, now);
		}
	}

	// ------------------------------------------------------------------ the bars

	private static int StepOf(int damage) => Math.Clamp((int)Math.Round(damage / 100.0 * BarSteps), 0, BarSteps);

	/// <summary>One p-step class on, the old one off. Off-then-on of different names is a real diff, so it arrives.</summary>
	private void Step(CCSPlayerController player, string id, int[] steps, int row, int to)
	{
		if (steps[row] == to)
			return;

		if (steps[row] >= 0)
			Cls(player, id, $"p{steps[row]}", false);
		Cls(player, id, $"p{to}", true);
		steps[row] = to;
	}

	/// <summary>Every bar back to empty, while the card is on its way out, so the next card fills from zero.</summary>
	private void EmptyBars(CCSPlayerController player, Viewer viewer)
	{
		for (var i = 0; i < RowPool; i++)
		{
			if (viewer.Dealt[i] > 0)
				Step(player, $"dr{i}_gf", viewer.Dealt, i, 0);
			if (viewer.Taken[i] > 0)
				Step(player, $"dr{i}_tf", viewer.Taken, i, 0);
		}
	}

	// ------------------------------------------------------------------ drawing

	/// <summary>Writes the card's text and state. The bars are left empty; <see cref="Tick"/> fills them.</summary>
	private void Draw(CCSPlayerController player, Viewer viewer, DamageCard card)
	{
		Cls(player, CardId, "st-killed", card.Killed);
		Cls(player, CardId, "compact", card.Compact);
		Cls(player, CardId, "hs", card.Headshot);

		Set(player, "dmg_eyebrow", card.Eyebrow);
		Set(player, "dmg_title", card.Title);

		var weapon = card.Weapon is { } w && Weapons.Contains(w) ? $"wpn-{w}" : null;
		if (viewer.WeaponUnknown)
		{
			foreach (var cls in WeaponClasses)
				if (cls != weapon)
					Cls(player, WeaponId, cls, false);
			if (weapon != null)
				Cls(player, WeaponId, weapon, true);
			viewer.WeaponClass = weapon;
			viewer.WeaponUnknown = false;
		}
		else if (viewer.WeaponClass != weapon)
		{
			if (viewer.WeaponClass != null)
				Cls(player, WeaponId, viewer.WeaponClass, false);
			if (weapon != null)
				Cls(player, WeaponId, weapon, true);
			viewer.WeaponClass = weapon;
		}
		Cls(player, WeaponId, "on", weapon != null);

		if (card.Compact)
		{
			DrawLine(player, card.Line);
			return;
		}

		Set(player, "dmg_l_dealt", card.DealtLabel);
		Set(player, "dmg_l_taken", card.TakenLabel);
		Set(player, "dmg_l_opp", card.OpponentLabel);
		Set(player, "dmg_l_hp", card.HealthLabel);
		Set(player, "dmg_given", card.Dealt.ToString());
		Set(player, "dmg_taken", card.Taken.ToString());

		for (var i = 0; i < RowPool; i++)
		{
			var id = $"dr{i}";
			if (i >= card.Rows.Count)
			{
				Cls(player, id, "hidden", true);
				continue;
			}

			var row = card.Rows[i];
			Cls(player, id, "hidden", false);
			Cls(player, id, "killer", row.Killer);
			Set(player, $"{id}_name", row.Name);
			Set(player, $"{id}_g", row.Dealt.ToString());
			Set(player, $"{id}_gh", row.DealtHits);
			Set(player, $"{id}_t", row.Taken.ToString());
			Set(player, $"{id}_th", row.TakenHits);
			Set(player, $"{id}_hp", row.Health);
			Cls(player, $"{id}_gn", "zero", row.Dealt == 0);
			Cls(player, $"{id}_tn", "zero", row.Taken == 0);
			Cls(player, $"{id}_hp", "dead", row.Dead);
		}
	}

	/// <summary>
	/// The compact line, one segment per label. A label next to a label has no word space, so every segment
	/// after a visible one gets "sp" unless it starts with punctuation (". kobi has" after "2 hits").
	/// </summary>
	private void DrawLine(CCSPlayerController player, List<string> line)
	{
		var anyBefore = false;

		for (var i = 0; i < LineSegments; i++)
		{
			var text = i < line.Count ? line[i].Trim() : "";
			var id = $"dmg_s{i}";

			Set(player, id, text);
			Cls(player, id, "hidden", text.Length == 0);
			Cls(player, id, "sp", anyBefore && text.Length > 0 && !char.IsPunctuation(text[0]));

			if (text.Length > 0)
				anyBefore = true;
		}
	}

	private void Set(CCSPlayerController player, string name, string value) => _panel!.SetVariableFor(player, name, value);

	private void Cls(CCSPlayerController player, string id, string cls, bool on) => _panel!.SetClassFor(player, id, cls, on);
}
