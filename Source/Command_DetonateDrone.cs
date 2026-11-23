using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace FPVDrones;

internal sealed class Command_DetonateDrone : Command_Action
{
    internal Command_DetonateDrone(FlyingDrone drone)
    {
        defaultLabel = "FPV_UI_End".Translate();
        defaultDesc = (DroneVariants.IsHeat(drone.def) ? "FPV_UI_EndHeatDesc" :
            DroneVariants.IsIncendiary(drone.def) ? "FPV_UI_EndFireDesc" : "FPV_UI_EndDesc").Translate();
        icon = drone.ControlIcon;
        hotKey = DefDatabase<KeyBindingDef>.GetNamed("FPV_DetonateSelected");
        action = () =>
        {
            if (DroneFlightOrders.IsPlayerFlight(drone)) drone.FinishFlight(false);
        };
        onHover = () =>
        {
            foreach (var selected in Find.Selector.SelectedObjects.OfType<FlyingDrone>())
                if (!selected.IsScout && DroneFlightOrders.IsPlayerFlight(selected)) DroneControl.DrawBlast(selected);
        };
    }

    public override bool GroupsWith(Gizmo other) => groupable && other is Command_DetonateDrone { groupable: true };

    protected override GizmoResult GizmoOnGUIInt(Rect rect, GizmoRenderParms parms)
    {
        if (Event.current?.type != EventType.KeyDown ||
            GUIUtility.keyboardControl == 0 && !Find.WindowStack.AnyWindowAbsorbingAllInput &&
            !Find.Targeter.IsTargeting && Find.DesignatorManager.SelectedDesignator == null)
            return base.GizmoOnGUIInt(rect, parms);

        var binding = hotKey;
        hotKey = null;
        try { return base.GizmoOnGUIInt(rect, parms); }
        finally { hotKey = binding; }
    }
}
