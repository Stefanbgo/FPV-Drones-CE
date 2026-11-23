using System;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace FPVDrones;

[HarmonyPatch(typeof(JobGiver_UpdateLoadout), "FindPickup")]
internal static class ScoutLoadoutCharge
{
    // CE keeps ItemPriority private. Use its own enum values for the same search distances.
    private static readonly Type PriorityType = AccessTools.Inner(typeof(JobGiver_UpdateLoadout), "ItemPriority");

    private static bool Prefix(Pawn pawn, LoadoutSlot curSlot, int findCount, object[] __args)
    {
        if (curSlot.thingDef?.defName != "FPV_Scout" || pawn.Map == null) return true;
        var charged = GenClosest.ClosestThingReachable(pawn.Position, pawn.Map, ThingRequest.ForDef(curSlot.thingDef),
            PathEndMode.ClosestTouch, TraverseParms.For(pawn, Danger.Some), 80f,
            t => t.TryGetComp<CompDroneBattery>()?.Charge01 > 0f && !t.IsForbidden(pawn) &&
                pawn.CanReserve(t, 10, 1) && EquipmentUtility.CanEquip(t, pawn));
        if (charged == null) return true;
        string priority = pawn.Position.DistanceToSquared(charged.Position) <= 400 ? "Proximity" :
            findCount >= curSlot.count * 0.5f ? "LowStock" : "Low";
        __args[3] = Enum.Parse(PriorityType, priority);
        __args[4] = charged;
        __args[5] = null;
        return false;
    }
}
