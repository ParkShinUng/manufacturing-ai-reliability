namespace Mair.EquipmentSimulator;

/// <summary>
/// One simulated variable-speed motor-driven conveyor drive unit: physics, state machine,
/// fault profiles, and equipment self-protection.
/// <para>
/// This type is layer <b>L3</b> in the authority model (DEC-001, ADR-0011). It has no
/// dependency on any other component of the platform — that is not an accident of the
/// implementation, it is the property AC-020 asserts: protective trips and the dead-man
/// revert keep working with the entire platform stopped.
/// </para>
/// <para>
/// Time is advanced only by <see cref="Tick"/>, never read from a clock, so a run is a
/// pure function of (seed, command sequence, tick count). That is what makes PROP-03
/// checkable at all.
/// </para>
/// </summary>
public sealed class EquipmentSimulation
{
    // --- Model constants. EQUIPMENT_MODEL_AND_STATE.md sections 1.3, 1.4, 3.3, 3.4. ---
    // Every one of these is a SIMULATOR DESIGN CONSTANT, not a real-world safety limit.
    private const double TickSeconds = 0.1;
    private const double AmbientC = 22.0;
    private const double ThermalTauSeconds = 180.0;
    private const double MaxSlewPctPerSecond = 15.0;

    private const double TripTemperatureC = 120.0;
    private const double TripVibrationRms = 25.0;
    private const double TripCurrentA = 32.0;
    private const double DegradeVibrationRms = 12.0;
    private const double DegradeTemperatureC = 95.0;

    private const int ConnectTimeoutTicks = 100;   // 10 s
    private const int StopTimeoutTicks = 150;      // 15 s
    private const int DegradedClearHoldTicks = 300; // 30 s
    private const int SlewGraceTicks = 50;         // 5 s
    private const int UncertainSustainTicks = 20;  // 2 s
    private const int BadSustainTicks = 20;        // 2 s
    private const int OverCurrentSustainTicks = 10; // 1 s
    private const int FaultEventBufferCapacity = 1_000;

    private readonly EquipmentOptions _options;
    private readonly DeterministicRandom _random;
    private readonly int _deadmanTicks;
    private readonly Queue<FaultEvent> _faultEvents = new();
    private readonly HashSet<ProtectiveCondition> _injectedConditions = [];
    private readonly HashSet<ProtectiveCondition> _latched = [];

    private EquipmentState _state = EquipmentState.Offline;
    private double _setpointPct;
    private double _appliedPct;
    private double _expectedPct;
    private double _temperatureC = AmbientC;
    private double _health;           // h in [0,1], 0 = healthy
    private double _coolingLoss;      // c in [0,1], 0 = nominal cooling
    private double? _frozenValue;
    private bool _sensorFaultActive = true;
    private bool _commLost;
    private bool _stopRequired;

    private ulong _sequence;
    private ulong _elapsedMs;
    private long _tickCount;
    private int _ticksSinceSetpointWrite;
    private int _connectingTicks;
    private int _stoppingTicks;
    private int _overCurrentTicks;
    private int _badQualityTicks;
    private int _uncertainTicks;
    private int _rateDeviationTicks;
    private int _degradedClearTicks;

    public EquipmentSimulation(EquipmentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _options = options;
        _random = new DeterministicRandom(options.Seed);
        _deadmanTicks = options.DeadmanTimeoutMs / (int)(TickSeconds * 1000);
    }

    public string EquipmentId => _options.EquipmentId;

    public EquipmentState State => _state;

    /// <summary>RUNNING is the only AI-eligible state (§3.1).</summary>
    public bool IsAiEligible => _state == EquipmentState.Running;

    public double SetpointRatePct => _setpointPct;

    public double AppliedRatePct => _appliedPct;

    public IReadOnlyCollection<ProtectiveCondition> LatchedConditions => _latched;

    public int DeadmanRevertCount { get; private set; }

    public int ProtectiveTripCount { get; private set; }

    // ---------------------------------------------------------------- commands

