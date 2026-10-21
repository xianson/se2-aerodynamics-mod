#pragma warning disable
using AeroMod;

/// <summary>The suspension's stiffness cap (Gear/GearStiffness.cs): every gear's slider x StiffnessScale never past an effective
/// tau of 1.67; below the cap unchanged.</summary>
static class StiffnessTests
{
    static int _checks, _fails;
    static void Check(bool ok, string what) { _checks++; if (!ok) { _fails++; if (_fails <= 20) Console.WriteLine("FAIL stiffness: " + what); } }
    public static (int checks, int fails) Run()
    {
        Check(GearStiffness.MaxStrength * GearStiffness.SolverTau <= GearStiffness.MaxEffectiveTau + 1e-5f && GearStiffness.MaxEffectiveTau <= 1.67f, "cap above 1.67 effective");
        Check(GearStiffness.Strength(1f, 3f) == GearStiffness.MaxStrength, "the slider's top x3 not capped: " + GearStiffness.Strength(1f, 3f));
        Check(GearStiffness.Strength(0.5f, 3f) == 1.5f && GearStiffness.Strength(0f, 3f) == 0f && GearStiffness.Strength(-1f, 3f) == 0f, "below the cap changed");
        for (float sl = 0f; sl <= 1f; sl += 0.05f) Check(GearStiffness.Strength(sl, 3f) * GearStiffness.SolverTau <= GearStiffness.MaxEffectiveTau + 1e-5f, "over the cap at " + sl);
        Check(MathF.Abs(GearStiffness.Sag(1000f, GearStiffness.SpringRate(1f, 100f, 1f / 60f)) - 1000f * 9.81f / (0.6f * 100f * 3600f)) < 1e-5f, "sag formula");
        return (_checks, _fails);
    }
}
