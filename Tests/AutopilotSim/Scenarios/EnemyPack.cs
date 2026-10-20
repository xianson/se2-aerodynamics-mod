using AeroMod;

/// <summary>
/// THE ENEMY TOGGLE AND THE TARGET FILTER (Autopilot/ApEnemy.cs): the relation from the game's own rule (a mock of SE2 4.0's
/// RelationsSessionComponent.GetRelationFromOwners and the factions, identities as numbers - 1 the neutral NPC, 2 the hostile
/// NPC, players above) and the filter's matrix; the switch's state machine and permission rules over a mocked world (the world's
/// rule, the owner, creative, admin; the owner before saved with the block and restored after a save round trip); an "enemy"
/// six-axis gunship in Engage that leaves its nearer wingman alone and chases the player; a player's drone that takes the raider
/// (hostile only) or a nearer neutral NPC hauler (+ neutral), never a faction mate's ship. (To the hostile NPC everything but
/// another hostile NPC is hostile - the game's rule - so an enemy's filter only matters for Everything.)
/// </summary>
sealed class EnemyPack : IScenarioPack
{
    public void Register(ScenarioRegistry r)
    {
        r.Add("enemy: relations by the game's rule (mocked identities) and the filter's matrix", true, Relations);
        r.Add("enemy: the switch - rules, the whole ship (joined later / detached / destroyed / split), per-grid owners, save round trip", true, Switch);
        r.Add("enemy: an enemy gunship chases the player, not its wingman; a drone takes the raider, or a nearer neutral with + neutral", true, Chase);
    }

    // ── the game's rule, mocked ──
    public const long NeutralNpc = 1, HostileNpc = 2;
    /// <summary>SE2 4.0 RelationsSessionComponent.GetRelationFromOwners: hostile when exactly one owner is the hostile NPC.</summary>
    public static bool GameHostile(long a, long b) => (a == HostileNpc) ^ (b == HostileNpc);
    static readonly Dictionary<long, int> Faction = new() { [1001] = 7, [1002] = 7 };
    static bool SameFaction(long a, long b) => Faction.TryGetValue(a, out int fa) && Faction.TryGetValue(b, out int fb) && fa == fb;
    /// <summary>The relation of an owned target (owner b) to a grid of owner a, as ApGameSense's reader asks the game.</summary>
    public static ApRelation Rel(long a, long b, bool self = false)
        => ApRelations.Classify(self, true, a == b, a != b && GameHostile(a, b), a != b && !GameHostile(a, b) && SameFaction(a, b), false);

    static void Relations()
    {
        // (player 1000; 1001 and 1002 one faction; 1003 another player)
        var cases = new (string what, long a, long b, ApRelation want)[]
        {
            ("enemy -> player", HostileNpc, 1000, ApRelation.Hostile),
            ("player -> enemy", 1000, HostileNpc, ApRelation.Hostile),
            ("enemy -> enemy (its wingman)", HostileNpc, HostileNpc, ApRelation.Own),
            ("enemy -> neutral NPC", HostileNpc, NeutralNpc, ApRelation.Hostile),   // (the game's rule: exactly one hostile NPC)
            ("player -> neutral NPC", 1000, NeutralNpc, ApRelation.Neutral),
            ("player -> another player", 1000, 1003, ApRelation.Neutral),
            ("player -> a faction mate", 1001, 1002, ApRelation.Friendly),
            ("player -> own other ship", 1000, 1000, ApRelation.Own),
        };
        foreach (var c in cases) Sim.Check(Rel(c.a, c.b) == c.want, $"relation {c.what}: {Rel(c.a, c.b)} (want {c.want})");
        Sim.Check(ApRelations.Classify(true, true, true, false, false, false) == ApRelation.Self, "relation: the ship itself");
        Sim.Check(ApRelations.Classify(false, false, false, false, false, true) == ApRelation.Hostile && ApRelations.Classify(false, false, false, false, false, false) == ApRelation.Neutral,
                  "relation: no owner - a priority target is hostile (IsEligibleTarget), else neutral");
        // the filter's matrix
        var rels = new[] { ApRelation.Self, ApRelation.Own, ApRelation.Friendly, ApRelation.Neutral, ApRelation.Hostile };
        var want = new Dictionary<ApTargetFilter, bool[]>
        {
            [ApTargetFilter.Hostile] = new[] { false, false, false, false, true },
            [ApTargetFilter.HostileNeutral] = new[] { false, false, false, true, true },
            [ApTargetFilter.Everything] = new[] { false, true, true, true, true },
        };
        foreach (var (f, w) in want)
            for (int i = 0; i < rels.Length; i++)
                Sim.Check(ApRelations.Passes(f, rels[i]) == w[i], $"filter {f} on {rels[i]}: {(w[i] ? "taken" : "refused")}");
        // the scan never picks the same owner's, even with Everything; nearest first
        var pk = new ApTargetPicker(8);
        pk.Add(new Vector3D(50, 0, 0), ApRelation.Own, 0); pk.Add(new Vector3D(300, 0, 0), ApRelation.Neutral, 1); pk.Add(new Vector3D(900, 0, 0), ApRelation.Hostile, 2);
        pk.Add(new Vector3D(5000, 0, 0), ApRelation.Hostile, 3); pk.Add(new Vector3D(100, 0, 0), ApRelation.Friendly, 4);
        Sim.Check(pk.Pick(Vector3D.Zero, 4000, ApTargetFilter.Hostile) == 2 && pk.Pick(Vector3D.Zero, 4000, ApTargetFilter.HostileNeutral) == 1
                  && pk.Pick(Vector3D.Zero, 4000, ApTargetFilter.Everything) == 4 && pk.Pick(Vector3D.Zero, 500, ApTargetFilter.Hostile) == -1,
                  "picker: hostile -> the hostile at 900 m; + neutral -> the neutral at 300; everything -> the friendly at 100 (never its own at 50); none within 500");
        Sim.Check(ApRelations.Refused("Bob's Miner", ApRelation.Neutral, ApTargetFilter.Hostile) == "Bob's Miner is neutral (filter: hostile only)", "the refusal's words");
    }

