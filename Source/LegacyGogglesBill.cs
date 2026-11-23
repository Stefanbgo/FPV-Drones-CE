using HarmonyLib;
using RimWorld;
using Verse;

namespace FPVDrones;

[HarmonyPatch(typeof(Bill), nameof(Bill.ExposeData))]
internal static class LegacyGogglesBill
{
    private static void Postfix(Bill __instance)
    {
        if (Scribe.mode == LoadSaveMode.PostLoadInit && __instance.recipe?.defName == "FPV_MakeGoggles")
            __instance.suspended = true;
    }
}
