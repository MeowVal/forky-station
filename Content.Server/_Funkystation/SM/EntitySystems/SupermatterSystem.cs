using Content.Server._Funkystation.SM.Components;
using Content.Server._Funkystation.SM.Events;
using Content.Server.Administration.Logs;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Atmos.Piping.Components;
using Content.Server.Singularity.Components;
using Content.Shared._Funkystation.Mobs;
using Content.Shared._Funkystation.SM;
using Content.Shared._Funkystation.SM.Components;
using Content.Shared._Funkystation.SM.Prototypes;
using Content.Shared.Atmos;
using Content.Shared.CCVar;
using Content.Shared.Audio;
using Content.Shared.Chat.Prototypes;
using Content.Shared.Chemistry.Components.SolutionManager;
using Content.Shared.Damage.Systems;
using Content.Shared.Database;
using Content.Shared.Interaction;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Projectiles;
using Content.Shared.Radiation.Components;
using Content.Shared.Speech.Components;
using Content.Shared.Stacks;
using Content.Shared.Station.Components;
using Content.Shared.Tag;
using Content.Shared.Throwing;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Configuration;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
namespace Content.Server._Funkystation.SM.EntitySystems;

public sealed class SupermatterSystem : EntitySystem
{
    [Dependency] private readonly IAdminLogManager _adminLogger = default!;
    [Dependency] private readonly AtmosphereSystem _atmosphereSystem = default!;
    [Dependency] private readonly SharedContainerSystem _containerSystem = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly TagSystem _tagSystem = default!;
    [Dependency] private readonly TransformSystem _transformSystem = default!;
    [Dependency] private readonly SharedTransformSystem _xformSystem = default!;
    [Dependency] private readonly MetaDataSystem _metaDataSystem = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;


    private static readonly ProtoId<TagPrototype> HighRiskItemTag = "HighRiskItem";
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SupermatterComponent, AtmosDeviceUpdateEvent>(OnProcessSupermatter);
        SubscribeLocalEvent<SupermatterComponent, MapInitEvent>(OnSupermatterMapInit);
        SubscribeLocalEvent<MapGridComponent, SupermatterAttemptConsumeEntityEvent>(PreventAshing);
        SubscribeLocalEvent<StationDataComponent, SupermatterAttemptConsumeEntityEvent>(PreventAshing);
        SubscribeLocalEvent<ProjectileComponent, SupermatterAttemptConsumeEntityEvent>(PreventAshingProjectile);
        SubscribeLocalEvent<SupermatterComponent, EntGotInsertedIntoContainerMessage>(OnSupermatterContained);
        SubscribeLocalEvent<SupermatterContainedEvent>(OnSupermatterContained);
        SubscribeLocalEvent<SupermatterComponent, SupermatterAttemptConsumeEntityEvent>(OnAnotherSupermatterAttemptAbsorbThisSupermatter);
        SubscribeLocalEvent<SupermatterComponent, SupermatterAshedEntityEvent>(OnAnotherSupermatterAbsorbedThisSupermatter);
        SubscribeLocalEvent<SupermatterComponent, EntityAshedBySupermatterEvent>(OnAshed);
        SubscribeLocalEvent<SupermatterComponent, StartCollideEvent>(OnAshAbsorption);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        SubscribeLocalEvent<ContainerManagerComponent, SupermatterAshedEntityEvent>(OnContainerAshed);
        SubscribeLocalEvent<SupermatterComponent, DamageChangedEvent>(OnDamage);
        SubscribeLocalEvent<SupermatterComponent, ThrowHitByEvent>(OnEmbed);
        SubscribeLocalEvent<SupermatterComponent, InteractHandEvent>(OnInteractHand);
        SubscribeLocalEvent<SupermatterComponent, InteractUsingEvent>(OnInteractUsing);
        SubscribeLocalEvent<SupermatterImmuneComponent, SupermatterAttemptConsumeEntityEvent>(OnImmuneCancelAshing);

