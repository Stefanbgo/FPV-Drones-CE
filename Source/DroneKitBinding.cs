using Verse;

namespace FPVDrones;

public sealed class CompProperties_KitBinding : CompProperties
{
    public CompProperties_KitBinding() => compClass = typeof(CompKitBinding);
}

public sealed class CompKitBinding : ThingComp
{
    internal FlyingDrone Flight;

    public bool AvailableTo(Pawn pawn) => Flight?.Spawned != true || Flight.Operator == pawn;

    public override void PostExposeData()
    {
        base.PostExposeData();
        if (Scribe.mode == LoadSaveMode.Saving && Flight?.Spawned != true) Flight = null;
        Scribe_References.Look(ref Flight, "fpvBoundFlight");
    }
}

public partial class FlyingDrone
{
    private Thing boundGoggles;
    private Thing boundController;

    internal void BindKit(Pawn pawn)
    {
        ReplaceItem(ref boundGoggles, DroneControl.AvailableGear(pawn, "FPV_Goggles"));
        ReplaceItem(ref boundController, DroneControl.AvailableGear(pawn, "FPV_Controller"));
    }

    private void ReplaceItem(ref Thing previous, Thing next)
    {
        if (previous != next && previous?.TryGetComp<CompKitBinding>() is { } old && old.Flight == this)
            old.Flight = null;
        previous = next;
        if (next != null) next.TryGetComp<CompKitBinding>().Flight = this;
    }

    private bool HasBoundKit()
    {
        // A flight saved by an earlier build acquires its existing operator's gear on first tick.
        if (boundGoggles == null && boundController == null) BindKit(Operator);
        var items = Operator?.inventory?.innerContainer;
        return items != null && boundGoggles != null && boundController != null &&
            items.Contains(boundGoggles) && items.Contains(boundController);
    }
}
