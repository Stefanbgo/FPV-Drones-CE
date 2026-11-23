using System;
using Verse;

namespace FPVDrones;

internal static class DroneVariants
{
    internal static bool IsDroneItem(Thing thing) => IsItemName(thing?.def.defName);

    internal static PawnKindDef AirKindFor(Thing thing) => IsDroneItem(thing)
        ? DefDatabase<PawnKindDef>.GetNamed(thing.def.defName + "Air")
        : null;

    internal static ThingDef ItemFor(ThingDef def)
    {
        string name = ItemName(def);
        return IsItemName(name) ? DefDatabase<ThingDef>.GetNamed(name) : null;
    }

    internal static bool IsFiber(ThingDef def) => ItemName(def) is "FPV_KamikazeFiber" or "FPV_IncendiaryFiber";

    internal static bool IsIncendiary(ThingDef def) => ItemName(def) is "FPV_Incendiary" or "FPV_IncendiaryFiber";

    private static string ItemName(ThingDef def)
    {
        string name = def?.defName;
        return name != null && name.EndsWith("Air", StringComparison.Ordinal) ? name[..^3] : name;
    }

    private static bool IsItemName(string name) => name is "FPV_Kamikaze" or "FPV_KamikazeFiber" or
        "FPV_Incendiary" or "FPV_IncendiaryFiber" or "FPV_Scout";
}
