using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace FPVDrones;

public partial class FlyingDrone : Pawn
{
    internal Texture2D ControlIcon => DroneVariants.ItemFor(def).uiIcon;

    public override IEnumerable<Gizmo> GetGizmos()
    {
        if (Faction != Faction.OfPlayer) yield break;
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
            yield return new Command_DetonateDrone(this);
        }
    }

    public override string GetInspectString() =>
        "FPV_UI_ChargeLine".Translate(Charge01.ToStringPercent(), (int)Math.Ceiling(Charge01 * (IsScout ? 80 : 30))).ToString() + "\n" +
        (Returning ? "FPV_UI_Auto".Translate() : "FPV_UI_Manual".Translate()).ToString() +
        (AttackTarget != null ? "\n" + "FPV_UI_AttackTarget".Translate(AttackTarget.LabelShortCap).ToString() : "") +
        (RouteBlocked ? "\n" + "FPV_UI_BlockedRoute".Translate().ToString() : "") +
        (Operator != null ? "\n" + "FPV_UI_OperatorLine".Translate(Operator.LabelShortCap).ToString() : "");

    public override void DrawExtraSelectionOverlays()
    {
        if (Faction != Faction.OfPlayer) return;
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
        foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
            if (pawn is FlyingDrone { IsScout: false, FlightActive: true, ChargeTicks: <= 300 } drone &&
                drone.Faction == Faction.OfPlayer)
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
    private static ThingDef ProjectileDefFor(ThingDef droneDef)
    {
        if (DroneVariants.IsIncendiary(droneDef))
            return DefDatabase<ThingDef>.GetNamed("Proj_GrenadeMolotov");

        string ammoName = DroneVariants.IsHeat(droneDef) ? "Ammo_RPG7Grenade_HEAT" : "Ammo_RPG7Grenade_Frag";
        return ((AmmoDef)DefDatabase<ThingDef>.GetNamed(ammoName)).detonateProjectile;
    }

    // Keep the projectile's native impact path (including patched projectile subclasses
    // and comps). Reflection bridges CE's historical UnityEngine assembly reference
    // to the Unity-compatible vector type supplied by the 1.7 game runtime.
    private static readonly MethodInfo LaunchProjectile =
        AccessTools.Method(typeof(ProjectileCE), nameof(ProjectileCE.Launch),
            new[] { typeof(Thing), typeof(Vector2), typeof(float), typeof(float), typeof(float), typeof(float), typeof(Thing), typeof(float) });

    internal static void Detonate(FlyingDrone drone, IntVec3 cell, Thing contact = null)
    {
        var map = drone.Map;
        if (!DroneVariants.IsHeat(drone.def) || contact?.Spawned != true || contact.Map != map ||
            !contact.OccupiedRect().Contains(cell))
            contact = null;

        float height = 0.1f;
        float rotation = 0f;
        if (contact != null)
        {
            if (!DroneFlightOrders.IsMultiCellPawn(contact))
                height = Mathf.Max(height, new CollisionVertical(contact).HeightRange.Average);
            var direction = cell - drone.Position;
            rotation = direction == IntVec3.Zero ? -drone.Rotation.AsAngle : -direction.ToVector3().AngleFlat();
        }

        var projectile = (ProjectileCE)GenSpawn.Spawn(ProjectileDefFor(drone.def), cell, map);
        var position = cell.ToVector3Shifted();
        var origin = new Vector2(position.x, position.z);
        Thing launcher = drone.Operator ?? (Thing)drone;
        LaunchProjectile.Invoke(projectile, new object[] { launcher, origin, 0f, rotation, height, -1f, null, -1f });
        if (contact != null)
            projectile.intendedTarget = contact;

        var vehicleHandler = contact == null ? null : VehicleImpact.Register(contact, launcher, cell);
        try
        {
            projectile.Impact(contact);
        }
        finally
        {
            if (vehicleHandler != null)
                VehicleImpact.Deregister(vehicleHandler, launcher);
        }
    }

    internal static void DrawPreview(FlyingDrone drone)
    {
        if (!drone.Spawned || drone.Map != Find.CurrentMap) return;
        var cells = BlastCells(drone, drone.LastFree).ToList();
        GenDraw.DrawFieldEdges(cells, Color.red);
        foreach (var cell in cells)
        {
            var pawn = cell.GetFirstPawn(drone.Map);
            if (pawn != null && pawn != drone && pawn.Faction == Faction.OfPlayer)
                GenDraw.DrawTargetHighlight(pawn);
        }
    }

    internal static IEnumerable<IntVec3> BlastCells(FlyingDrone drone, IntVec3 cell) => BlastCells(drone.def, cell, drone.Map);

    internal static IEnumerable<IntVec3> BlastCells(ThingDef droneDef, IntVec3 cell, Map map)
    {
        if (map == null || !cell.InBounds(map))
            return Enumerable.Empty<IntVec3>();

        ThingDef projectile = ProjectileDefFor(droneDef);
        if (projectile.projectile is not { } props)
            return Enumerable.Empty<IntVec3>();
        var cells = props.damageDef.Worker.ExplosionCellsToHit(cell, map, props.explosionRadius);
        if (DroneVariants.IsHeat(droneDef) && projectile.GetCompProperties<CompProperties_ExplosiveCE>() is { } explosive)
            return cells.Concat(explosive.explosiveDamageType.Worker.ExplosionCellsToHit(cell, map, explosive.explosiveRadius)).Distinct();
        return cells;
    }

    private static class VehicleImpact
    {
        private static readonly FieldInfo Handler;
        private static readonly MethodInfo RegisterImpacter;
        private static readonly MethodInfo DeregisterImpacter;

        static VehicleImpact()
        {
            var vehicleType = AccessTools.TypeByName("Vehicles.VehiclePawn");
            if (vehicleType == null) return;
            Handler = AccessTools.Field(vehicleType, "statHandler");
            if (Handler == null) return;
            RegisterImpacter = AccessTools.Method(Handler.FieldType, "RegisterImpacter", new[] { typeof(Thing), typeof(IntVec3) });
            DeregisterImpacter = AccessTools.Method(Handler.FieldType, "DeregisterImpacter", new[] { typeof(Thing) });
        }

        internal static object Register(Thing target, Thing launcher, IntVec3 cell)
        {
            if (Handler?.DeclaringType.IsInstanceOfType(target) != true || RegisterImpacter == null || DeregisterImpacter == null)
                return null;
            object handler = Handler.GetValue(target);
            if (handler == null) return null;

            // Vehicle Framework uses the impact cell to resolve component damage.
            // CE still applies the hit and attributes it to the drone operator.
            RegisterImpacter.Invoke(handler, new object[] { launcher, cell });
            return handler;
        }

        internal static void Deregister(object handler, Thing launcher) =>
            DeregisterImpacter.Invoke(handler, new object[] { launcher });
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.ThreatDisabled))]
    private static class TargetAvailability
    {
        private static bool Prefix(Pawn __instance, ref bool __result)
        {
            if (__instance is not FlyingDrone drone) return true;
            __result = !drone.Spawned || !drone.FlightActive || drone.CrossingWall;
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
            if (target is not FlyingDrone { FlightActive: true, CrossingWall: false } drone)
                return;

            if (drone.IsScout)
            {
                // Airborne scouts have normal pawn priority even without an attack job.
                if (!drone.IsCombatant()) __result += 50f;
                return;
            }

            // CE keeps a bonus for its last target. A visible strike drone can compete
            // with that target at normal weapon range and takes priority up close.
            __result += 65f;
            if (drone.Position.DistanceToSquared(searcher.Thing.Position) <= 225)
                __result += 20f;
        }
    }
}





