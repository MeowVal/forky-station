

using Content.Shared.Atmos;
using Robust.Shared.Serialization;

namespace Content.Shared._Funkystation.SM.Components;

public abstract partial class SharedSupermatterComponent : Component
{
    public Dictionary<Gas, GasCharacteristics> GasTable = new();
}
[Serializable, NetSerializable]
public enum SupermatterState : byte
{
    Inactive,
    Stable,
    Unstable,
    Critical,
    Delaminating
}
[Serializable, NetSerializable]
public readonly record struct GasCharacteristics(
    float Stability,
    float Growth,
    float Conductivity,
    float Enthalpy
);
[Serializable, NetSerializable]
public enum GasCharacteristicsType
{
    Stability,
    Growth,
    Conductivity,
    Enthalpy,
}
[Serializable, NetSerializable]
public enum SupermatterVisualKeys : byte
{
    State
}