        LoadGasCharacteristics();
    }

    /// <summary>
    /// Prototype sets <see cref="RadiationSourceComponent"/> intensity to 0 until the first atmos tick;
    /// sync immediately so radiation hazards and tests see non-zero output without waiting for <see cref="AtmosDeviceUpdateEvent"/>.
    /// </summary>
    private void OnSupermatterMapInit(EntityUid uid, SupermatterComponent sm, MapInitEvent args)
    {
        if (sm.Delaminated)
            return;

        ComputeRadiation(sm);
        EmitRadiation(uid, sm);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<SupermatterComponent>();
        while (query.MoveNext(out var uid, out var sm))
        {
            if (!sm.Delamming || sm.Delaminated)
                continue;

            TickDelaminationCountdown(uid, sm, frameTime);
        }
    }

    /// <summary>
    /// checks if the GasCharacteristicsPrototype was modified
    /// </summary>
    /// <param name="ev"></param>
    private void OnPrototypesReloaded(PrototypesReloadedEventArgs ev)
    {
        if (ev.WasModified<GasCharacteristicsPrototype>())
            LoadGasCharacteristics();
    }

    /// <summary>
    /// Loads the Gas characteristics from yml
    /// </summary>
    private void LoadGasCharacteristics()
    {
        var newTable = new Dictionary<Gas, GasCharacteristics>();

        foreach (var proto in _proto.EnumeratePrototypes<GasCharacteristicsPrototype>())
        {
            if (!Enum.TryParse<Gas>(proto.ID, out var gas))
                continue;

            newTable[gas] = new GasCharacteristics(
                proto.Stability,
                proto.Growth,
                proto.Conductivity,
                proto.Enthalpy
            );
        }

        foreach (var sm in EntityQuery<SupermatterComponent>())
        {
            sm.GasTable = newTable;
        }
    }

    /// <summary>
    /// Process logic for each supermatter.
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="sm"></param>
    /// <param name="args"></param>
    private void OnProcessSupermatter(EntityUid uid, SupermatterComponent sm, AtmosDeviceUpdateEvent args)
    {
        sm.AbsorbedGas.Clear();
        AbsorbGas(uid, sm, args);
        if (sm.Activated)
            ApplyPowerPool(sm);
        ComputeGasCharacteristics(sm);
        ApplyPowerMultipliers(sm);
        ApplyStability(sm);
        ApplyEnthalpy(sm);
        ApplyGrowth(sm);

        if (!sm.Activated)
        {
            sm.Power = 0f;
            sm.PowerPool = 0f;
        }

        UpdateReproductionAndShards(uid, sm);
        sm.CurrentConductivity = sm.Conductivity;

        if (sm.Delaminated)
            return;

        // Integrity already zero (e.g. test injection) — begin delam before UpdateIntegrity can heal the crystal.
        if (!sm.Delaminated && !sm.Delamming && sm.Integrity <= 0)
            BeginDelaminationCountdown(uid, sm);

        UpdateIntegrity(uid, sm);

        if (!sm.Delaminated && !sm.Delamming && sm.Integrity <= 0)
            BeginDelaminationCountdown(uid, sm);

        if (sm.Delaminated)
            return;

        ComputeRadiation(sm);
        EmitRadiation(uid, sm);

        UpdateWikiItemPull(uid, sm);

        ReleaseGas(uid, sm, args);

        sm.DelamBeganThisAtmos = false;
    }

    /// <summary>
    /// TG wiki: powered crystal pulls loose items — implemented via a weak <see cref="GravityWellComponent"/>.
    /// </summary>
    private void UpdateWikiItemPull(EntityUid uid, SupermatterComponent sm)
    {
        if (sm.Delaminated || sm.Power < 1f)
        {
            // Avoid GravityWellSystem pulsing with MaxRange 0 (engine asserts positive range).
            RemComp<GravityWellComponent>(uid);
            return;
        }

        var grav = EnsureComp<GravityWellComponent>(uid);
        grav.MaxRange = Math.Clamp(sm.Power / 2000f, 0.5f, 6f);
        grav.BaseRadialAcceleration = -3f * Math.Clamp(sm.Power / 5000f, 0.15f, 1f);
    }

    /// <summary>
    /// Helper function that is a list of offsets
    /// </summary>
    private static readonly Vector2i[] AbsorptionOffsets =
    {
        new(-1, -1), new(0, -1), new(1, -1),
        new(-1,  0), new(0,  0), new(1,  0),
        new(-1,  1), new(0,  1), new(1,  1)
    };

    /// <summary>
    /// Absorbs gas in a 3 x 3 area with the Supermatter at the center
    /// </summary>
    /// <param name="smUid"></param>
    /// <param name="sm"></param>
    /// <param name="args"></param>
    private void AbsorbGas(EntityUid smUid, SupermatterComponent sm, AtmosDeviceUpdateEvent args)
    {
        sm.CountVacuumTiles = 0;
        var ratio = sm.RatioPerTile;
        if (args.Grid is not {} grid)
            return;
        var centerTile = _transformSystem.GetGridTilePositionOrDefault(smUid);

        foreach (var offset in AbsorptionOffsets)
        {
            var tile = centerTile + offset;

            var mixture = _atmosphereSystem.GetTileMixture(grid, args.Map, tile, excite: true);

            if (mixture == null)
            {
                sm.CountVacuumTiles++;
                continue;
            }

            var pressure = mixture.Pressure;
            if (pressure < sm.VacuumThreshold)
                sm.CountVacuumTiles++;

            if(pressure <= 0)
                continue;

            var absorbed = mixture.RemoveRatio(ratio);
            foreach (var (gas, moles) in absorbed)
            {
                sm.AbsorbedGas.AdjustMoles(gas, moles);
            }
            sm.AbsorbedGas.Temperature = absorbed.Temperature;
        }
    }

    /// <summary>
    /// Adds the power from when an entity is ashed to the SM
    /// </summary>
    /// <param name="sm"></param>
    private void ApplyPowerPool(SupermatterComponent sm)
    {
        if (sm.PowerPool <= 0.1f)
            return;

        var gained = sm.PowerPool * 0.10f; // 10%
        sm.Power += gained;
        sm.PowerPool -= gained;
    }

    /// <summary>
    /// Computes the characteristics of the absorbed gas
    /// </summary>
    /// <param name="sm"></param>
    private void ComputeGasCharacteristics(SupermatterComponent sm)
    {
        float stability = sm.BaseStability;
        float growth = sm.BaseGrowth;
        float conductivity = sm.BaseConductivity;
        float enthalpy = sm.BaseEnthalpy;

        foreach (var (gas, moles) in sm.AbsorbedGas )
        {
            if (moles <= 0f)
                continue;

            if (!sm.GasTable.TryGetValue(gas, out var ch))
                continue;

            stability    += moles * ch.Stability;
            growth       += moles * ch.Growth;
            conductivity += moles * ch.Conductivity;
            enthalpy     += moles * ch.Enthalpy;
        }

        // Per-tick totals from absorbed gas (base + table contribution), not cumulative across ticks.
        sm.Stability = Math.Min(stability / 100f, sm.NeutralStability);
        sm.Growth = growth / 100f;
        sm.Conductivity = conductivity / 100f;
        sm.Enthalpy = enthalpy / 100f;
    }

    /// <summary>
    /// Characteristic Multiplication by Power
    /// </summary>
    /// <param name="sm"></param>
    private void ApplyPowerMultipliers(SupermatterComponent sm)
    {
        var multiplier = 1f + sm.Power / sm.PowerScalingFactor;
        sm.Growth *= multiplier;
        sm.Conductivity *= multiplier;
        sm.Enthalpy *= multiplier;
    }

    /// <summary>
    /// Updates the stability
    /// </summary>
    /// <param name="sm"></param>
    private void ApplyStability(SupermatterComponent sm)
    {
        var stabilityEffectScale = (sm.NeutralStability - sm.Stability) / sm.NeutralStability;

        sm.Growth       *= stabilityEffectScale;
        sm.Conductivity *= stabilityEffectScale;
        sm.Enthalpy     *= stabilityEffectScale;

        sm.Power *= 1f - sm.StabilityPowerDrainScale * sm.Stability;
        sm.Power += sm.Stability;
        if (sm.Power <= 0f)
            sm.Power = 0;
    }

    /// <summary>
    /// Updates the Enthalpy
    /// </summary>
    /// <param name="sm"></param>
    private void ApplyEnthalpy( SupermatterComponent sm)
    {
        var deltaEnergy = sm.Enthalpy * 1_000_000f; // MJ → joules
        sm.Power += sm.Enthalpy * (sm.AbsorbedGas.Temperature - sm.NeutralEnthalpyTemperature); // temperature - room temperature in Kelvin
        if (sm.Power <= 0f)
            sm.Power = 0;
        _atmosphereSystem.AddHeat(sm.AbsorbedGas, deltaEnergy);
    }

    /// <summary>
    /// Updates the growth
    /// </summary>
    /// <param name="sm"></param>
    private void ApplyGrowth(SupermatterComponent sm)
    {
        switch (sm.Growth)
        {
            //Negative Growth
            case < 0f:
            {
                var amount = -sm.Growth;
                var count = (int)MathF.Floor((sm.Power + sm.PowerPerGasPacket) / sm.PowerPerGasPacket);
                if (count < 1)
                    count = 1;

                var characteristics = new List<(float value, Gas gas)>
                {
                    (sm.Growth,        Gas.Ammonia),
                    (sm.Enthalpy >= 0 ? sm.Enthalpy : -sm.Enthalpy, sm.Enthalpy >= 0 ? Gas.Plasma     : Gas.Frezon),
                    (sm.Conductivity >= 0 ? sm.Conductivity : -sm.Conductivity, sm.Conductivity >= 0 ? Gas.WaterVapor : Gas.Oxygen),
                    (sm.Stability >= 0 ? sm.Stability : -sm.Stability, sm.Stability >= 0 ? Gas.Nitrogen   : Gas.Tritium),
                };
                characteristics.Sort((a, b) => MathF.Abs(b.value).CompareTo(MathF.Abs(a.value)));
                for (var i = 0; i < count && i < characteristics.Count; i++)
                {
                    var (_, gas) = characteristics[i];
                    sm.AbsorbedGas.AdjustMoles((int)gas, amount);
                }

                sm.Power -= amount * count;
                if (sm.Power <= 0f)
                    sm.Power = 0;
                return;
            }
            //Positive growth
            case > 0f:
            {
                var fraction = sm.Growth / sm.GrowthAbsorptionScale;
                if (fraction <= 0f)
                    break;

                if (fraction > 1f)
                    fraction = 1f;

                var absorbed = sm.AbsorbedGas.RemoveRatio(fraction);
                _atmosphereSystem.Merge(sm.AbsorbedGas, absorbed);

                var absorbedMoles = absorbed.TotalMoles;

                if (absorbedMoles <= 0f)
                    break;

                sm.Power += Math.Abs(absorbedMoles);
                sm.Reproduction += absorbedMoles;
                break;
            }
        }

    }

    /// <summary>
    /// Updates the reproduction and creates a shard when reaching the threshold
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="sm"></param>
    private void UpdateReproductionAndShards(EntityUid uid, SupermatterComponent sm)
    {
        if (HasComp<SupermatterShardComponent>(uid))
            return;

        sm.Reproduction *= sm.ReproductionDecay;

        sm.ReproductionProgress += sm.Reproduction;

        while (sm.ReproductionProgress >= sm.ReproductionThreshold)
        {
            sm.ReproductionProgress -= sm.ReproductionThreshold;

            var coords = Transform(uid).Coordinates;
            Spawn("SupermatterShard", coords);
        }
    }

    /// <summary>
    /// Updates the integrity of the supermatter crystal
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="sm"></param>
    private void UpdateIntegrity(EntityUid uid, SupermatterComponent sm)
    {
        if (sm.Delaminated)
            return;

        var delta = 0f;

        if (sm.Activated)
        {
            // Bias relative to nominal stability so a “perfectly stable” crystal does not out-heal vacuum/power stress each tick.
            delta += sm.Stability - sm.NeutralStability;
            delta -= sm.Power / sm.PowerDamageScale;

            if (sm.Power > sm.VacuumDamageMinPower)
                delta -= sm.CountVacuumTiles * sm.VacuumDamagePerTile;

            var gasTemp = sm.AbsorbedGas.Temperature;
            const float roomTemp = 293.15f;
            var tempDelta = ((gasTemp - roomTemp) / sm.TemperatureDamageScale) * sm.Enthalpy;
            delta += tempDelta;
        }

        if (sm.AbsorptionHealingPool > sm.AbsorptionHealingCost)
        {
            delta += sm.AbsorptionHealing;
            sm.AbsorptionHealingPool -= sm.AbsorptionHealingCost;
        }

        sm.Integrity += Math.Clamp(delta, -sm.IntegrityChangeCap, sm.IntegrityChangeCap);
        sm.Integrity = Math.Clamp(sm.Integrity, 0f, sm.MaxIntegrity);

        // TG wiki: during the final countdown the crystal can recover if integrity heals back above zero.
        if (sm.Delamming && sm.Integrity > 0f && !sm.DelamBeganThisAtmos)
        {
            sm.Delamming = false;
            sm.DelamCountdown = 0f;
        }
    }

    /// <summary>
    /// Checks if the supermatters intregrity has hit 0
    /// and raises an event if it has which trigger the delamination
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="sm"></param>
    private void BeginDelaminationCountdown(EntityUid uid, SupermatterComponent sm)
    {
        sm.PreferredDelamType = ChooseDelamType(uid, sm);
        sm.Delamming = true;
        sm.DelamBeganThisAtmos = true;
        sm.DelamCountdown = sm.DelamTimerDuration;
        if (sm.DelamCountdown <= 0)
            ResolveDelamination(uid, sm);
    }

    private void TickDelaminationCountdown(EntityUid uid, SupermatterComponent sm, float dt)
    {
        sm.DelamCountdown -= dt;
        if (sm.DelamCountdown <= 0)
            ResolveDelamination(uid, sm);
    }

    private void ResolveDelamination(EntityUid uid, SupermatterComponent sm)
    {
        var dominant = GetDominantCharacteristic(sm);
        var ev = new SupermatterDelaminationEvent(uid, dominant, sm.PreferredDelamType);
        RaiseLocalEvent(uid, ref ev);
        sm.Delaminated = true;
        sm.Delamming = false;
        sm.DelamCountdown = 0;
        _audio.PlayPvs(sm.SoundDelamming, uid);
    }

    /// <summary>
    /// TG wiki delamination priority (later wiki list entries beat earlier): cascade &gt; singularity &gt; tesla &gt; default explosion.
    /// </summary>
    public DelamType ChooseDelamType(EntityUid uid, SupermatterComponent sm)
    {
        if (_cfg.GetCVar(CCVars.SupermatterDoForceDelam))
        {
            var forced = _cfg.GetCVar(CCVars.SupermatterForcedDelamType);
            if (forced >= 0 && forced <= (byte)DelamType.Cascade)
                return (DelamType)(byte)forced;
            return DelamType.Explosion;
        }

        var minNob = _cfg.GetCVar(CCVars.SupermatterCascadeNobMinFraction);
        var cascadeMoles = _cfg.GetCVar(CCVars.SupermatterCascadeMinAbsorbedMoles);
        if (_cfg.GetCVar(CCVars.SupermatterDoCascadeDelam) &&
            AbsorbedMixQualifiesForWikiResonanceCascade(sm.AbsorbedGas, cascadeMoles, minNob))
            return DelamType.Cascade;

        var singuloNeed = _cfg.GetCVar(CCVars.SupermatterSinguloAbsorbedMolesThreshold) *
                          _cfg.GetCVar(CCVars.SupermatterSingulooseMolesModifier);
        if (_cfg.GetCVar(CCVars.SupermatterDoSingulooseDelam) && sm.AbsorbedGas.TotalMoles > singuloNeed)
            return DelamType.Singulo;

        var powerNeed = _cfg.GetCVar(CCVars.SupermatterPowerPenaltyThreshold) *
                        _cfg.GetCVar(CCVars.SupermatterTesloosePowerModifier);
        if (_cfg.GetCVar(CCVars.SupermatterDoTeslooseDelam) && sm.Power > powerNeed)
            return DelamType.Tesla;

        return DelamType.Explosion;
    }

    /// <summary>
    /// TG wiki resonance cascade: both nob gases above fraction threshold in the absorbed mix, total absorbed moles above minimum.
    /// </summary>
    public static bool AbsorbedMixQualifiesForWikiResonanceCascade(GasMixture mix, float minTotalMoles, float minNobFraction)
    {
        var total = mix.TotalMoles;
        if (total < minTotalMoles)
            return false;

        var anti = mix.GetMoles(Gas.AntiNoblium);
        var hyper = mix.GetMoles(Gas.HyperNoblium);
        if (anti <= 0f || hyper <= 0f)
            return false;

        return anti / total > minNobFraction && hyper / total > minNobFraction;
    }

    /// <summary>
    /// Integration tests need to set <see cref="SupermatterComponent.Power"/> without tripping Access checks on that component.
    /// </summary>
    public void SetPowerForIntegrationTests(EntityUid uid, float power)
    {
        var sm = Comp<SupermatterComponent>(uid);
        sm.Power = power;
        if (power > 0f)
            sm.Activated = true;
    }

    /// <summary>
    /// Integration tests: wiki “inert until struck” vs powered gas processing.
    /// </summary>
    public void SetActivatedForIntegrationTests(EntityUid uid, bool activated)
    {
        Comp<SupermatterComponent>(uid).Activated = activated;
    }

    /// <summary>
    /// Integration tests: set absorbed gas snapshot for <see cref="ChooseDelamType"/> without running a full atmos tick.
    /// </summary>
    public void SetAbsorbedGasForIntegrationTests(EntityUid uid, GasMixture mix)
    {
        var sm = Comp<SupermatterComponent>(uid);
        sm.AbsorbedGas.Clear();
        _atmosphereSystem.Merge(sm.AbsorbedGas, mix.Clone());
    }

    /// <summary>
    /// Integration tests set integrity directly without tripping Access checks.
    /// </summary>
    public void SetIntegrityForIntegrationTests(EntityUid uid, float integrity)
    {
        var sm = Comp<SupermatterComponent>(uid);
        sm.Integrity = Math.Clamp(integrity, 0f, sm.MaxIntegrity);
    }

    /// <summary>
    /// Percent crystal remaining (0–100), for portable test parity with historical damage-based display.
    /// </summary>
    public static float GetIntegrityPercent(SupermatterComponent sm) =>
        GetIntegrityPercent(sm.Integrity, sm.MaxIntegrity);

    public static float GetIntegrityPercent(float integrity, float maxIntegrity)
    {
        if (maxIntegrity <= 0)
            return 0f;
        return integrity / maxIntegrity * 100f;
    }

    private void OnInteractHand(EntityUid uid, SupermatterComponent sm, InteractHandEvent args)
    {
        if (args.Handled)
            return;

        if (HasComp<SupermatterImmuneComponent>(args.User))
            return;

        if (!AttemptAshEntity(uid, args.User, sm))
            return;

        args.Handled = true;
    }

    private void OnInteractUsing(EntityUid uid, SupermatterComponent sm, InteractUsingEvent args)
    {
        if (args.Handled)
            return;

        if (!AttemptAshEntity(uid, args.Used, sm))
            return;

        args.Handled = true;
    }

    private void OnImmuneCancelAshing(EntityUid uid, SupermatterImmuneComponent _, ref SupermatterAttemptConsumeEntityEvent args)
    {
        args.Cancelled = true;
    }

    /// <summary>
    /// Gets the dominant Characteristics for delamination
    /// </summary>
    /// <param name="sm"></param>
    /// <returns></returns>
    private GasCharacteristicsType GetDominantCharacteristic(SupermatterComponent sm)
    {
        // Start with Growth as the default
        var dominant = GasCharacteristicsType.Growth;
        var max = MathF.Abs(sm.Growth);

        var conductivity = MathF.Abs(sm.Conductivity);
        if (conductivity > max)
        {
            max = conductivity;
            dominant = GasCharacteristicsType.Conductivity;
        }

        var enthalpy = MathF.Abs(sm.Enthalpy);
        if (enthalpy > max)
        {
            max = enthalpy;
            dominant = GasCharacteristicsType.Enthalpy;
        }

        var stability = MathF.Abs(sm.Stability);
        if (stability > max)
        {
            dominant = GasCharacteristicsType.Stability;
        }

        return dominant;
    }

    /// <summary>
    /// Computes the radiation output of the Supermatter based on power and stability
    /// </summary>
    /// <param name="sm"></param>
    private void ComputeRadiation(SupermatterComponent sm)
    {
        var baseRadiation = sm.BaseRadiation + (sm.Power * sm.PowerPercentage);
        var stabilityMultiplier = (10f - sm.Stability) / 10f;
        // Floor so nominal stability still yields measurable output (radiation source + integration tests).
        stabilityMultiplier = MathF.Max(0.15f, stabilityMultiplier);
        sm.CurrentRadiation = baseRadiation * stabilityMultiplier;
    }

    /// <summary>
    /// Updates the RadiationSourceComponent with the current radiation intensity of the supermatter
    /// </summary>
    /// <param name="smUid"></param>
    /// <param name="sm"></param>
    private void EmitRadiation(EntityUid smUid, SupermatterComponent sm)
    {
        var rad = EnsureComp<RadiationSourceComponent>(smUid);
        rad.Intensity = sm.CurrentRadiation;
    }

    /// <summary>
    /// Releases the gases the sm absorbed and produced
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="sm"></param>
    /// <param name="args"></param>
    private void ReleaseGas(EntityUid uid,SupermatterComponent sm, AtmosDeviceUpdateEvent args)
    {
        if (args.Grid is not {} grid)
            return;
        var centerTile = _transformSystem.GetGridTilePositionOrDefault(uid);

        var mixture = _atmosphereSystem.GetTileMixture(grid, args.Map, centerTile, excite: true);
        if (mixture == null)
            return;

        _atmosphereSystem.Merge(mixture, sm.AbsorbedGas);
    }

    // This whole section could potentially be reduced by using the
    // Event horizon consumption system as most of the functions are taken from there
    // and changed a bit to fit the supermatter.
    // Credit to TemporalOroboros <TemporalOroboros@gmail.com> for the original functions.
    #region Ashing
    /// <summary>
    /// Handles supermatter ashing any entities they bump into.
    /// The supermatter will not ash any entities if it itself has been absorbed by a supermatter.
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="sm"></param>
    /// <param name="args"></param>
    private void OnAshAbsorption(EntityUid uid, SupermatterComponent sm, ref StartCollideEvent args)
    {
        AttemptAshEntity(uid, args.OtherEntity, sm);
    }


    /// <summary>
    /// Makes a supermatter attempt to ash a given entity.
    /// </summary>
    /// <param name="hungry"></param>
    /// <param name="morsel"></param>
    /// <param name="sm"></param>
    /// <param name="outerContainer"></param>
    /// <param name="fromTree"></param>
    /// <param name="isMob"></param>
    /// <returns></returns>
    private bool AttemptAshEntity(EntityUid hungry, EntityUid morsel, SupermatterComponent sm, BaseContainer? outerContainer = null, bool fromTree = false, bool isMob = false)
    {
        if (!CanAshEntity(hungry, morsel, sm))
            return false;

        if (TryComp<PhysicsComponent>(morsel, out var phys))
        {
            if (phys.Mass == 0)
                return false;
        }

        if (Name(morsel) == "ash")
            return false;

        AshEntity(hungry, morsel, sm, outerContainer, fromTree, isMob);
        return true;
    }

    /// <summary>
    /// Checks whether a supermatter can ash a given entity.
    /// </summary>
    /// <param name="hungry"></param>
    /// <param name="uid"></param>
    /// <param name="sm"></param>
    /// <returns></returns>
    private bool CanAshEntity(EntityUid hungry, EntityUid uid, SupermatterComponent sm)
    {
        var ev = new SupermatterAttemptConsumeEntityEvent(uid, hungry, sm);
        RaiseLocalEvent(uid, ref ev);
        return !ev.Cancelled;
    }

    /// <summary>
    /// Makes a supermatter ash a given entity.
    /// </summary>
    /// <param name="hungry"></param>
    /// <param name="morsel"></param>
    /// <param name="sm"></param>
    /// <param name="outerContainer"></param>
    /// <param name="fromTree"></param>
    /// <param name="isMob"></param>
    private void AshEntity(EntityUid hungry, EntityUid morsel, SupermatterComponent sm, BaseContainer? outerContainer = null, bool fromTree = false, bool isMob = false)
    {
        if (EntityManager.IsQueuedForDeletion(morsel)) // already handled, and we're substepping
            return;

        if (HasComp<MindContainerComponent>(morsel)
            || _tagSystem.HasTag(morsel, HighRiskItemTag))
        {
            _adminLogger.Add(LogType.EntityDelete, LogImpact.High, $"{ToPrettyString(morsel):player} entered the Supermatter of {ToPrettyString(hungry)} and was deleted");
        }

        QueueDel(morsel);
        var evSelf = new EntityAshedBySupermatterEvent(morsel, hungry, sm, outerContainer, fromTree, isMob);
        var evEaten = new SupermatterAshedEntityEvent(morsel, hungry, sm, outerContainer, fromTree, isMob );
        RaiseLocalEvent(hungry, ref evSelf);
        RaiseLocalEvent(morsel, ref evEaten);
    }

    /// <summary>
    /// Modified version of the TryPlayEmoteSound from SharedChatSystem.
    /// Modified to take the SM component and save the audio process on the SM component for later use
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="sm"></param>
    /// <param name="proto"></param>
    /// <param name="emoteId"></param>
    /// <param name="audioParams"></param>
    /// <returns></returns>
    public void TryPlayEmoteSound(EntityUid uid, SupermatterComponent sm, EmoteSoundsPrototype? proto, string emoteId, AudioParams? audioParams = null)
    {
        if (proto == null)
            return;

        // try to get specific sound for this emote
        if (!proto.Sounds.TryGetValue(emoteId, out var sound))
        {
            // no specific sound - check fallback
            sound = proto.FallbackSound;
            if (sound == null)
                return;
        }

        // optional override params > general params for all sounds in set > individual sound params
        var param = audioParams ?? proto.GeneralParams ?? sound.Params;
        sm.MobAudioProcess = _audio.PlayPvs(sound, uid, param)?.Entity;
    }

    /// <summary>
    /// Adds power to the sm and adjust the integrity or AbsorptionHealingPool
    /// accordingly to whether the entity is alive or not.
    /// Also spawns an ash entity at the location of the ashed entity
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="smUid"></param>
    /// <param name="sm"></param>
    /// <param name="args"></param>
    private void OnAshed(EntityUid uid,SupermatterComponent sm, EntityAshedBySupermatterEvent args)
    {
        sm.Activated = true;
        int count = 1;
        if (TryComp<MobStateComponent>(args.Entity, out var mob))
        {
            if (TryComp<VocalComponent>(args.Entity, out var vocal) && vocal.EmoteSounds is {} sounds)
            {
                TryPlayEmoteSound(uid, sm, _proto.Index(sounds), vocal.ScreamId);
                _audio.PlayPvs(sm.SoundAsh, uid);
                Robust.Shared.Timing.Timer.Spawn(TimeSpan.FromSeconds(sm.ScreamCutOffTimer),
                    () =>
                {
                    if (sm.MobAudioProcess != null)
                        _audio.Stop(sm.MobAudioProcess);
                });
            }

            if (mob.CurrentState is not (MobState.Alive or MobState.Critical))
                return;

            if (!TryComp<MobSizeComponent>(args.Entity, out var size))
                return;

            var power = size.SizeProto?.SmPower ?? 0f;
            sm.Power += power;
            sm.Integrity -= power / sm.IntegrityDivisor;
        }
        else
        {

            if(args is { FromContainerTree: false, IsMob: false })
                _audio.PlayPvs(sm.SoundAsh, uid);

            else if(!_audio.IsPlaying(sm.AudioProcess) && !args.IsMob)
                sm.AudioProcess = _audio.PlayPvs(sm.SoundAsh, uid)?.Entity;

            if (TryComp<StackComponent>(args.Entity, out var stack))
                count = stack.Count;

            if (!TryComp<PhysicsComponent>(args.Entity, out var phys))
                return;

            if (phys.Mass == 0)
                return;

            sm.PowerPool += phys.Mass * count;
            sm.AbsorptionHealingPool += phys.Mass * count;
        }

        if (args.FromContainerTree || HasComp<ContainerManagerComponent>(args.Entity) )
            return;

        var coords = Transform(args.Entity).Coordinates;
        var ash = SpawnAtPosition("Ash", coords);

        if (count > 1)
        {
            var meta = MetaData(ash);
            var baseDesc = meta.EntityDescription;
            var newDesc = $"{baseDesc} It contains the remains of {count} things.";
            _metaDataSystem.SetEntityDescription(ash, newDesc, meta);
        }
    }

    /// <summary>
    /// A generic event handler that prevents supermatters from ashing entities with a component of a given type if registered.
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="comp"></param>
    /// <param name="args"></param>
    /// <typeparam name="TComp"></typeparam>
    private static void PreventAshing<TComp>(EntityUid uid, TComp comp, ref SupermatterAttemptConsumeEntityEvent args)
    {
        if (!args.Cancelled)
            args.Cancelled = true;
    }

    private void PreventAshingProjectile(EntityUid uid, ProjectileComponent comp, ref SupermatterAttemptConsumeEntityEvent args)
    {
        if (HasComp<EmbeddableProjectileComponent>(uid))
            return;
        args.Cancelled = true;
    }


    /// <summary>
    /// Handles supermatters attempting to escape containers they have been inserted into.
    /// If the supermatter has not been absorbed by another supermatter this handles making the supermatter ash the containing
    ///     container and drop the the next innermost contaning container.
    /// This loops until the supermatter has escaped to the map or wound up in an indestructible container.
    /// </summary>
    /// <param name="args"></param>
    private void OnSupermatterContained(SupermatterContainedEvent args)
    {
        var uid = args.Entity;
        if (!Exists(uid))
            return;
        var comp = args.Supermatter;
        if (comp.BeingAbsorbedByAnotherSupermatter)
            return;

        var containerEntity = args.Args.Container.Owner;
        if (!Exists(containerEntity))
            return;
        if (AttemptAshEntity(uid, containerEntity, comp))
            return; // If we ash the entity we also ash everything in the containers it has.

        AshContainerTree(uid, containerEntity, comp, args.Args.Container);
    }

    /// <summary>
    /// Recursively ash all entities within a container that is ashed by the supermatter.
    /// If an entity within an ashed container cannot be ashed itself it is removed from the container.
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="comp"></param>
    /// <param name="args"></param>
    private void OnContainerAshed(EntityUid uid, ContainerManagerComponent comp, ref SupermatterAshedEntityEvent args)
    {
        if (args.Container != null)
            return;

        var dropContainer = args.Container;
        if (dropContainer is null)
            _containerSystem.TryGetContainingContainer((uid, null, null), out dropContainer);

        AshContainerTree(args.SupermatterUid, args.Entity, args.Supermatter, dropContainer);
    }

    /// <summary>
    /// Makes a list of all entities in the container tree
    /// </summary>
    /// <param name="hungry"></param>
    /// <param name="morsel"></param>
    /// <param name="sm"></param>
    /// <param name="outerContainer"></param>
    private void AshContainerTree(EntityUid hungry, EntityUid morsel, SupermatterComponent sm, BaseContainer? outerContainer)
    {
        if (_containerSystem.TryGetContainingContainer((morsel, null, null), out var parent))
            return;

        List<BaseContainer> allContainers = new();
        CollectAllContainers(morsel, allContainers);

        List<EntityUid> allEntities = new();
        allEntities.Add(morsel);
        CollectAllEntities(allContainers, allEntities);

        // Step 3: Ash them
        AshCollectedEntities(hungry, sm, outerContainer, morsel, allEntities);
    }

    /// <summary>
    /// RECURSION ALERT
    /// Recursive Depth‑First Search of all containers
    /// We love Recursion
    /// Explores the container tree as deep as possible before backing up and going down the next branch on the tree.
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="results"></param>
    private void CollectAllContainers(EntityUid uid, List<BaseContainer> results)
    {
        if (!HasComp<ContainerManagerComponent>(uid))
            return;
        foreach (var container in _containerSystem.GetAllContainers(uid))
        {
            results.Add(container);

            foreach (var entity in container.ContainedEntities)
            {
                if (HasComp<SolutionContainerManagerComponent>(entity))
                    continue;
                CollectAllContainers(entity, results);
            }
        }
    }

    /// <summary>
    /// Iterative Depth‑First Search of all containers.
    /// Explores the container tree as deep as possible before backing up and going down the next branch on the tree.
    /// </summary>
    /// <param name="root"></param>
    /// <param name="results"></param>
    private void CollectAllContainersIterative(EntityUid root, List<BaseContainer> results)
    {
        Stack<EntityUid> stack = new();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var uid = stack.Pop();

            foreach (var container in _containerSystem.GetAllContainers(uid))
            {
                results.Add(container);

                foreach (var entity in container.ContainedEntities)
                {
                    if (HasComp<SolutionContainerManagerComponent>(entity))
                        continue;
                    stack.Push(entity);
                }
            }
        }
    }


    /// <summary>
    /// Finds all the entities in a list of containers
    /// </summary>
    /// <param name="containers"></param>
    /// <param name="results"></param>
    private void CollectAllEntities(List<BaseContainer> containers, List<EntityUid> results)
    {
        foreach (var container in containers)
        {
            foreach (var entity in container.ContainedEntities)
            {
                results.Add(entity);
            }
        }
    }

    /// <summary>
    /// Ashes all entities in a list of entities
    /// </summary>
    /// <param name="hungry"></param>
    /// <param name="sm"></param>
    /// <param name="outerContainer"></param>
    /// <param name="morsel"></param>
    /// <param name="allEntities"></param>
    private void AshCollectedEntities(EntityUid hungry, SupermatterComponent sm, BaseContainer? outerContainer, EntityUid morsel, List<EntityUid> allEntities)
    {
        List<EntityUid> immune = new();
        var ashedCount = 0;
        var baseIsMob = false;
        if(HasComp<MobStateComponent>(morsel))
            baseIsMob = true;
        foreach (var entity in allEntities)
        {
            if (entity == hungry || !AttemptAshEntity(hungry, entity, sm, outerContainer, fromTree: true,  isMob: baseIsMob))
            {
                // The first check keeps supermatters an admin smited into a locker from ashing themselves.
                // The second check keeps things that have been rendered immune to supermatters from being deleted by a supermatter eating their container.
                immune.Add(entity);
                continue;
            }
            if (TryComp<StackComponent>(entity, out var stack))
            {
                ashedCount += stack.Count;
                if (HasComp<SolutionContainerManagerComponent>(entity)) // Ideally this check is not needed but because morsel does not get filtered it's needed
                    ashedCount -= 1;

            }
            else
            {
                ashedCount++;
            }

        }
        if (ashedCount  > 0)
        {
            var coords = Transform(morsel).Coordinates;
            var ash = SpawnAtPosition("Ash", coords);

            // Set description
            if (ashedCount > 1)
            {
                var meta = MetaData(ash);
                var baseDesc = meta.EntityDescription; // "This used to be something, but now it's not."
                var newDesc = $"{baseDesc} It contains the remains of {ashedCount} things.";
                _metaDataSystem.SetEntityDescription(ash,newDesc, meta);
            }
        }

        // Eject immune items if needed
        foreach (var entity in immune)
        {
            var target = outerContainer;

            while (target != null)
            {
                if (_containerSystem.Insert(entity, target))
                    break;

                _containerSystem.TryGetContainingContainer((target.Owner, null, null), out target);
            }

            if (target == null)
                _xformSystem.AttachToGridOrMap(entity);
        }
    }



    /// <summary>
    /// Prevents two supermatters from annihilating one another.
    /// Specifically prevents supermatters from absorbing themselves.
    /// Also ensures that if this supermatter has already been absorbed by another supermatter it cannot be absorbed again.
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="comp"></param>
    /// <param name="args"></param>
    private void OnAnotherSupermatterAttemptAbsorbThisSupermatter(EntityUid uid, SupermatterComponent comp, ref SupermatterAttemptConsumeEntityEvent args)
    {
        if (!args.Cancelled && (args.Supermatter == comp || comp.BeingAbsorbedByAnotherSupermatter))
            args.Cancelled = true;
    }

    /// <summary>
    /// Prevents two supermatters from annihilating one another.
    /// Specifically ensures if this supermatter is absorbed by another supermatter it knows that it has been absorbed.
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="comp"></param>
    /// <param name="args"></param>
    private static void OnAnotherSupermatterAbsorbedThisSupermatter(EntityUid uid, SupermatterComponent comp, ref SupermatterAshedEntityEvent args)
    {
        comp.BeingAbsorbedByAnotherSupermatter = true;
    }

    /// <summary>
    /// Handles supermatters deciding to escape containers they are inserted into.
    /// Delegates the actual escape to <see cref="OnSupermatterContained(SupermatterContainedEvent)" /> on a delay.
    /// This ensures that the escape is handled after all other handlers for the insertion event and satisfies the assertion that
    ///     the inserted entity SHALL be inside of the specified container after all handles to the entity event
    ///     <see cref="EntGotInsertedIntoContainerMessage" /> are processed.
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="comp"></param>
    /// <param name="args"></param>
    private void OnSupermatterContained(EntityUid uid, SupermatterComponent comp, EntGotInsertedIntoContainerMessage args)
    {
        // Delegates processing an event until all queued events have been processed.
        QueueLocalEvent(new SupermatterContainedEvent(uid, comp, args));
    }


    #endregion

    /// <summary>
    /// Converts damage into power and
    /// scales radiation damage by the radiation damage multiplier so that it gives way more power
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="sm"></param>
    /// <param name="args"></param>
    private void OnDamage(EntityUid uid, SupermatterComponent sm, ref DamageChangedEvent args)
    {
        if (args.DamageDelta is null)
            return;
        var totalDamage = 0f;

        foreach (var (typeId, amount) in args.DamageDelta.DamageDict)
        {
            if (amount <= 0)
                continue;

            if (sm.RadiationDamageTypes.Contains(typeId))
                totalDamage += (float) amount * sm.RadiationDamageMultiplier;
            else
                totalDamage += (float) amount;
        }
        if (totalDamage <= 0)
            return;

        sm.Activated = true;
        sm.PowerPool += totalDamage;
    }

    private void OnEmbed(EntityUid uid, SupermatterComponent sm, ref ThrowHitByEvent args)
    {

        AttemptAshEntity(args.Target, args.Thrown, sm);
    }

}
