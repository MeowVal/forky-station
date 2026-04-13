using Content.Server._Funkystation.SM.EntitySystems;
using Content.Shared._Funkystation.SM;
using Content.Shared._Funkystation.SM.Components;
using Content.Shared.Atmos;
using Content.Shared.Explosion;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Server._Funkystation.SM.Components;

[RegisterComponent]
[Access(typeof(SupermatterSystem), typeof(SupermatterLightningSystem), typeof(SupermatterAnomalySystem))]
public sealed partial class SupermatterComponent : SharedSupermatterComponent
{
    // --- Core State ---
    /// <summary>
    /// TG wiki: the crystal is inert until struck or fed matter. Until activated, internal energy does not accumulate from gas processing and vacuum/charge hazards treat power as zero.
    /// </summary>
    [DataField("activated")]
    public bool Activated;
    /// <summary>
    /// Internal energy drive for lightning, tesla delam branch (wiki-style MeV thresholds from server CVars), radiation, and console telemetry. Runtime still uses the gas characteristic pipeline; this field is the authoritative scalar for wiki energy predicates.
    /// </summary>
    [DataField("power")]
    public float Power;
    [DataField("integrity")]
    public float Integrity = 1000f;
    [DataField("maxIntegrity")]
    public float MaxIntegrity = 1000f;
    [DataField("vacuumDamagePerTile")]
    public float VacuumDamagePerTile = 0.5f;
    /// <summary>
    /// Integrity vacuum stress only applies when stored power exceeds this (after per-tick stability injection).
    /// </summary>
    [DataField("vacuumDamageMinPower")]
    public float VacuumDamageMinPower = 50f;
    [DataField("absorptionHealing")]
    public float AbsorptionHealing = 1f;
    [DataField("ratioPerTile")]
    public float RatioPerTile = 0.05f;
    [DataField("vacuumThreshold")]
    public float VacuumThreshold = 10f;
    [DataField("currentRadiation")]
    public float CurrentRadiation;
    [DataField("RadiationDamageTypes")]
    public List<string> RadiationDamageTypes= [];
    [ViewVariables(VVAccess.ReadOnly)]
    public bool Delaminated = false;

    /// <summary>
    /// Crystal is counting down to final delamination outcome.
    /// </summary>
    [ViewVariables(VVAccess.ReadOnly)]
    public bool Delamming;

    /// <summary>
    /// Outcome chosen when <see cref="Delamming"/> began (before timer elapses).
    /// </summary>
    [ViewVariables(VVAccess.ReadOnly)]
    public DelamType PreferredDelamType;

    /// <summary>
    /// True for the rest of the atmos tick that started delamination so passive healing cannot cancel countdown instantly.
    /// </summary>
    public bool DelamBeganThisAtmos;

    /// <summary>
    /// Seconds remaining until <see cref="SupermatterDelaminationEvent"/> fires.
    /// </summary>
    [ViewVariables(VVAccess.ReadOnly)]
    public float DelamCountdown;

    /// <summary>
    /// Countdown duration when integrity first hits zero. Test protos may set small values.
    /// </summary>
    [DataField("delamTimer")]
    public float DelamTimerDuration = 30f;

    [DataField("roomTemp")]
    public float NeutralEnthalpyTemperature = 293.15f;



    // --- Process values ---
    [DataField("reproductionThreshold")]
    public float ReproductionThreshold = 1000f;
    [DataField("reproductionDecay")]
    public float ReproductionDecay = 0.9f;
    [DataField("powerDamageScale")]
    public float PowerDamageScale = 500f;
    [DataField("temperatureDamageScale")]
    public float TemperatureDamageScale = 100f;
    [DataField("absorptionHealingCost")]
    public float AbsorptionHealingCost = 10f;
    [DataField("growthAbsorptionScale")]
    public float GrowthAbsorptionScale = 45f;
    [DataField("powerPerGasPacket")]
    public float PowerPerGasPacket = 3000f;
    [DataField("stabilityPowerDrainScale")]
    public float StabilityPowerDrainScale = 0.08f;
    [DataField("baseStability")]
    public float BaseStability = 10f;
    [DataField("neutralStability")]
    public float NeutralStability = 10f;
    [DataField("baseGrowth")]
    public float BaseGrowth;
    [DataField("baseConductivity")]
    public float BaseConductivity;
    [DataField("baseEnthalpy")]
    public float BaseEnthalpy;
    [DataField("integrityChangeCap")]
    public float IntegrityChangeCap = 2f;
    [DataField("powerScalingFactor")]
    public float PowerScalingFactor = 1000f;
    [DataField("baseRadiation")]
    public float BaseRadiation = 3f;
    [DataField("powerPercentage")]
    public float PowerPercentage = 0.005f;
    [DataField("radiationDamageMultiplier")]
    public float RadiationDamageMultiplier = 10f; // For PA/emitters
    [DataField("integrityDivisor")]
    public float IntegrityDivisor = 10f;


