using System.Text.RegularExpressions;

namespace Mair.EquipmentSimulator;

/// <summary>
/// Per-equipment configuration. EQUIPMENT_SIMULATOR.md §15.
/// Validation is fail-closed (NFR-014): invalid configuration aborts construction
/// rather than starting on defaults.
/// </summary>
public sealed record EquipmentOptions
{
    private static readonly Regex EquipmentIdPattern =
        new("^eq-[0-9]{3,6}$", RegexOptions.CultureInvariant);

    /// <summary>Must match the contract pattern <c>^eq-[0-9]{3,6}$</c>.</summary>
    public required string EquipmentId { get; init; }

    /// <summary>Seed for the deterministic generator. Identical seed ⇒ bit-identical telemetry (NFR-010).</summary>
    public required ulong Seed { get; init; }

    public FaultProfile FaultProfile { get; init; } = FaultProfile.Normal;

    /// <summary>Required for the four single-channel sensor profiles; must be null otherwise.</summary>
    public SensorChannel? FaultChannel { get; init; }

    /// <summary><c>T_fail</c> for BEARING_DEGRADATION, and the RUL label source (§2.1).</summary>
    public double FailureTimeSeconds { get; init; } = 1_800;

    /// <summary><c>T_cool</c> for COOLING_DEGRADATION.</summary>
    public double CoolingTimeSeconds { get; init; } = 900;

    /// <summary>Dead-man window. No setpoint refresh within it reverts to <see cref="SafeDefaultRatePct"/> (DEC-001).</summary>
    public int DeadmanTimeoutMs { get; init; } = 30_000;

    public double SafeDefaultRatePct { get; init; } = 60;

    /// <summary>Fault injection exists only when this is true (§16).</summary>
    public bool DemoProfile { get; init; }

    private static readonly FaultProfile[] SingleChannelProfiles =
    [
        FaultProfile.SensorDrift,
        FaultProfile.SensorNoise,
        FaultProfile.SensorFreeze,
        FaultProfile.SensorDropout,
    ];

    public void Validate()
    {
        if (!EquipmentIdPattern.IsMatch(EquipmentId))
        {
            throw new ArgumentException(
                $"equipmentId '{EquipmentId}' does not match the contract pattern ^eq-[0-9]{{3,6}}$.",
                nameof(EquipmentId));
        }

        var needsChannel = Array.IndexOf(SingleChannelProfiles, FaultProfile) >= 0;
        if (needsChannel && FaultChannel is null)
        {
            // The model says "one channel" without naming a default. Guessing one would
            // invent a rule the specification does not state, so this fails closed.
            throw new ArgumentException(
                $"fault profile {FaultProfile} applies to one channel, which must be named explicitly.",
                nameof(FaultChannel));
        }

        if (!needsChannel && FaultChannel is not null)
        {
            throw new ArgumentException(
                $"fault profile {FaultProfile} does not apply to a single channel.",
                nameof(FaultChannel));
        }

        if (FailureTimeSeconds <= 0 || CoolingTimeSeconds <= 0)
        {
            throw new ArgumentException("T_fail and T_cool must be positive.", nameof(FailureTimeSeconds));
        }

        if (DeadmanTimeoutMs <= 0)
        {
            throw new ArgumentException("the dead-man timeout must be positive.", nameof(DeadmanTimeoutMs));
        }

        if (!double.IsFinite(SafeDefaultRatePct) || SafeDefaultRatePct is < 0 or > 100)
        {
            throw new ArgumentException("the safe default rate must be within 0-100 %.", nameof(SafeDefaultRatePct));
        }
    }
}