    /// <summary>A mocked world: grids (keys) and their owners, the ship's links (which grids are joined to the block's grid 1 by a
    /// rotor / hinge / piston now), the asking player, the game's transfers (CanTransferOwnership's: never to the owner it has).</summary>
    sealed class World : IApOwnership
    {
        public readonly Dictionary<long, long> Owner = new() { [1] = 1000 };
        public readonly HashSet<long> Linked = new();   // (grids joined to grid 1 now)
        public long Requester = 1000; public bool Creative, Admin, HasGrid = true, Fail;
        public readonly HashSet<long> Players = new() { 1000, 1003 };
        public int Transfers;
        public long BlockGrid => 1;
        public bool IsEnemy(out bool hasGrid) { hasGrid = HasGrid && Owner.ContainsKey(1); return hasGrid && Owner[1] == HostileNpc; }
        public ApEnemyWho Who(out long requester) { requester = Requester; return new ApEnemyWho { Owner = Owner.TryGetValue(1, out long o) && o == Requester, Creative = Creative, Admin = Admin }; }
        public void Ship(List<ApGridOwner> into)
        {
            if (!HasGrid || !Owner.ContainsKey(1)) return;
            into.Add(new ApGridOwner { Grid = 1, Owner = Owner[1] });
            foreach (var g in Linked) if (Owner.TryGetValue(g, out long o)) into.Add(new ApGridOwner { Grid = g, Owner = o });
        }
        public bool KnownPlayer(long owner) => Players.Contains(owner);
        public bool MakeEnemy(long grid) { if (Fail || !Owner.TryGetValue(grid, out long o) || o == HostileNpc) return false; Owner[grid] = HostileNpc; Transfers++; return true; }
        public bool GiveTo(long grid, long owner) { if (Fail || !Owner.TryGetValue(grid, out long o) || o == owner) return false; Owner[grid] = owner; Transfers++; return true; }
        public World Ship2(params (long grid, long owner)[] gs) { foreach (var (g, o) in gs) { Owner[g] = o; Linked.Add(g); } return this; }
    }

    static (ApEnemySwitch sw, ApPanel p) On(World w, ApEnemyRule rule = ApEnemyRule.CreativeOrAdmin)
    {
        var sw = new ApEnemySwitch(); var p = ApPanel.Defaults; p.Mode = ApMode.Engage;
        sw.Request(true, w, rule); sw.Step(w, rule, ref p.Enemy, ref p.EnemyPrevOwner);
        return (sw, p);
    }
    static void Off(World w, ApEnemySwitch sw, ref ApPanel p, ApEnemyRule rule = ApEnemyRule.CreativeOrAdmin) { sw.Request(false, w, rule); sw.Step(w, rule, ref p.Enemy, ref p.EnemyPrevOwner); }

