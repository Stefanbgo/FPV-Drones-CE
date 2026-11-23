using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace FPVDrones;

[HarmonyPatch]
internal static class SidearmsPilotProtection
{
    private const string Assignment = "PeteTimesSix.SimpleSidearms.Utilities.WeaponAssingment";

    private static bool Prepare() => AccessTools.TypeByName(Assignment) != null;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        var type = AccessTools.TypeByName(Assignment);
        if (type == null) yield break;
        foreach (var name in new[] { "equipSpecificWeaponFromInventory", "equipSpecificWeapon" })
        {
            var method = AccessTools.DeclaredMethod(type, name);
            if (method != null) yield return method;
        }
    }

    private static bool Prefix(Pawn pawn) => !DroneControl.ManualPilot(pawn);
}