    // --- Gas Characteristics (calculated each tick) ---
    [DataField("stability")]
    public float Stability = 10f;
    [DataField("conductivity")]
    public float Conductivity;
    [DataField("currentConductivity")]
    public float CurrentConductivity;
    [DataField("enthalpy")]
    public float Enthalpy;
    [DataField("growth")]
    public float Growth;

    // --- Internal Buffers ---
    [DataField("absorbedGas")]
    public GasMixture AbsorbedGas = new();
    [DataField("reproduction")]
    public float Reproduction;
    [DataField("reproductionProgress")]
    public float ReproductionProgress;
    [DataField("absorptionHealingPool")]
    public float AbsorptionHealingPool;
    [DataField("powerPool")]
    public float PowerPool;
    [DataField("countVacuumTiles")]
    public int CountVacuumTiles;

    // --- Lightning ---
    [DataField("lightningTimer")]
    public float LightningTimer;
    [DataField("lightningRange")]
    public float LightningRange;
    [DataField("powerPerBolt")]
    public float PowerPerBolt;
    [DataField("maxBolts")]
    public float MaxBolts;

    // --- Cached Values for Visuals ---
    [DataField("lastTemperature")]
    public float LastTemperature = 293.15f;
    [DataField("lastMaxCharacteristic")]
    public float LastMaxCharacteristic;
    [DataField("visualState")]
    public SupermatterState VisualState = SupermatterState.Inactive;

    /// <summary>
    /// Whether the entity this supermatter is attached to is being absorbed by another supermatter.
    /// </summary>
    [ViewVariables(VVAccess.ReadOnly)]
    public bool BeingAbsorbedByAnotherSupermatter = false;

    // --- Sound effects ---
    /// <summary>
    /// Ashing sound
    /// </summary>
    [DataField]
    public SoundPathSpecifier SoundAsh = new("/Audio/_Funkystation/Supermatter/supermatter.ogg");
    /// <summary>
    /// Delamming sound
    /// </summary>
    [DataField]
    public SoundPathSpecifier SoundDelamming = new("/Audio/_Funkystation/Supermatter/delamming.ogg");
    [ViewVariables]
    public EntityUid? AudioProcess;
    [ViewVariables]
    public EntityUid? MobAudioProcess;

    [DataField("screamCutOffTimer")]
    public float ScreamCutOffTimer = 0.5f;

    /// <summary>
    /// Spawned when delamination resolves as a singularity-style outcome.
    /// </summary>
    [DataField]
    public EntProtoId DelamSingularityPrototype = "Singularity";

    /// <summary>
    /// Spawned for tesloose delamination.
    /// </summary>
    [DataField]
    public EntProtoId DelamTeslaPrototype = "TeslaEnergyBall";

    [DataField]
    public ProtoId<ExplosionPrototype> DelamExplosionPrototype = "Default";

    [DataField]
    public float DelamExplosionTotalIntensity = 200f;

    [DataField]
    public float DelamExplosionSlope = 5f;

    [DataField]
    public float DelamExplosionMaxTileIntensity = 20f;

    /// <summary>
    /// Stronger explosion used for cascade-style delamination.
    /// </summary>
    [DataField]
    public float DelamCascadeIntensityMultiplier = 2f;

    // --- Anomalies (optional SM-driven spawns) ---
    /// <summary>TG wiki: anomalies become a concern above ~5000 MeV internal energy.</summary>
    [DataField]
    public float AnomalyMinPower = 5000f;

    /// <summary>When 0, anomaly spawns are not gated by conductivity (wiki emphasizes energy/integrity).</summary>
    [DataField]
    public float AnomalyMinConductivity;

    [DataField]
    public float AnomalySpawnInterval = 120f;

    [DataField]
    public EntProtoId AnomalySpawnPrototype = "RandomAnomalySpawner";

    [DataField]
    public float AnomalySpawnMinRadius = 2f;

    [DataField]
    public float AnomalySpawnMaxRadius = 8f;

    /// <summary>Count-up to next spawn attempt; starts high so maps do not spawn on first tick.</summary>
    public float AnomalyCooldown = 120f;
}
