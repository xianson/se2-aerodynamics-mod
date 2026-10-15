// GENERATED from the mod's sources (the fields the settings registry reads and writes), so the registry compiles
// offline. Program.Honest() checks every member against the real declaration (type and initializer) - a changed default
// in the mod fails the suite until this file is updated to match.
namespace AeroMod;

public static class AeroSwitch
{
    public static bool Enabled = true;   // Runtime/AeroConfig.cs
    public static int Generation;   // Runtime/AeroConfig.cs
}
public static class AeroTuning
{
    public static float DragMultiplier = 1f;   // Runtime/AeroConfig.cs
    public static float LiftMultiplier = 1f;   // Runtime/AeroConfig.cs
}
public static class ThrustTorque
{
    public static bool Enabled = true;   // Propulsion/ThrustTorque.cs
    public static float MaxRate = 0.8f;   // Propulsion/ThrustTorque.cs
    public static bool FlamesEnabled = true;   // Propulsion/ThrustTorque.cs
}
public static class AeroWind
{
    public static bool Enabled = true;   // Air/AeroWind.cs
}
public static class HingeSurfaces
{
    public static int Mode;   // Blocks/HingeSurfaces.cs
    public static float MaxDeflection = 20f;   // Blocks/HingeSurfaces.cs
}
public static class CompressibleWingModel
{
    public static bool GroundEffect = true;   // Lift/CompressibleWingModel.cs
}
public static class AtmosphereTail
{
    public static bool Enabled = true;   // Atmosphere/AtmosphereTail.cs
}
public static class StructureLoads
{
    public static bool Enabled = true;   // Structure/Server/StructureLoads.cs
    public static bool Damage = true;   // Structure/Server/StructureLoads.cs
    public static bool HeatDamage = true;   // Structure/Server/StructureLoads.cs
    public static bool SteerRespectsStructure = true;   // Structure/Server/StructureLoads.cs
    public static float Rate = 4f;   // Structure/Server/StructureLoads.cs
    public static float BreakUtil { get => StructureEval.BreakUtil; set => StructureEval.BreakUtil = value; }   // Structure/Server/StructureLoads.cs
    public static float WearPerSecond { get => StructureEval.WearPerSecond; set => StructureEval.WearPerSecond = value; }   // Structure/Server/StructureLoads.cs
}
public static class StructureTear
{
    public static bool Enabled = true;   // Structure/Server/StructureTear.cs
    public static float TearSeconds = 1.2f;   // Structure/Server/StructureTear.cs
    public static float MinMass = 2000f;   // Structure/Server/StructureTear.cs
    public static float ForceFactor = 6f;   // Structure/Server/StructureTear.cs
    public static int MaxAnchors = 6;   // Structure/Server/StructureTear.cs
    public static float PeelFirst = 0.35f;   // Structure/Server/StructureTear.cs
}
public static class AeroGridComponent
{
    public static bool LocalUpdates = true;   // Grid/AeroGridComponent.cs
    public static int ChunksPerUpdate = 8;   // Grid/AeroGridComponent.cs
    public static float MinSpeedForUpdates = 10f;   // Grid/AeroGridComponent.cs
    public static double QuietBeforeFullRebuild = 5.0;   // Grid/AeroGridComponent.cs
    public static bool SplitBlocks = true;   // Structure/Server/StructureServer.Failure.cs
    public static bool Dents = true;   // Structure/Server/StructureServer.Deform.cs
    public static int CrackSmokes = 3;   // Structure/Server/StructureServer.Failure.cs
    public static float CrumpleDepth = 0.15f;   // Structure/Server/StructureServer.Failure.cs
    public static float GearContactShare = 1f;   // Structure/Server/StructureContacts.cs
}
public static class StructureContactLoads
{
    public static bool Enabled = true;   // Structure/Server/StructureContacts.cs
}
public static class WingStructure
{
    public static bool Enabled = false;   // Structure/Server/WingStrainServer.cs
    public static float Strength = 25f;   // Structure/Server/WingStrainServer.cs
}
public static class HeatScorch
{
    public static bool Enabled = true;   // Structure/Fx/HeatScorch.cs
    public static int MaxSpots = 4;   // Structure/Fx/HeatScorch.cs
    public static bool Visible = true;   // Structure/Fx/HeatScorch.cs
}
public static class AeroEntryFx
{
    public static float OnsetMach = 2f;   // Fx/AeroEntryFx.cs
    public static float FullHeat = 0.59f;   // Fx/AeroEntryFx.cs
    public static float OnsetHeat = 0.3f;   // Fx/AeroEntryFx.cs
    public static float HeatLag = 2f;   // Fx/AeroEntryFx.cs
    public static bool AllEffects = true;   // Fx/AeroEntryFx.cs
}
public static class AeroFrameBudget
{
    public static double BudgetMs = 1.0;   // Grid/AeroFrameBudget.cs
}
public static class AeroWork
{
    public static int Workers = 2;   // Grid/AeroWork.cs
}
public static class AeroTableCache
{
    public static bool Enabled = true;   // Drag/AeroTableCache.cs
    public static long MaxBytes = 256L << 20;   // Drag/AeroTableCache.cs
}
public static class StallWarning
{
    public static bool Enabled = true;   // Hud/StallWarning.cs
}
public static class LoacWarning
{
    public static bool Enabled = true;   // Hud/Loac.cs
}
public static class MachHud
{
    public static bool On = false;   // Hud/MachHud.cs
}
public static class GameStallWarning
{
    public static bool On = false;   // Hud/GameStallWarning.cs
    public static void Apply() { }
}
public static class StructureFeel
{
    public static bool Creaks = true;   // Structure/Fx/StructureFeel.cs
    public static float Volume = 1f;   // Structure/Fx/StructureFeel.cs
    public static bool Shake = true;   // Structure/Fx/StructureFeel.cs
    public static float ShakeScale = 1f;   // Structure/Fx/StructureFeel.cs
    public static float CreakFrom = 0.6f;   // Structure/Fx/StructureFeel.cs
}
public static class FlightField
{
    public static bool On = true;   // Fx/FlightField.Host.cs
    public static bool PlasmaOn = true;   // Fx/FlightField.Host.cs
    public static bool VapourOn = true;   // Fx/FlightField.Host.cs
    public static bool TipsOn = true;   // Fx/FlightField.Host.cs
    public static bool SpotOn = true;   // Fx/FlightField.Host.cs
    public static int FlightMax = 4;   // Fx/FlightField.Host.cs
    public static float FlightRange = 4000f;   // Fx/FlightField.Host.cs
    public static int SpawnPerTick = 48;   // Fx/FlightField.Spawn.cs
    public static bool ConeOn;   // Fx/FlightField.Host.cs
    public static float Bright = 1f;   // Fx/FlightField.cs
}
public static class AeroDebrisDrag
{
    public static bool Enabled = true;   // Debris/AeroDebrisDrag.cs
    public static float DragScale = 0.4f;   // Debris/AeroDebrisDrag.cs
}

public static class TearSmoke
{
    public static bool Enabled = true;   // Structure/Fx/TearSmoke.cs
    public static float Seconds = 4f;   // Structure/Fx/TearSmoke.cs
    public static float DebrisScale = 1f;   // Structure/Fx/TearSmoke.cs
}
public static class AeroSurfaceSaved
{
    public static void Load(string text) { }
    public static void Clear() { }
    public static string Text() => "";
}