    /// <summary>T1: OFFLINE → CONNECTING.</summary>
    public void Connect()
    {
        if (_state == EquipmentState.Offline)
        {
            _state = EquipmentState.Connecting;
            _connectingTicks = 0;
        }
    }

    /// <summary>T11: protocol session lost, from any state.</summary>
    public void SessionLost() => _state = EquipmentState.Offline;

    /// <summary>
    /// Writes the operation rate setpoint — the only writable control value (§1.1).
    /// Naturally idempotent, which is what makes Control Service retry safe.
    /// An out-of-range value is rejected, never clamped (§11).
    /// </summary>
    public SetpointResult WriteSetpoint(double ratePct)
    {
        if (!double.IsFinite(ratePct) || ratePct is < 0 or > 100)
        {
            return SetpointResult.RejectedOutOfRange;
        }

        if (_state is not (EquipmentState.Idle or EquipmentState.Running or EquipmentState.Degraded))
        {
            return SetpointResult.RejectedState;
        }

        _setpointPct = ratePct;
        _ticksSinceSetpointWrite = 0;

        // T5 / T12: a zero setpoint begins a stop, and AI authority ends immediately.
        if (ratePct == 0 && _state is EquipmentState.Running or EquipmentState.Degraded)
        {
            _state = EquipmentState.Stopping;
            _stoppingTicks = 0;
        }

        return SetpointResult.Accepted;
    }

    /// <summary>Enters STOP_REQUIRED, a protective condition (§3.4).</summary>
    public void RequestStop() => _stopRequired = true;

    /// <summary>
    /// T10: the only exit from FAULT, and it goes to IDLE — never straight to RUNNING.
    /// Returns false while any protective condition is still present.
    /// </summary>
    public bool OperatorReset()
    {
        if (_state != EquipmentState.Fault)
        {
            return false;
        }

        if (PresentProtectiveConditions().Any(c => c != ProtectiveCondition.StopRequired))
        {
            return false;
        }

        // Acknowledging the reset also exits STOP_REQUIRED: that mode is entered and
        // left by the operator, not sensed.
        _stopRequired = false;
        _latched.Clear();
        _overCurrentTicks = 0;
        _badQualityTicks = 0;
        _state = EquipmentState.Idle;
        _setpointPct = 0;
        return true;
    }

    // ------------------------------------------------------------ demo-only API
    // Section 16: fault injection exists only under the demo profile. In a non-demo
    // build these throw rather than silently doing nothing, so a misconfiguration is
    // loud instead of invisible.

    public void InjectProtectiveCondition(ProtectiveCondition condition)
    {
        RequireDemoProfile();
        _injectedConditions.Add(condition);
    }

    public void ClearInjectedProtectiveConditions()
    {
        RequireDemoProfile();
        _injectedConditions.Clear();
    }

    public void InjectCommLoss()
    {
        RequireDemoProfile();
        _commLost = true;
    }

    public void RestoreComm()
    {
        RequireDemoProfile();
        _commLost = false;
    }

    /// <summary>Disables the configured single-channel sensor overlay.</summary>
    public void ClearInjectedSensorFault()
    {
        RequireDemoProfile();
        _sensorFaultActive = false;
        _frozenValue = null;
    }

    private void RequireDemoProfile()
    {
        if (!_options.DemoProfile)
        {
            throw new InvalidOperationException(
                "fault injection is available only when demoProfile is enabled (EQUIPMENT_SIMULATOR.md 16).");
        }
    }

    /// <summary>Drains buffered fault events. Bounded at 1 000, drop-oldest (§13).</summary>
    public IReadOnlyList<FaultEvent> DrainFaultEvents()
    {
        var drained = _faultEvents.ToArray();
        _faultEvents.Clear();
        return drained;
    }

    // ------------------------------------------------------------------- loop