    static void Switch()
    {
        // the rules alone
        var survival = new ApEnemyWho { Owner = true };
        var creative = new ApEnemyWho { Owner = true, Creative = true };
        var admin = new ApEnemyWho { Owner = false, Admin = true };
        Sim.Check(ApEnemy.CanTurnOn(ApEnemyRule.Off, in creative, false) == ApEnemyRefusal.WorldOff, "rule off: nobody switches it on");
        Sim.Check(ApEnemy.CanTurnOn(ApEnemyRule.CreativeOrAdmin, in survival, false) == ApEnemyRefusal.NotCreativeOrAdmin, "rule creative-or-admin: a survival owner is refused");
        Sim.Check(ApEnemy.CanTurnOn(ApEnemyRule.CreativeOrAdmin, in creative, false) == ApEnemyRefusal.None, "rule creative-or-admin: a creative owner may");
        Sim.Check(ApEnemy.CanTurnOn(ApEnemyRule.CreativeOrAdmin, in admin, false) == ApEnemyRefusal.NotOwner, "an admin who does not own the ship is refused (owner only)");
        Sim.Check(ApEnemy.CanTurnOn(ApEnemyRule.Everyone, in survival, false) == ApEnemyRefusal.None, "rule everyone: a survival owner may");
        Sim.Check(ApEnemy.CanTurnOn(ApEnemyRule.Everyone, new ApEnemyWho(), false) == ApEnemyRefusal.NotOwner, "rule everyone: not the owner - refused");
        Sim.Check(ApEnemy.CanTurnOff(in survival, true) == ApEnemyRefusal.Locked && ApEnemy.CanTurnOff(in admin, true) == ApEnemyRefusal.None
                  && ApEnemy.CanTurnOff(new ApEnemyWho { Creative = true }, true) == ApEnemyRefusal.None, "off: creative or an admin only (a survival player cannot flip an enemy back)");
        Sim.Check(!ApEnemy.CanEdit(true, in survival) && ApEnemy.CanEdit(true, in admin) && ApEnemy.CanEdit(false, in survival), "an enemy's panel: creative / admin only; any other ship's: anyone");
        Sim.Check(ApEnemy.RestoreTo(1000, true, 1003) == 1000 && ApEnemy.RestoreTo(1000, false, 1003) == 1003 && ApEnemy.RestoreTo(0, false, 1003) == 1003, "restore: the owner before if known, else the one switching it off");

        // the WHOLE SHIP: grid 1 (the block's) with a rotor turret grid 2 and a piston arm grid 3, all the player's
        var w = new World { Creative = true }.Ship2((2, 1000), (3, 1000));
        var sw = new ApEnemySwitch(); var panel = ApPanel.Defaults; panel.Mode = ApMode.Engage;
        sw.Request(true, w, ApEnemyRule.CreativeOrAdmin);
        Sim.Check(sw.Ask == 1 && w.Owner[1] == 1000, "asked on: queued for the block's server step (nothing changed yet)");
        sw.Step(w, ApEnemyRule.CreativeOrAdmin, ref panel.Enemy, ref panel.EnemyPrevOwner);
        Sim.Check(w.Owner[1] == HostileNpc && w.Owner[2] == HostileNpc && w.Owner[3] == HostileNpc && sw.Flipped == 3 && panel.Enemy && panel.EnemyPrevOwner == 1000
                  && sw.Prev.Count == 3 && sw.Text().StartsWith("Enemy ship"), $"switched on: the block's grid and both joined grids are the hostile NPC's ({sw.Flipped}), each owner before kept - '{sw.Text()}'");

        // a joined grid of someone else's: refused, nothing taken
        var wx = new World { Creative = true }.Ship2((2, 1003));
        var (swx, px) = On(wx);
        Sim.Check(wx.Owner[1] == 1000 && wx.Owner[2] == 1003 && !px.Enemy && wx.Transfers == 0 && swx.Text().Contains("belongs to someone else"), $"a joined grid of another player's: refused, nothing changed - '{swx.Text()}'");

        // while an enemy: a grid joined later is flipped (its owner kept), a grid detached is left as it is and forgotten
        w.Ship2((4, 1000));
        sw.Step(w, ApEnemyRule.CreativeOrAdmin, ref panel.Enemy, ref panel.EnemyPrevOwner);
        Sim.Check(w.Owner[4] == HostileNpc && sw.Prev.TryGetValue(4, out long p4) && p4 == 1000 && sw.JoinedLater == 1, "a grid joined later (a new rotor head): flipped, its owner before kept");
        w.Linked.Remove(3);
        sw.Step(w, ApEnemyRule.CreativeOrAdmin, ref panel.Enemy, ref panel.EnemyPrevOwner);
        Sim.Check(w.Owner[3] == HostileNpc && !sw.Prev.ContainsKey(3), "a grid detached while an enemy: left as it is (the hostile NPC's), forgotten");
        // a grid destroyed; a split piece still joined (a new grid with no entry, the NPC's by the game's split copy)
        w.Linked.Remove(2); w.Owner.Remove(2);
        w.Owner[5] = HostileNpc; w.Linked.Add(5);
        sw.Step(w, ApEnemyRule.CreativeOrAdmin, ref panel.Enemy, ref panel.EnemyPrevOwner);
        Sim.Check(!sw.Prev.ContainsKey(2) && !sw.Prev.ContainsKey(5), "a grid destroyed: forgotten; a split piece (already the NPC's): no entry");

        // saved and loaded: the panel's key and each grid's owner before (one entry per grid: grid -> owner)
        Sim.Check(AeroBlockSaveForm.TryReadAutopilot(AeroBlockSaveForm.Autopilot(panel), out var loaded) && loaded.Enemy && loaded.EnemyPrevOwner == 1000, "saved and loaded: enemy, the block grid's owner before 1000");
        var saved = new Dictionary<string, long>(); int i = 0;
        foreach (var kv in sw.Prev) saved[AeroBlockSaveForm.EnemyPrev(i++, kv.Value)] = kv.Key;
        var sw2 = new ApEnemySwitch();
        foreach (var kv in saved) if (AeroBlockSaveForm.TryReadEnemyPrev(kv.Key, out long ow)) sw2.Prev[kv.Value] = ow;
        Sim.Check(sw2.Prev.Count == sw.Prev.Count && sw2.Prev[1] == 1000 && sw2.Prev[4] == 1000 && saved.Keys.All(k => k.StartsWith(AeroBlockSaveForm.EnemyPrevKey)),
                  $"the owners before round trip, one key per grid ({saved.Count}: '{saved.Keys.First()}')");
        sw2.Step(w, ApEnemyRule.CreativeOrAdmin, ref loaded.Enemy, ref loaded.EnemyPrevOwner);
        Sim.Check(sw2.GridIsEnemy && loaded.Enemy, "after the load: read as an enemy");

        // a survival player at the enemy's panel: locked, cannot switch it back
        w.Creative = false; w.Admin = false; w.Requester = 1003;
        Sim.Check(!sw2.MayEdit(w) && sw2.Text().Contains("only creative or an admin"), $"a survival player: the enemy's panel is locked - '{sw2.Text()}'");
        sw2.Request(false, w, ApEnemyRule.CreativeOrAdmin);
        Sim.Check(sw2.Ask == 0 && w.Owner[1] == HostileNpc, "a survival player cannot switch it off");
        // off by an admin (the world's rule off does not stop it): every grid still in the ship back to its own owner before; the split
        // piece (no entry) to the block grid's; the detached grid left the NPC's
        w.Admin = true;
        sw2.Request(false, w, ApEnemyRule.Off);
        sw2.Step(w, ApEnemyRule.Off, ref loaded.Enemy, ref loaded.EnemyPrevOwner);
        Sim.Check(w.Owner[1] == 1000 && w.Owner[4] == 1000 && w.Owner[5] == 1000 && w.Owner[3] == HostileNpc && !sw2.GridIsEnemy && !loaded.Enemy && sw2.Prev.Count == 0 && sw2.Restored == 3,
                  $"switched off by an admin: the ship back to 1000 (grids 1, 4 and the split piece 5: {sw2.Restored}), the detached grid 3 left the NPC's");
        // per grid: a joined grid's own owner before kept apart from the block grid's (a faction mate's turret given back to them)
        var wf = new World { Creative = true }; wf.Players.Add(1002);
        var swf = new ApEnemySwitch(); var pf = ApPanel.Defaults;
        swf.Prev[1] = 1000; swf.Prev[2] = 1002; wf.Owner[1] = HostileNpc; wf.Ship2((2, HostileNpc)); wf.Admin = true;
        swf.Step(wf, ApEnemyRule.CreativeOrAdmin, ref pf.Enemy, ref pf.EnemyPrevOwner);
        Off(wf, swf, ref pf);
        Sim.Check(wf.Owner[1] == 1000 && wf.Owner[2] == 1002, "per grid: each grid back to its own owner before (1000, 1002)");
        // the owner before gone: the one switching it off gets it, and says so
        var w3 = new World { Creative = true }.Ship2((2, 1000)); var (sw3, p3) = On(w3);
        w3.Players.Remove(1000); w3.Requester = 1003; w3.Admin = true;
        Off(w3, sw3, ref p3);
        Sim.Check(w3.Owner[1] == 1003 && w3.Owner[2] == 1003 && sw3.Text().Contains("the ship is yours"), $"the owner before is gone: the admin gets the ship - '{sw3.Text()}'");
        // made an enemy by someone else (an encounter): read as an enemy with no owners before; off gives it to the one who does
        var w4 = new World { Requester = 1000, Admin = true }.Ship2((2, HostileNpc)); w4.Owner[1] = HostileNpc; var sw4 = new ApEnemySwitch(); var p4b = ApPanel.Defaults;
        sw4.Step(w4, ApEnemyRule.CreativeOrAdmin, ref p4b.Enemy, ref p4b.EnemyPrevOwner);
        Sim.Check(sw4.GridIsEnemy && p4b.Enemy && p4b.EnemyPrevOwner == 0, "an encounter's enemy: read as an enemy, no owner before");
        Off(w4, sw4, ref p4b);
        Sim.Check(w4.Owner[1] == 1000 && w4.Owner[2] == 1000 && !p4b.Enemy, "switched off: the admin who did it owns the whole ship");
        // the owner changed between the ask and the step: checked again
        var w5 = new World { Creative = true }; var sw5 = new ApEnemySwitch(); var p5 = ApPanel.Defaults;
        sw5.Request(true, w5, ApEnemyRule.CreativeOrAdmin); w5.Owner[1] = 1003;
        sw5.Step(w5, ApEnemyRule.CreativeOrAdmin, ref p5.Enemy, ref p5.EnemyPrevOwner);
        Sim.Check(w5.Owner[1] == 1003 && !p5.Enemy && w5.Transfers == 0 && sw5.Text().Contains("only the ship's owner"), $"the owner changed before the step: refused - '{sw5.Text()}'");
        // the game refuses the transfer: said, the panel unchanged
        var w6 = new World { Creative = true, Fail = true }; var (sw6, p6) = On(w6);
        Sim.Check(!p6.Enemy && sw6.Text().Contains("refused the ownership change"), "the game refused the transfer: said, nothing saved");
        // no grid
        var w7 = new World { HasGrid = false }; var sw7 = new ApEnemySwitch();
        sw7.Request(true, w7, ApEnemyRule.Everyone);
        Sim.Check(sw7.Ask == 0 && sw7.Text().Contains("no ship"), "no grid: refused");
    }

