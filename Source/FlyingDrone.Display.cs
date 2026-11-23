using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace FPVDrones;

public partial class FlyingDrone
{
    private readonly List<IntVec3> queuedRoutePreview = new();
    private bool routePreviewDirty = true;
    private int routePreviewTick = -1;

    internal bool RouteBlocked => !Returning && routeBlocked;
    internal bool IsMovingInFlight => segmentTo.IsValid;

    private void InvalidateRoutePreview() => routePreviewDirty = true;

    public void DrawFlightOverlays()
    {
        if (!FlightActive || Map != Find.CurrentMap || Faction != Faction.OfPlayer)
            return;

        var color = Returning ? SimpleColor.Cyan : AttackTarget != null ? SimpleColor.Red : SimpleColor.Green;
        var last = DrawPos;
        last.y = AltitudeLayer.MetaOverlays.AltitudeFor();
        if (segmentTo.IsValid)
            DrawPathCell(segmentTo, color, ref last);

        for (int index = pathIndex; index < path.Count; index++)
            DrawPathCell(path[index], color, ref last);

        if (Returning)
        {
            if (landingTarget.IsValid)
                GenDraw.DrawTargetHighlightWithLayer(landingTarget, AltitudeLayer.MetaOverlays);
            return;
        }

        RefreshQueuedRoutePreview();
        foreach (IntVec3 cell in queuedRoutePreview)
            DrawPathCell(cell, color, ref last);

        for (int index = 0; index < waypoints.Count; index++)
        {
            var marker = routeBlocked && index == 0 ? SimpleColor.Red : color;
            GenDraw.DrawCircleOutline(waypoints[index].ToVector3ShiftedWithAltitude(AltitudeLayer.MetaOverlays), 0.22f, marker);
        }
    }

    private static void DrawPathCell(IntVec3 cell, SimpleColor color, ref Vector3 last)
    {
        var next = cell.ToVector3ShiftedWithAltitude(AltitudeLayer.MetaOverlays);
        if ((next - last).sqrMagnitude > 0.0001f)
            GenDraw.DrawLineBetween(last, next, color, 0.075f);
        last = next;
    }

    private void RefreshQueuedRoutePreview()
    {
        int now = Find.TickManager.TicksGame;
        if (!routePreviewDirty && now < routePreviewTick + 30)
            return;

        routePreviewTick = now;
        routePreviewDirty = false;
        queuedRoutePreview.Clear();
        int firstUnplanned = path.Count > 0 ? 1 : 0;
        IntVec3 start = path.Count > 0 ? path[^1] : segmentTo.IsValid ? segmentTo : Position;
        for (int index = firstUnplanned; index < waypoints.Count; index++)
        {
            IntVec3 goal = waypoints[index];
            if (!TryFindPath(start, goal, out var leg, out _))
                break;
            queuedRoutePreview.AddRange(leg);
            start = goal;
        }
    }
}

public sealed class DroneDisplay : MapComponent
{
    private static readonly Vector3[] RotorMounts =
    {
        new(-0.26f, 0f, -0.25f), new(0.26f, 0f, -0.25f),
        new(-0.26f, 0f, 0.25f), new(0.26f, 0f, 0.25f)
    };

    private static Material rotorMaterial;
    private static Material shadowMaterial;

    public DroneDisplay(Map map) : base(map) { }

    public override void MapComponentDraw()
    {
        if (Find.CurrentMap != map)
            return;

        int tick = Find.TickManager.TicksGame;
        foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
        {
            if (pawn is not FlyingDrone { FlightActive: true } drone || !Visible(drone))
                continue;

            DrawRotors(drone, tick);
            if (drone.Faction == Faction.OfPlayer && Find.Selector.IsSelected(drone))
            {
                Vector3 outline = drone.DrawPos;
                outline.y = AltitudeLayer.MetaOverlays.AltitudeFor();
                GenDraw.DrawCircleOutline(outline, 0.53f, SimpleColor.Cyan);
            }
        }
    }

    public override void MapComponentOnGUI()
    {
        if (Find.CurrentMap != map)
            return;

        foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
        {
            if (pawn is not FlyingDrone { FlightActive: true } drone || drone.Faction != Faction.OfPlayer || !Visible(drone))
                continue;

            Vector2 position = GenMapUI.LabelDrawPosFor(drone, 0.6f);
            var badge = new Rect(position.x - 11f, position.y - 21f, 22f, 22f);
            Color outline = Find.Selector.IsSelected(drone) ? new Color(0.40f, 0.95f, 1f) : new Color(0.45f, 0.80f, 0.56f);
            Widgets.DrawBoxSolid(badge, outline);
            Widgets.DrawBoxSolid(badge.ContractedBy(1f), new Color(0.09f, 0.14f, 0.16f, 0.94f));
            if (Widgets.ButtonImage(badge.ContractedBy(3f), drone.ControlIcon))
                DroneGroups.SelectFromBadge(drone);
            TooltipHandler.TipRegion(badge, drone.LabelCap.ToString());
        }
    }

    private bool Visible(FlyingDrone drone) =>
        !drone.Position.Fogged(map) && Find.CameraDriver.CurrentViewRect.Contains(drone.Position);

    private static void DrawRotors(FlyingDrone drone, int tick)
    {
        rotorMaterial ??= MaterialPool.MatFrom("FPVDrones/RotorBlade", ShaderDatabase.Transparent);
        shadowMaterial ??= MaterialPool.MatFrom("FPVDrones/DroneShadow", ShaderDatabase.Transparent);
        Vector3 centre = drone.DrawPos;
        var shadow = centre + new Vector3(0.08f, 0f, -0.09f);
        shadow.y = AltitudeLayer.Shadows.AltitudeFor();
        var shadowTransform = Matrix4x4.TRS(shadow, Quaternion.identity, new Vector3(0.82f, 1f, 0.74f));
        Graphics.DrawMesh(MeshPool.plane10, shadowTransform, shadowMaterial, 0);
        centre.y += 0.035f;
        Quaternion bodyRotation = Quaternion.Euler(0f, drone.Rotation.AsAngle, 0f);
        for (int index = 0; index < RotorMounts.Length; index++)
        {
            Vector3 hub = centre + bodyRotation * RotorMounts[index];
            float angle = (tick % 180) * (index % 2 == 0 ? 68f : -68f) + index * 43f;
            Quaternion rotation = bodyRotation * Quaternion.Euler(drone.IsMovingInFlight ? 12f : 0f, angle, 0f);
            var transform = Matrix4x4.TRS(hub, rotation, new Vector3(0.28f, 1f, 0.28f));
            Graphics.DrawMesh(MeshPool.plane10, transform, rotorMaterial, 0);
        }
    }
}