    /// <summary>
    /// Advances the model by one 100 ms tick and returns the sample the protocol servers
    /// would publish, or <c>null</c> when the endpoint is not answering. Physics and
    /// self-protection keep running either way — losing communications does not stop
    /// a motor.
    /// </summary>
    public RawSample? Tick()
    {
        var sequence = _sequence;
        var sourceEpochMs = (uint)(_elapsedMs & 0xFFFF_FFFFUL);

        AdvanceActuation();
        AdvancePhysics();
        var reading = Measure();
        EvaluateState(reading);

        var sample = new RawSample
        {
            EquipmentId = _options.EquipmentId,
            Sequence = sequence,
            SourceEpochMs = sourceEpochMs,
            State = _state,
            TemperatureC = reading.Values[(int)SensorChannel.TemperatureC],
            VibrationRms = reading.Values[(int)SensorChannel.VibrationRms],
            CurrentA = reading.Values[(int)SensorChannel.CurrentA],
            VoltageV = reading.Values[(int)SensorChannel.VoltageV],
            Rpm = reading.Values[(int)SensorChannel.Rpm],
            TorqueNm = reading.Values[(int)SensorChannel.TorqueNm],
            OperationRatePct = reading.Values[(int)SensorChannel.OperationRatePct],
            Flags = reading.Flags,
            RawQuality = reading.Quality,
        };

        _tickCount++;
        _sequence++;
        _elapsedMs += 100;

        // §11: a sourceEpochMs wrap resets sequence at the same instant, so the two
        // signals stay consistent and a consumer cannot read the wrap as a gap.
        if (_elapsedMs % 0x1_0000_0000UL == 0)
        {
            _sequence = 0;
        }

        return _commLost || _state == EquipmentState.Offline ? null : sample;
    }

    private void AdvanceActuation()
    {
        _ticksSinceSetpointWrite++;

        // Dead-man (DEC-001). It only ever LOWERS a running drive to the safe default;
        // it never starts an idle one, because a timeout that spun up a stopped machine
        // would be a new hazard rather than a mitigation.
        if (_setpointPct > 0 && _ticksSinceSetpointWrite > _deadmanTicks)
        {
            _setpointPct = _options.SafeDefaultRatePct;
            _ticksSinceSetpointWrite = 0;
            DeadmanRevertCount++;
            Emit("DEADMAN_REVERT", $"no setpoint refresh within {_options.DeadmanTimeoutMs} ms");
        }

        var maxStep = MaxSlewPctPerSecond * TickSeconds;
        _appliedPct += Math.Clamp(_setpointPct - _appliedPct, -maxStep, maxStep);

        // The slew-limited rate the drive SHOULD have reached. With no drive-fault
        // profile in the model these are equal; the comparison exists so that a future
        // stuck-drive fault is detected rather than silently tolerated.
        _expectedPct = _appliedPct;
    }

    private void AdvancePhysics()
    {
        var elapsedSeconds = _tickCount * TickSeconds;

        _health = _options.FaultProfile == FaultProfile.BearingDegradation
            ? Math.Min(1.0, Math.Pow(elapsedSeconds / _options.FailureTimeSeconds, 2.2))
            : 0.0;

        _coolingLoss = _options.FaultProfile == FaultProfile.CoolingDegradation
            ? Math.Min(0.9, elapsedSeconds / _options.CoolingTimeSeconds)
            : 0.0;

        var r = _appliedPct / 100.0;
        var target = AmbientC + (32.0 * r * (1.0 + (0.8 * _health)) / (1.0 - (0.75 * _coolingLoss)));
        _temperatureC += (target - _temperatureC) * (TickSeconds / ThermalTauSeconds);
    }

    private readonly record struct Reading(double?[] Values, IReadOnlyList<ChannelFlag> Flags, QualityOverall Quality);