    /// <summary>The scan's targets offline: candidates with their relation (the mocked game's), the pick every 2 s as in game.</summary>
    sealed class Picked : IApTargetSource
    {
        public readonly List<(SimTarget t, ApRelation rel)> All = new();
        public ApTargetFilter Filter; readonly ApTargetPicker _pk = new(16); SimTarget _cur; float _next; public float Time;
        public string Current => _cur?.Name;
        public bool Get(in ApSense s, out ApTargetFix f)
        {
            f = default;
            if (Time >= _next)
            {
                _next = Time + 2f; _pk.Clear();
                for (int i = 0; i < All.Count; i++) _pk.Add(All[i].t.P, All[i].rel, i);
                int k = _pk.Pick(s.Pos, 4000, Filter);
                _cur = k >= 0 ? All[k].t : null;
            }
            if (_cur == null) return false;
            f.Valid = true; f.Pos = _cur.P; f.Vel = _cur.V; f.Name = _cur.Name; f.Id = _cur.GetHashCode(); f.Radius = 6f;
            return true;
        }
    }

    /// <summary>One six-axis gunship of owner 'me' in Engage (standoff 150 m) among the targets (their owners), with a filter.</summary>
    static (Run r, Picked src, ApPilot p) Gunship(string name, long me, ApTargetFilter f, params (SimTarget t, long owner)[] targets)
    {
        var r = new Run(name, FlightShips.SixAxis());
        r.P.P = new Vector3D(0, 150, 0);
        r.Fly(0.5f);
        var src = new Picked { Filter = f };
        foreach (var (t, owner) in targets) { r.Targets.Add(t); src.All.Add((t, Rel(me, owner))); }
        var cfg = ApPanel.Defaults; cfg.Behaviour = ApBehaviour.Standoff; cfg.RangeM = 150f; cfg.AltOffsetM = 10f; cfg.HeightRef = ApHeightRef.Inertial; cfg.MinAglM = 0f; cfg.SpeedMs = 25f;
        cfg.TargetFilter = f; cfg.Enemy = me == HostileNpc;
        var p = FlightPack.Engage(r, cfg, ApMode.Engage, src);
        r.Each(rr => src.Time = rr.P.Time);
        return (r, src, p);
    }

