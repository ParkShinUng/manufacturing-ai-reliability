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

    // Fault severities, raised on 2026-09-15 when OD-002 was resolved. Before that, two of the three
    // sensed protective conditions could not be reached by any profile and the third had 0.46 degC
    // of margin, so the innermost safety layer was unexercisable. The protective THRESHOLDS below
    // were deliberately left alone - the fault model was what could not produce a dangerous machine.
    private const double VibrationHealthCoupling = 12.0;  // was 6.0  -> worst case 28.6 mm/s
    private const double OverloadLoadFactor = 2.8;        // was 1.6  -> worst case 33.6 A
    private const double MaxCoolingLoss = 0.95;           // was 0.9  -> T_target 133.3 degC

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

    /// <summary>
    /// Conditions found present on the most recent tick. <see cref="OperatorReset"/> reads THIS
    /// rather than re-deriving its own set: two independent evaluations of "is the machine safe"
    /// will drift, and the one guarding the reset is the one that must not be weaker.
    /// </summary>
    private IReadOnlyList<ProtectiveCondition> _presentConditions = [];

    // EQUIPMENT_SIMULATOR.md 17. Every label here is an enum, so the label set is bounded and
    // equipmentId is never one (CODING_STANDARDS.md). These are in-process counters, not an
    // exporter: exporting them needs a metrics library, which is a dependency decision and so
    // belongs to the host in Phase 2.
    private readonly Dictionary<EquipmentState, long> _stateTicks = [];
    private readonly Dictionary<ProtectiveCondition, int> _protectiveTrips = [];
    private readonly Dictionary<SetpointResult, int> _setpointWrites = [];
    private readonly Dictionary<FaultProfile, int> _faultInjections = [];

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

        // COD-VFY-002, accepted in full at round 3. AC-018 requires each PROFILE to produce its
        // documented signature, and §2's model for COMM_LOSS is "protocol endpoint stops
        // responding". Gating the injection was not enough: the profile has to act by itself.
        // Onset control - disconnect mid-run, then restore - is AC-002, which is Phase 2; the demo
        // API stays for that cycle.
        _commLost = options.FaultProfile == FaultProfile.CommLoss;
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

    /// <summary><c>fault_injections_total{profile}</c>, labelled by the equipment's configured profile.</summary>
    public IReadOnlyDictionary<FaultProfile, int> FaultInjectionsTotal => _faultInjections;

    /// <summary>Ticks spent in each state - the bounded-label form of `equipment_state{state}`.</summary>
    public IReadOnlyDictionary<EquipmentState, long> StateTicks => _stateTicks;

    /// <summary><c>protective_trips_total{condition}</c>.</summary>
    public IReadOnlyDictionary<ProtectiveCondition, int> ProtectiveTripsTotal => _protectiveTrips;

    /// <summary><c>setpoint_writes_total{result}</c> - rejections are counted, not just successes.</summary>
    public IReadOnlyDictionary<SetpointResult, int> SetpointWritesTotal => _setpointWrites;

    private static void Count<TKey>(Dictionary<TKey, int> counter, TKey key) where TKey : notnull
        => counter[key] = counter.TryGetValue(key, out var n) ? n + 1 : 1;

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
            Count(_setpointWrites, SetpointResult.RejectedOutOfRange);
            return SetpointResult.RejectedOutOfRange;
        }

        if (_state is not (EquipmentState.Idle or EquipmentState.Running or EquipmentState.Degraded))
        {
            Count(_setpointWrites, SetpointResult.RejectedState);
            return SetpointResult.RejectedState;
        }

        Count(_setpointWrites, SetpointResult.Accepted);

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

        // T10: "operator reset AND all protective conditions clear". STOP_REQUIRED is excluded
        // because it is entered and left by the operator, not sensed — acknowledging the reset is
        // what exits it. Every sensed condition still blocks.
        // COD-R2-002: _presentConditions is last tick's sensed evaluation, and a demo injection
        // can arrive between ticks, so the injected set is unioned in synchronously. The remaining
        // asymmetry is deliberate: stale state may only cause a reset to be REFUSED (until the next
        // tick re-evaluates), never granted.
        var blocking = _presentConditions
            .Concat(_injectedConditions)
            .Any(c => c != ProtectiveCondition.StopRequired);

        if (blocking)
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
        Count(_faultInjections, _options.FaultProfile);
    }

    public void ClearInjectedProtectiveConditions()
    {
        RequireDemoProfile();
        _injectedConditions.Clear();
    }

    /// <summary>
    /// Stops the protocol endpoint answering. Requires <see cref="FaultProfile.CommLoss"/>:
    /// the profile is what makes the fault possible, injection is only what makes it happen
    /// (EQUIPMENT_SIMULATOR.md §5). Without the gate the profile was inert and its AC-018 test was
    /// really testing the injection API.
    /// </summary>
    public void InjectCommLoss()
    {
        RequireDemoProfile();

        if (_options.FaultProfile != FaultProfile.CommLoss)
        {
            throw new InvalidOperationException(
                $"COMM_LOSS can only be injected into equipment configured with the CommLoss fault profile; this one is {_options.FaultProfile}.");
        }

        _commLost = true;
        Count(_faultInjections, _options.FaultProfile);
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

        _stateTicks[_state] = _stateTicks.TryGetValue(_state, out var ticks) ? ticks + 1 : 1;

        _tickCount++;
        _sequence++;

        var previousElapsedMs = _elapsedMs;
        _elapsedMs += 100;

        // §11: a sourceEpochMs wrap resets sequence at the same instant, so the two signals stay
        // consistent and a consumer cannot read the wrap as a gap.
        if (EpochWrapped(previousElapsedMs, _elapsedMs))
        {
            _sequence = 0;
        }

        return _commLost || _state == EquipmentState.Offline ? null : sample;
    }

    /// <summary>
    /// True when the 32-bit <c>sourceEpochMs</c> view of elapsed milliseconds rolls over between
    /// two consecutive ticks.
    /// <para>
    /// Testing this through <see cref="Tick"/> would take 43 billion calls, so it is a pure
    /// function with its own test. The obvious formulation — <c>elapsed % 2^32 == 0</c> — is
    /// <b>wrong</b> at a 100 ms cadence: elapsed is always a multiple of 100, 2^32 is not, and the
    /// two only coincide at LCM(2^32, 100) = 107 374 182 400 ms. Sequence would have reset on every
    /// twenty-fifth wrap, leaving the other twenty-four looking like an unexplained gap.
    /// </para>
    /// </summary>
    internal static bool EpochWrapped(ulong beforeMs, ulong afterMs)
        => (uint)(afterMs & 0xFFFF_FFFFUL) < (uint)(beforeMs & 0xFFFF_FFFFUL);

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

        // Two independent integrators. _expectedPct is the slew-limited response the drive OWES the
        // setpoint; _appliedPct is what it actually did. Deriving one from the other makes the
        // §3.3 tracking check a tautology that can never fire (COD-VFY-004).
        _expectedPct += Math.Clamp(_setpointPct - _expectedPct, -maxStep, maxStep);

        // COD-R2-001: drive lag is a DEMO hook, and a demo hook must never be able to block the
        // safety path. A protective trip cuts the drive, so in FAULT the applied rate slews to zero
        // whatever the injection says - otherwise "FAULT: rate forced 0" would be defeatable from
        // outside L3, which is the one property AC-020 exists to guarantee.
        // DRIVE_STUCK (OD-001): from T_stuck the drive ignores its setpoint. COD-R2-001 still
        // applies - a stuck drive must not be able to survive a protective trip, so FAULT overrides
        // the profile. That is physical too: the trip cuts the drive.
        var stuck = _options.FaultProfile == FaultProfile.DriveStuck
                    && _tickCount * TickSeconds >= _options.StuckTimeSeconds;

        if (!stuck || _state == EquipmentState.Fault)
        {
            _appliedPct += Math.Clamp(_setpointPct - _appliedPct, -maxStep, maxStep);
        }
    }

    private void AdvancePhysics()
    {
        var elapsedSeconds = _tickCount * TickSeconds;

        _health = _options.FaultProfile == FaultProfile.BearingDegradation
            ? Math.Min(1.0, Math.Pow(elapsedSeconds / _options.FailureTimeSeconds, 2.2))
            : 0.0;

        _coolingLoss = _options.FaultProfile == FaultProfile.CoolingDegradation
            ? Math.Min(MaxCoolingLoss, elapsedSeconds / _options.CoolingTimeSeconds)
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
        var load = _options.FaultProfile == FaultProfile.Overload ? OverloadLoadFactor : 1.0;

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
        values[(int)SensorChannel.VibrationRms] = (2.2 * (0.35 + (0.65 * r)) * (1.0 + (VibrationHealthCoupling * h * h))) + nVibration;
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
        _presentConditions = present;

        // T9, including STOPPING since 2026-09-15: §3.4 says any protective condition is sufficient
        // for FAULT, and a machine slewing down through an over-temperature condition must still
        // latch rather than coast into IDLE unacknowledged. FAULT itself stays latched.
        if (present.Count > 0
            && _state is EquipmentState.Idle or EquipmentState.Running or EquipmentState.Degraded
                      or EquipmentState.Stopping)
        {
            foreach (var condition in present)
            {
                _latched.Add(condition);
            }

            _state = EquipmentState.Fault;
            _setpointPct = 0;
            ProtectiveTripCount++;
            foreach (var condition in present)
            {
                Count(_protectiveTrips, condition);
            }

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
