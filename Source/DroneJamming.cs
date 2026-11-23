using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace FPVDrones;

public sealed class DroneJamming : MapComponent
{
    public const float Radius = 30f;
    public const string BackpackDefName = "FPV_JammerBackpack";

    private readonly List<Pawn> wearers = new();
    private int lastScan = -1;

    public DroneJamming(Map map) : base(map) { }

    public static bool IsJammed(FlyingDrone drone)
    {
        return drone.Spawned && drone.Faction != null && !DroneVariants.IsFiber(drone.def) &&
            drone.Map.GetComponent<DroneJamming>().ContainsHostileWearer(drone);
    }

    private bool ContainsHostileWearer(FlyingDrone drone)
    {
        int now = Find.TickManager.TicksGame;
        if (now != lastScan)
        {
            lastScan = now;
            wearers.Clear();
            foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
                if (IsActiveWearer(pawn)) wearers.Add(pawn);
        }

        Vector3 droneCenter = drone.DrawPos;
        foreach (Pawn wearer in wearers)
        {
            if (wearer.Map != map || !IsActiveWearer(wearer) || !wearer.Faction.HostileTo(drone.Faction))
                continue;
            float x = wearer.Position.x + 0.5f - droneCenter.x;
            float z = wearer.Position.z + 0.5f - droneCenter.z;
            if (x * x + z * z <= Radius * Radius) return true;
        }

        return false;
    }

    private static bool IsActiveWearer(Pawn pawn)
    {
        return pawn.Spawned && pawn.Faction != null && !pawn.DeadOrDowned && pawn.Awake() &&
            HasBackpack(pawn);
    }

    private static bool HasBackpack(Pawn pawn)
    {
        if (pawn.apparel == null) return false;
        foreach (Apparel apparel in pawn.apparel.WornApparel)
            if (apparel.def.defName == BackpackDefName) return true;
        return false;
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.DrawExtraSelectionOverlays))]
    private static class DrawBackpackRange
    {
        private static void Postfix(Pawn __instance)
        {
            if (IsActiveWearer(__instance) && __instance.Map == Find.CurrentMap)
                GenDraw.DrawRadiusRing(__instance.Position, Radius);
        }
    }
}
