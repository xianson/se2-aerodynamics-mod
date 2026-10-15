#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Linq;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.BlockDetails;
using Keen.VRage.Library.Serialization;

namespace AeroMod;

/// <summary>
/// DEV (harness: aeroset AeroPanelProbe.Gen n): opens a control surface's control panel the way the terminal does -
/// from a client surface block: the grid's ControlPanelListModel (StreamedControlPanelSessionComponent.Attach(entity)),
/// then that block's detail models (Attach(types, [id], list) - where a mod's model used to kill the game, the
/// replication lookup), then each model's members as the terminal's inspector lists them (ObjectManipulator), and a
/// slider set through the model reaching the block's settings. [AERO-PANEL] lines, PASS / FAIL.
/// </summary>
public static class AeroPanelProbe
{
    public static int Gen;
    static int _ran;
    public static string Last = "not run";

    /// <summary>Client: from a surface block's flap job (once per Gen).</summary>
    internal static void Tick(Entity clientBlock)
    {
        if (_pending != null) { Poll(); return; }
        if (Gen == _ran || clientBlock == null) return;
        _ran = Gen;
        Run(clientBlock);
    }

    static void Say(string s) { Last = s; Log.Default?.Info("[AERO-PANEL] " + s); }

    static async void Run(Entity block)
    {
        try
        {
            var cp = block.GetSession()?.SessionComponents.TryGet<StreamedControlPanelSessionComponent>();
            if (cp == null) { Say("FAIL no control panel session component"); return; }
            var list = await cp.Attach(block);
            if (list == null) { Say("FAIL no list model"); return; }
            // (EntityId and the list's ObservableList are in assemblies a mod cannot reference: read by reflection)
            const System.Reflection.BindingFlags inst = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public;
            var blocks = list.GetType().GetProperty("Blocks", inst)?.GetValue(list) as System.Collections.IEnumerable;
            if (blocks == null) { Say("FAIL list has no Blocks"); cp.Detach(list); return; }
            var mine = SubclassOf<BlockDetailModel>.From<AeroSurfaceBlockDetailModel>();
            ControlPanelEntityModel entry = null; int count = 0;
            foreach (var o in blocks)
            {
                count++;
                if (entry == null && o is ControlPanelEntityModel b && b.DetailViewTypes != null && b.DetailViewTypes.Contains(mine)) entry = b;
            }
            Say($"list: {count} blocks");
            if (entry == null) { Say("FAIL no block in the list offers AeroSurfaceBlockDetailModel"); cp.Detach(list); return; }
            object id = typeof(ControlPanelEntityModel).GetField("EntityId", inst).GetValue(entry);
            Say($"block {id}: {entry.DetailViewTypes.Count} detail types ({string.Join(", ", entry.DetailViewTypes.Select(t => t.Type.Name))})");
            var ids = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(id.GetType()));
            ids.Add(id);
            var attach = typeof(StreamedControlPanelSessionComponent).GetMethods(inst)
                .First(m => m.Name == "Attach" && m.GetParameters().Length == 3);
            // (the game's own Task<Dictionary<EntityId, ...>> - a struct of VRage.Library's: kept boxed and polled each
            //  frame from the flap job, its result read through its awaiter)
            _pending = attach.Invoke(cp, new object[] { entry.DetailViewTypes, ids, list });
            _cp = cp; _list = list; _id = id; _block = block; _since = Environment.TickCount64;
        }
        catch (Exception e) { Say($"FAIL {e.GetType().Name}: {e.Message} @ {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
    }

    static object _pending, _id; static StreamedControlPanelSessionComponent _cp; static ControlPanelListModel _list;
    static Entity _block; static long _since;

    static void Poll()
    {
        try
        {
            const System.Reflection.BindingFlags inst = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public;
            if (!(bool)_pending.GetType().GetProperty("IsCompleted", inst).GetValue(_pending))
            {
                if (Environment.TickCount64 - _since > 15000) { Say("FAIL the detail models never came back (15 s)"); Done(); }
                return;
            }
            var awaiter = _pending.GetType().GetMethod("GetAwaiter", inst, null, Type.EmptyTypes, null).Invoke(_pending, null);
            var dict = awaiter.GetType().GetMethod("GetResult", inst, null, Type.EmptyTypes, null).Invoke(awaiter, null) as System.Collections.IDictionary;
            if (dict == null || !dict.Contains(_id) || !(dict[_id] is List<BlockDetailModel> got)) { Say("FAIL no detail models came back"); Done(); return; }
            Finish(got);
        }
        catch (Exception e) { Say($"FAIL {e.GetType().Name}: {e.GetBaseException().Message}"); }
        Done();
    }

    static void Done()
    {
        try { _cp?.Detach(_list); } catch { }
        _pending = null; _cp = null; _list = null; _id = null; _block = null;
    }

    static void Finish(List<BlockDetailModel> got)
    {
        var block = _block;
        bool ok = false;
        foreach (var m in got)
        {
            object mo = m;   // (as IAccessible through object: its INotifyPropertyChanged base is not a mod reference)
            var names = new ObjectManipulator((IAccessible)mo, trackChanges: false).Members.Select(x => x.Name).ToList();
            var mode = (BlockDetailDisplayModeAttribute)Attribute.GetCustomAttribute(m.GetType(), typeof(BlockDetailDisplayModeAttribute), true);
            Say($"model {m.GetType().Name}: display {(mode == null ? "-" : mode.DisplayMode.ToString())}, members [{string.Join(", ", names)}]");
            if (m is AeroSurfaceBlockDetailModel s)
            {
                bool listed = names.Contains("Pitch") && names.Contains("MaxDeflection") && names.Contains("Trim");
                float was = s.MaxDeflection;
                s.MaxDeflection = 27f;
                var settings = AllSettingsNear(block);
                bool reached = settings != null && Math.Abs(settings.MaxDeflection - 27f) < 1e-3f;
                s.MaxDeflection = was;
                Say($"surface model: members listed {listed}, slider reaches the block {reached} ({settings?.Describe() ?? "no settings"})");
                ok = listed && reached && mode != null;
            }
        }
        Say(ok ? "PASS control surface panel" : "FAIL control surface panel");
    }

    /// <summary>The server settings of the block nearest this client block (in process: the same world).</summary>
    static AeroSurfaceSettingsComponent AllSettingsNear(Entity clientBlock)
    {
        var at = clientBlock.Data.GetWorldTransform().Position; AeroSurfaceSettingsComponent best = null; double bd = 2.0;
        lock (AeroSurfaceSettingsComponent.All)
            foreach (var s in AeroSurfaceSettingsComponent.All)
            {
                if (s.Entity == null) continue;
                double d = (s.Entity.Data.GetWorldTransform().Position - at).Length();
                if (d < bd) { bd = d; best = s; }
            }
        return best;
    }
}