    static SimTarget T(string name, Vector3D at, Vector3 v) { var t = SimTarget.Straight(at, v); t.Name = name; return t; }

    static void Chase()
    {
        // 1. THE ENEMY (the hostile NPC's): its wingman (the same owner) close by, the player 500 m off - it engages the player
        {
            var player = T("player", new Vector3D(500, 140, 120), new Vector3(3f, 0f, 1f));
            var wing = T("wingman", new Vector3D(60, 150, -60), Vector3.Zero);
            var (r, src, p) = Gunship("enemy six-axis", HostileNpc, ApTargetFilter.Hostile, (player, 1000), (wing, HostileNpc));
            int onPlayer = 0, other = 0; float wingMin = float.MaxValue;
            r.Each(rr => { if (src.Current == "player") onPlayer++; else if (src.Current != null) other++; wingMin = MathF.Min(wingMin, rr.Rel(wing).Range); });
            r.Fly(45f);
            float rng = r.Rel(player).Range;
            Sim.Note($"{r.Name}: the player (hostile to it) at 500 m, its wingman at 85 m - on the player {onPlayer} ticks, on anything else {other}; after 45 s the player at {rng:F0} m (standoff 150), the wingman never nearer than {wingMin:F0} m, {p.ActiveBh}");
            Sim.Check(other == 0 && onPlayer > 0 && MathF.Abs(rng - 150f) < 25f && wingMin > 20f && !r.P.Crashed,
                      $"{r.Name}: engages the player only ({other} ticks on others), standoff {rng:F0} m (150 +- 25), the wingman left alone ({wingMin:F0} m)");
            Program.LedgerPublic(r, false);
        }
        // 2. A PLAYER'S DRONE against the enemy, a nearer neutral NPC hauler and a faction mate's ship: hostile only - the enemy;
        //    + neutral - the nearer hauler; the faction mate never (friendly is only for Everything)
        foreach (var f in new[] { ApTargetFilter.Hostile, ApTargetFilter.HostileNeutral })
        {
            var enemy = T("raider", new Vector3D(520, 145, -100), new Vector3(-2f, 0f, 2f));
            var hauler = T("neutral hauler", new Vector3D(-230, 145, 120), new Vector3(0f, 0f, 1.5f));
            var mate = T("faction mate", new Vector3D(90, 150, 90), Vector3.Zero);
            var (r, src, p) = Gunship($"player drone ({ApRelations.FilterName(f)})", 1001, f, (enemy, HostileNpc), (hauler, NeutralNpc), (mate, 1002));
            string want = f == ApTargetFilter.Hostile ? "raider" : "neutral hauler";
            var tw = f == ApTargetFilter.Hostile ? enemy : hauler;
            int on = 0, other = 0;
            r.Each(rr => { if (src.Current == want) on++; else if (src.Current != null) other++; });
            r.Fly(45f);
            float rng = r.Rel(tw).Range;
            Sim.Note($"{r.Name}: the raider at 530 m (hostile), a neutral hauler at 260 m, a faction mate at 130 m - on the {want} {on} ticks, others {other}; after 45 s at {rng:F0} m");
            Sim.Check(other == 0 && on > 0 && MathF.Abs(rng - 150f) < 25f && !r.P.Crashed, $"{r.Name}: the {want} engaged ({other} ticks on others), {rng:F0} m (150 +- 25)");
            Program.LedgerPublic(r, false);
        }
    }
}