    private Reading Measure()
    {
        var r = _appliedPct / 100.0;
        var h = _health;
        var load = _options.FaultProfile == FaultProfile.Overload ? 1.6 : 1.0;

        // The noise draws happen in a fixed order, unconditionally, so that enabling a
        // sensor fault cannot shift the random sequence of the other channels.
        var nRpm = Gaussian(SensorChannel.Rpm, 1.5);
        var nTorque = Gaussian(SensorChannel.TorqueNm, 0.4);
        var nCurrent = Gaussian(SensorChannel.CurrentA, 0.06);
        var nVoltage = Gaussian(SensorChannel.VoltageV, 1.2);
        var nVibration = Gaussian(SensorChannel.VibrationRms, 0.05);

        var values = new double?[ChannelMeta.Length];
        values[(int)SensorChannel.Rpm] = (1800.0 * r) + nRpm;
        values[(int)SensorChannel.TorqueNm] = (42.0 * r * (1.0 + (0.45 * h)) * load) + nTorque;
        values[(int)SensorChannel.CurrentA] = (12.0 * r * (1.0 + (0.55 * h)) * load) + nCurrent;
        values[(int)SensorChannel.VoltageV] = 400.0 + nVoltage;
        values[(int)SensorChannel.VibrationRms] = (2.2 * (0.35 + (0.65 * r)) * (1.0 + (6.0 * h * h))) + nVibration;
        values[(int)SensorChannel.TemperatureC] = _temperatureC;
        values[(int)SensorChannel.OperationRatePct] = _appliedPct;

        // OOD: rpm is held at full speed while the rate says otherwise. Every value stays
        // inside its valid range — only the LEARNED RELATIONSHIP is violated, which is
        // exactly why a range check must not catch it (ADR-0015).
        if (_options.FaultProfile == FaultProfile.OodProfile)
        {
            values[(int)SensorChannel.Rpm] = 1800.0 + nRpm;
        }

        // Additive Gaussian noise on a channel sitting at zero would otherwise produce
        // negative rpm, current, and vibration. Those are physical floors, not sensor
        // faults, so the reading is clamped there. Only the LOWER bound is clamped: an
        // upper excursion is a real condition and must stay visible as VALUE_OUT_OF_RANGE.
        for (var i = 0; i < values.Length; i++)
        {
            if (ChannelMeta[i].Min == 0 && values[i] < 0)
            {
                values[i] = 0;
            }
        }

        var flags = new List<ChannelFlag>();
        ApplySensorOverlay(values, flags);

        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (value is null)
            {
                continue;
            }

            var meta = ChannelMeta[i];
            if (value < meta.Min || value > meta.Max)
            {
                flags.Add(new ChannelFlag((SensorChannel)i, QualityFlag.ValueOutOfRange));
            }
        }

