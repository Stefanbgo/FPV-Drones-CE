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
    private Thing boundController;

    internal void BindKit(Pawn pawn)
    {
        Thing next = DroneControl.EquippedController(pawn);
        if (boundController != next && boundController?.TryGetComp<CompKitBinding>() is { } old && old.Flight == this)
            old.Flight = null;
        boundController = next;
        if (next != null) next.TryGetComp<CompKitBinding>().Flight = this;
    }

    private bool HasBoundKit()
    {
        // A flight saved by an earlier build acquires its existing operator's gear on first tick.
        if (boundController == null) BindKit(Operator);
        return boundController != null && boundController == DroneControl.EquippedController(Operator);
    }
}
