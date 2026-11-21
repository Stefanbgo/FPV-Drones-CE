using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace FPVDrones;

public partial class FlyingDrone : Pawn
{
    internal Texture2D ControlIcon => DefDatabase<ThingDef>.GetNamed(IsScout ? "FPV_Scout" : "FPV_Kamikaze").uiIcon;

    public override IEnumerable<Gizmo> GetGizmos()
    {
        if (IsScout)
        {
            if (Returning)
                yield return DroneControl.Command("FPV_UI_Reacquire".Translate(), "FPV_UI_ReacquireDesc".Translate(),
                    Reacquire, ControlIcon);
            else
            {
                yield return DroneControl.Command("FPV_UI_Return".Translate(), "FPV_UI_ReturnDesc".Translate(),
                    ReturnHome, ControlIcon);
                yield return DroneControl.Command("FPV_UI_Land".Translate(), "FPV_UI_LandDesc".Translate(),
                    () => FinishFlight(false), ControlIcon);
            }
        }
        else
        {
            var end = DroneControl.Command("FPV_UI_End".Translate(),
                "FPV_UI_EndDesc".Translate(),
                () => FinishFlight(false), ControlIcon);
            end.onHover = () => DroneControl.DrawBlast(this);
            yield return end;
        }
    }

    public override string GetInspectString() =>
        "FPV_UI_ChargeLine".Translate(Charge01.ToStringPercent(), (int)Math.Ceiling(Charge01 * (IsScout ? 80 : 30))).ToString() + "\n" +
        (Returning ? "FPV_UI_Auto".Translate() : "FPV_UI_Manual".Translate()).ToString() +
        (Operator != null ? "\n" + "FPV_UI_OperatorLine".Translate(Operator.LabelShortCap).ToString() : "");

    public override void DrawExtraSelectionOverlays()
    {
        if (!Returning) DroneControl.DrawRange(HomeAnchor, MaxRange, Map);
        DrawFlightOverlays();
    }

    public override void PreApplyDamage(ref DamageInfo dinfo, out bool absorbed)
    {
        absorbed = true;
        if (Spawned && !CrossingWall && dinfo.Amount > 0 && dinfo.Def.ExternalViolenceFor(this))
            FinishFlight(true);
    }

    public override void Kill(DamageInfo? dinfo, Hediff exactCulprit = null)
    {
        if (Spawned) FinishFlight(true);
        else base.Kill(dinfo, exactCulprit);
    }
}

public sealed class FlightWarnings : MapComponent
{
    public FlightWarnings(Map map) : base(map) { }

    private IEnumerable<FlyingDrone> ExpiringDrones()
    {
        if (map != Find.CurrentMap) yield break;
        var kind = DefDatabase<ThingDef>.GetNamed("FPV_KamikazeAir");
        foreach (var thing in map.listerThings.ThingsOfDef(kind))
            if (thing is FlyingDrone { FlightActive: true, ChargeTicks: <= 300 } drone)
                yield return drone;
    }

    public override void MapComponentDraw()
    {
        foreach (var drone in ExpiringDrones()) DroneControl.DrawBlast(drone);
    }

    public override void MapComponentOnGUI()
    {
        foreach (var drone in ExpiringDrones())
            GenMapUI.DrawThingLabel(drone, "FPV_UI_Seconds".Translate((drone.ChargeTicks + 59) / 60), Color.red);
    }
}

internal static class DronePayload
{
    private static ThingDef ProjectileDef =>
        (DefDatabase<ThingDef>.GetNamed("Ammo_RPG7Grenade_Frag") as AmmoDef)?.detonateProjectile;

    // Keep the projectile's native impact path (including patched projectile subclasses
    // and comps). Reflection bridges CE's historical UnityEngine assembly reference
    // to the Unity-compatible vector type supplied by the 1.7 game runtime.
    private static readonly System.Reflection.MethodInfo LaunchProjectile =
        AccessTools.Method(typeof(ProjectileCE), nameof(ProjectileCE.Launch),
            new[] { typeof(Thing), typeof(Vector2), typeof(float), typeof(float), typeof(float), typeof(float), typeof(Thing), typeof(float) });

    internal static void Detonate(FlyingDrone drone, IntVec3 cell)
    {
        var map = drone.Map;
        var projectile = (ProjectileCE)GenSpawn.Spawn(ProjectileDef, cell, map);
        var position = cell.ToVector3Shifted();
        var origin = new Vector2(position.x, position.z);
        LaunchProjectile.Invoke(projectile, new object[] { drone.Operator ?? (Thing)drone, origin, 0f, 0f, 0.1f, -1f, null, -1f });
        projectile.Impact(null);
    }

    internal static void DrawPreview(FlyingDrone drone)
    {
        if (!drone.Spawned || drone.Map != Find.CurrentMap || ProjectileDef?.projectile is not { } props) return;
        var cells = props.damageDef.Worker.ExplosionCellsToHit(drone.LastFree, drone.Map, props.explosionRadius).ToList();
        GenDraw.DrawFieldEdges(cells, Color.red);
        foreach (var cell in cells)
        {
            var pawn = cell.GetFirstPawn(drone.Map);
            if (pawn != null && pawn != drone && pawn.Faction == Faction.OfPlayer)
                GenDraw.DrawTargetHighlight(pawn);
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.ThreatDisabled))]
    private static class TargetAvailability
    {
        private static bool Prefix(Pawn __instance, ref bool __result)
        {
            if (__instance is not FlyingDrone drone) return true;
            __result = !drone.Spawned || drone.CrossingWall;
            return false;
        }
    }

    [HarmonyPatch(typeof(ProjectileCE), "TryCollideWith")]
    private static class WallTransit
    {
        private static bool Prefix(Thing thing, ref bool __result)
        {
            if (thing is not FlyingDrone { CrossingWall: true }) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch]
    private static class DroneTargetScore
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(AttackTargetFinder), "GetShootingTargetScore");
            yield return AccessTools.Method(typeof(NonSnapAttackTargetFinder), "GetShootingTargetScore");
        }
        private static void Postfix(IAttackTarget target, IAttackTargetSearcher searcher, ref float __result)
        {
            if (target is FlyingDrone { IsScout: false, CrossingWall: false } drone &&
                drone.Position.DistanceToSquared(searcher.Thing.Position) <= 144)
                __result += 85f;
        }
    }
}