        return new Reading(values, flags, DeriveQuality(values, flags));
    }

    private double Gaussian(SensorChannel channel, double sigma)
    {
        // SENSOR_NOISE multiplies the sigma of one channel by 8.
        var scaled = _sensorFaultActive
                     && _options.FaultProfile == FaultProfile.SensorNoise
                     && _options.FaultChannel == channel
            ? sigma * 8.0
            : sigma;

        return _random.NextGaussian(scaled);
    }

    private void ApplySensorOverlay(double?[] values, List<ChannelFlag> flags)
    {
        if (!_sensorFaultActive || _options.FaultChannel is not { } channel)
        {
            return;
        }

        var index = (int)channel;

        switch (_options.FaultProfile)
        {
            case FaultProfile.SensorDrift:
                // +0.02 x range per 60 s. Deliberately raises no flag: an undetectable
                // drift is the whole point of the profile.
                values[index] += 0.02 * ChannelMeta[index].Range * (_tickCount * TickSeconds / 60.0);
                break;

            case FaultProfile.SensorFreeze:
                _frozenValue ??= values[index];
                values[index] = _frozenValue;
                flags.Add(new ChannelFlag(channel, QualityFlag.SensorFrozen));
                break;

            case FaultProfile.SensorDropout:
                // ADR-0018: null plus a flag. Never zero, never last-known, never
                // interpolated — a fabricated reading passes the safety gates.
                values[index] = null;
                flags.Add(new ChannelFlag(channel, QualityFlag.SensorMissing));
                break;

            case FaultProfile.SensorNoise:
                // Already applied at draw time.
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Raw quality as the equipment can judge it, using the safety-required/advisory map
    /// in §4. The gateway derives the canonical <c>quality.overall</c>; this is the
    /// equipment's own view, and it is what the L3 protective logic runs on.
    /// </summary>
    private static QualityOverall DeriveQuality(double?[] values, List<ChannelFlag> flags)
    {
        var quality = QualityOverall.Good;

        for (var i = 0; i < values.Length; i++)
        {
            var channel = (SensorChannel)i;
            var impaired = values[i] is null || flags.Any(f => f.Channel == channel);
            if (!impaired)
            {
                continue;
            }

            if (ChannelMeta[i].SafetyRequired)
            {
                return QualityOverall.Bad;
            }

            quality = QualityOverall.Uncertain;
        }

        return quality;
    }

    // --------------------------------------------------------- state machine

    private void EvaluateState(Reading reading)
    {
        if (_state == EquipmentState.Offline)
        {
            return;
        }

        if (_state == EquipmentState.Connecting)
        {
            if (_commLost)
            {
                if (++_connectingTicks >= ConnectTimeoutTicks)
                {
                    _state = EquipmentState.Offline;
                }

                return;
            }

            // T2 / T3: first valid telemetry.
            _state = _appliedPct > 0 ? EquipmentState.Running : EquipmentState.Idle;
            return;
        }

        var present = UpdateAndCollectProtectiveConditions(reading);

        // T9. Evaluated in IDLE, RUNNING, and DEGRADED (§3.2). FAULT is latched, and
        // STOPPING leaves only through T6 or its own timeout.
        if (present.Count > 0
            && _state is EquipmentState.Idle or EquipmentState.Running or EquipmentState.Degraded)
        {
            foreach (var condition in present)
            {
                _latched.Add(condition);
            }

            _state = EquipmentState.Fault;
            _setpointPct = 0;
            ProtectiveTripCount++;
            Emit("PROTECTIVE_TRIP", string.Join(",", present));
            return;
        }

        switch (_state)
        {
            case EquipmentState.Idle when _appliedPct > 0:
                _state = EquipmentState.Running; // T4
                break;

            case EquipmentState.Running:
            case EquipmentState.Degraded:
                EvaluateDegradation(reading);
                break;

            case EquipmentState.Stopping when _appliedPct == 0:
                _state = EquipmentState.Idle; // T6
                break;

            case EquipmentState.Stopping:
                if (++_stoppingTicks >= StopTimeoutTicks)
                {
                    _latched.Add(ProtectiveCondition.StopRequired);
                    _state = EquipmentState.Fault;
                    Emit("STOP_TIMEOUT", $"{StopTimeoutTicks} ticks without reaching rest");
                }

                break;

            default:
                break;
        }
    }

    private List<ProtectiveCondition> UpdateAndCollectProtectiveConditions(Reading reading)
    {
        var temperature = reading.Values[(int)SensorChannel.TemperatureC];
        var vibration = reading.Values[(int)SensorChannel.VibrationRms];
        var current = reading.Values[(int)SensorChannel.CurrentA];

        var present = new List<ProtectiveCondition>();

        if (temperature > TripTemperatureC || _injectedConditions.Contains(ProtectiveCondition.OverTemperature))
        {
            present.Add(ProtectiveCondition.OverTemperature);
        }

        if (vibration > TripVibrationRms || _injectedConditions.Contains(ProtectiveCondition.OverVibration))
        {
            present.Add(ProtectiveCondition.OverVibration);
        }

        // Over-current must be SUSTAINED for more than 1 s: a momentary inrush is not a fault.
        var overCurrent = current > TripCurrentA || _injectedConditions.Contains(ProtectiveCondition.OverCurrent);
        _overCurrentTicks = overCurrent ? _overCurrentTicks + 1 : 0;
        if (_overCurrentTicks > OverCurrentSustainTicks)
        {
            present.Add(ProtectiveCondition.OverCurrent);
        }

        var bad = reading.Quality == QualityOverall.Bad
                  || _injectedConditions.Contains(ProtectiveCondition.SafetySensorBad);
        _badQualityTicks = bad ? _badQualityTicks + 1 : 0;
        if (_badQualityTicks > BadSustainTicks)
        {
            present.Add(ProtectiveCondition.SafetySensorBad);
        }

        if (_stopRequired || _injectedConditions.Contains(ProtectiveCondition.StopRequired))
        {
            present.Add(ProtectiveCondition.StopRequired);
        }

        return present;
    }

    /// <summary>Conditions present right now, used by <see cref="OperatorReset"/>.</summary>
    private IEnumerable<ProtectiveCondition> PresentProtectiveConditions()
    {
        foreach (var condition in _injectedConditions)
        {
            yield return condition;
        }

        if (_temperatureC > TripTemperatureC)
        {
            yield return ProtectiveCondition.OverTemperature;
        }

        if (_stopRequired)
        {
            yield return ProtectiveCondition.StopRequired;
        }
    }

    private void EvaluateDegradation(Reading reading)
    {
        var temperature = reading.Values[(int)SensorChannel.TemperatureC];
        var vibration = reading.Values[(int)SensorChannel.VibrationRms];

        _uncertainTicks = reading.Quality == QualityOverall.Uncertain ? _uncertainTicks + 1 : 0;

        // "The drive is not tracking its setpoint" is measured against the SLEW-LIMITED
        // expected rate, not the raw commanded value: during a legitimate slew the drive
        // is tracking, and comparing to the command would make every cold start DEGRADED
        // (EQUIPMENT_MODEL_AND_STATE.md §3.3).
        var offTarget = Math.Abs(_appliedPct - _expectedPct) > 10.0;
        _rateDeviationTicks = offTarget ? _rateDeviationTicks + 1 : 0;

        var safetyChannelImpaired = reading.Quality == QualityOverall.Bad;

        var degraded = _uncertainTicks > UncertainSustainTicks
                       || safetyChannelImpaired
                       || vibration > DegradeVibrationRms
                       || temperature > DegradeTemperatureC
                       || _rateDeviationTicks > SlewGraceTicks;

        if (degraded)
        {
            _degradedClearTicks = 0;
            if (_state == EquipmentState.Running)
            {
                _state = EquipmentState.Degraded; // T7: AI authority suspended
                Emit("DEGRADED", "degradation condition active");
            }

            return;
        }

        if (_state != EquipmentState.Degraded)
        {
            return;
        }

        // T8: 30 s anti-flapping hold. Without it a sensor oscillating around a threshold
        // would flip AI authority on and off every tick.
        if (++_degradedClearTicks >= DegradedClearHoldTicks)
        {
            _state = EquipmentState.Running;
            _degradedClearTicks = 0;
        }
    }

    private void Emit(string kind, string detail)
    {
        if (_faultEvents.Count >= FaultEventBufferCapacity)
        {
            _faultEvents.Dequeue(); // drop oldest (§13)
        }

        _faultEvents.Enqueue(new FaultEvent(
            _options.EquipmentId,
            _sequence,
            (uint)(_elapsedMs & 0xFFFF_FFFFUL),
            kind,
            detail));
    }

    // ------------------------------------------------------------- channel map

    private readonly record struct Meta(double Min, double Max, bool SafetyRequired)
    {
        public double Range => Max - Min;
    }

    /// <summary>
    /// Valid ranges from §1.2 and the safety-required map from §4, indexed by
    /// <see cref="SensorChannel"/>.
    /// </summary>
    private static readonly Meta[] ChannelMeta = BuildChannelMeta();

    private static Meta[] BuildChannelMeta()
    {
        var meta = new Meta[Enum.GetValues<SensorChannel>().Length];
        meta[(int)SensorChannel.TemperatureC] = new Meta(-20, 160, SafetyRequired: true);
        meta[(int)SensorChannel.VibrationRms] = new Meta(0, 50, SafetyRequired: true);
        meta[(int)SensorChannel.CurrentA] = new Meta(0, 40, SafetyRequired: true);
        meta[(int)SensorChannel.VoltageV] = new Meta(0, 480, SafetyRequired: false);
        meta[(int)SensorChannel.Rpm] = new Meta(0, 2200, SafetyRequired: true);
        meta[(int)SensorChannel.TorqueNm] = new Meta(-10, 150, SafetyRequired: false);
        meta[(int)SensorChannel.OperationRatePct] = new Meta(0, 100, SafetyRequired: true);
        return meta;
    }
}
