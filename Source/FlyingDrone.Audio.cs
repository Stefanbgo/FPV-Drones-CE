using UnityEngine;
using Verse;
using Verse.Sound;

namespace FPVDrones;

public partial class FlyingDrone
{
    private Sustainer rotorSustainer;
    private float rotorThrottle;

    public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
    {
        StopRotorSound();
        base.DeSpawn(mode);
    }

    private void UpdateRotorSound(float distanceTraveled)
    {
        if (!FlightActive || !Spawned || Map != Find.CurrentMap || Position.Fogged(Map))
        {
            StopRotorSound();
            return;
        }

        float throttle = Mathf.Clamp01(distanceTraveled / CellDistancePerTick);
        rotorThrottle = Mathf.MoveTowards(rotorThrottle, throttle, 1f / 18f);
        float pitch = 1f + rotorThrottle * (IsScout ? 0.16f : 0.22f);

        if (rotorSustainer == null || rotorSustainer.Ended)
        {
            // Loops are runtime-only. The first flight tick after loading creates a new one.
            SoundInfo info = SoundInfo.InMap(this, MaintenanceType.PerTick);
            info.pitchFactor = pitch;
            SoundDef rotorSound = DefDatabase<SoundDef>.GetNamed(IsScout ? "FPV_ScoutRotor" : "FPV_StrikeRotor");
            rotorSustainer = rotorSound.TrySpawnSustainer(info);
        }
        else
        {
            rotorSustainer.info.pitchFactor = pitch;
            rotorSustainer.Maintain();
        }
    }

    private void StopRotorSound()
    {
        if (rotorSustainer != null && !rotorSustainer.Ended)
        {
            rotorSustainer.End();
        }

        rotorSustainer = null;
        rotorThrottle = 0f;
    }
}
