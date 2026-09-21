using Opc.Ua;

namespace Mair.EdgeGateway.Tests;

/// <summary>
/// The subscription's coherence rule, driven deterministically (COD-P2-001, COD-P2-003).
/// <para>
/// The over-the-wire coherence test can only observe what the stack happens to do on a given run,
/// and at a 100 ms cadence it rarely produces multi-value queues or discards. These cases build the
/// cycles by hand — uneven queue depths, a step straddling a cycle boundary, a value discarded from
/// one item only — because those are the situations the rule exists for.
/// </para>
/// </summary>
public sealed class OpcUaSampleAssemblerTests
{
    private static readonly DateTime T0 = new(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime Step(int n) => T0.AddMilliseconds(100 * n);

    private static DataValue Value(object value, DateTime timestamp, StatusCode? status = null)
        => new(new Variant(value), status ?? StatusCodes.Good, timestamp, timestamp);

    /// <summary>All ten nodes of one model step. The epoch encodes the step, so a stitch is visible.</summary>
    private static IEnumerable<(string Node, DataValue Value)> StepValues(int n, params string[] omit)
    {
        var t = Step(n);
        var all = new (string, DataValue)[]
        {
            ("Rpm", Value(1000.0 + n, t)),
            ("TorqueNm", Value(10.0, t)),
            ("CurrentA", Value(20.0, t)),
            ("VoltageV", Value(400.0, t)),
            ("TemperatureC", Value(60.0, t)),
            ("VibrationRms", Value(2.0 + n, t)),
            ("OperationRatePct", Value(80.0, t)),
            ("State", Value((ushort)3, t)),
            ("SequenceNo", Value((ulong)n, t)),
            ("SourceEpochMs", Value((uint)(100 * n), t)),
        };

        return all.Where(v => !omit.Contains(v.Item1));
    }

    private static void AssertCoherent(DataValue[] group)
    {
        var frame = OpcUaFrame.From(group);
        var n = (int)frame.Sequence;

        Assert.Equal((uint)(100 * n), frame.SourceEpochMs);
        Assert.Equal(1000.0 + n, frame.Values[Channel.Rpm]);
        Assert.Equal(2.0 + n, frame.Values[Channel.VibrationRms]);
        Assert.All(group, v => Assert.Equal(Step(n), v.SourceTimestamp));
    }

    [Fact]
    public void ACompleteStepIsEmittedInTheSameCycle()
    {
        var assembler = new OpcUaSampleAssembler();

        var emitted = assembler.AddCycle(StepValues(1));

        var group = Assert.Single(emitted);
        AssertCoherent(group);
    }

    [Fact]
    public void SeveralQueuedStepsInOneCycleEachAssembleFromTheirOwnStep()
    {
        var assembler = new OpcUaSampleAssembler();

        var emitted = assembler.AddCycle(StepValues(1).Concat(StepValues(2)).Concat(StepValues(3)));

        Assert.Equal([1UL, 2UL, 3UL], emitted.Select(g => OpcUaFrame.From(g).Sequence));
        Assert.All(emitted, AssertCoherent);
    }

    [Fact]
    public void AValueDiscardedFromOneItemOnlyDropsThatStep_NeverStitchesIt()
    {
        // The case the k-th pairing got wrong. Three steps queued; discard-oldest dropped step 1's
        // epoch but not its marker, so the epoch item carries two values against the marker's three.
        // Pairing by index would give marker 1 the epoch of step 2.
        var assembler = new OpcUaSampleAssembler();
        var cycle = StepValues(1, "SourceEpochMs").Concat(StepValues(2)).Concat(StepValues(3));

        var emitted = assembler.AddCycle(cycle).ToList();
        for (var i = 0; i < OpcUaSampleAssembler.HoldCycles; i++)
        {
            emitted.AddRange(assembler.AddCycle([]));
        }

        Assert.DoesNotContain(emitted, g => OpcUaFrame.From(g).Sequence == 1);
        Assert.Equal(1, assembler.IncompleteGroupsDropped);
        Assert.All(emitted, AssertCoherent);
    }

    [Fact]
    public void LaterStepsWaitBehindAHeldOne_SoSamplesLeaveInOrder()
    {
        var assembler = new OpcUaSampleAssembler();

        var first = assembler.AddCycle(StepValues(1, "VibrationRms").Concat(StepValues(2)));
        Assert.Empty(first); // step 1 may still complete, and step 2 must not overtake it

        var second = assembler.AddCycle(StepValues(1).Where(v => v.Node == "VibrationRms"));

        Assert.Equal([1UL, 2UL], second.Select(g => OpcUaFrame.From(g).Sequence));
        Assert.All(second, AssertCoherent);
    }

    [Fact]
    public void AStepStraddlingACycleBoundaryCompletesInTheNextCycle()
    {
        // The server samples items independently, so part of a step can publish one cycle late.
        var assembler = new OpcUaSampleAssembler();

        Assert.Empty(assembler.AddCycle(StepValues(1, "SourceEpochMs", "Rpm")));
        var emitted = assembler.AddCycle(StepValues(1).Where(v => v.Node is "SourceEpochMs" or "Rpm"));

        AssertCoherent(Assert.Single(emitted));
        Assert.Equal(0, assembler.IncompleteGroupsDropped);
    }

    [Fact]
    public void AMarkerWhoseStepNeverCompletesIsDroppedAfterTheHold_AndCounted()
    {
        var assembler = new OpcUaSampleAssembler();

        Assert.Empty(assembler.AddCycle(StepValues(1, "State")));
        for (var i = 1; i < OpcUaSampleAssembler.HoldCycles; i++)
        {
            Assert.Empty(assembler.AddCycle([]));
        }

        var emitted = assembler.AddCycle(StepValues(2));

        AssertCoherent(Assert.Single(emitted));
        Assert.Equal(1, assembler.IncompleteGroupsDropped);
    }

    [Fact]
    public void AValueArrivingAfterItsStepWasSettledIsDiscarded_NotAttachedToALaterStep()
    {
        var assembler = new OpcUaSampleAssembler();

        assembler.AddCycle(StepValues(1, "State"));
        for (var i = 0; i < OpcUaSampleAssembler.HoldCycles; i++)
        {
            assembler.AddCycle([]);
        }

        assembler.AddCycle(StepValues(1).Where(v => v.Node == "State"));

        Assert.Equal(1, assembler.LateValuesDiscarded);
    }

    [Fact]
    public void ABadMarkerIsNotASample()
    {
        // The equipment is not answering: every node is Bad_NoCommunication and the marker carries
        // no sequence. That is reported through the session and the next sequence, not as a record.
        var assembler = new OpcUaSampleAssembler();
        var t = Step(1);

        var cycle = OpcUaFrame.NodeIdentifiers.Select(node =>
            (node, new DataValue(Variant.Null, StatusCodes.BadNoCommunication, t, t)));

        Assert.Empty(assembler.AddCycle(cycle));
    }

    [Fact]
    public void NodeStatusIsTranslatedIntoTheFrame()
    {
        // COD-P2-011 and the dropped-status defect: an Uncertain value and a Good_LocalOverride
        // value carry their flags, and a disconnect is a disconnect, not a missing sensor.
        var t = Step(1);
        var group = StepValues(1).Select(v => v.Node switch
        {
            "TemperatureC" => Value(60.0, t, StatusCodes.UncertainLastUsableValue),
            "CurrentA" => Value(20.0, t, StatusCodes.GoodLocalOverride),
            "VibrationRms" => new DataValue(Variant.Null, StatusCodes.BadNoCommunication, t, t),
            _ => v.Value,
        }).ToArray();

        var telemetry = new TelemetryNormaliser("eq-001", "edge-gateway@test").Normalise(OpcUaFrame.From(group));

        Assert.True(telemetry.HasFlag(Channel.TemperatureC, QualityFlag.StaleReading));
        Assert.True(telemetry.HasFlag(Channel.CurrentA, QualityFlag.OutlierSuspected));
        Assert.True(telemetry.HasFlag(Channel.VibrationRms, QualityFlag.SensorDisconnected));
        Assert.False(telemetry.HasFlag(Channel.VibrationRms, QualityFlag.SensorMissing));
        Assert.Null(telemetry.VibrationRms);
        Assert.Equal(QualityOverall.Bad, telemetry.QualityOverall);
    }

    [Fact]
    public void AnUncertainReadingIsNeverReportedGood()
    {
        var t = Step(1);
        var group = StepValues(1)
            .Select(v => v.Node == "TemperatureC" ? Value(60.0, t, StatusCodes.UncertainLastUsableValue) : v.Value)
            .ToArray();

        var telemetry = new TelemetryNormaliser("eq-001", "edge-gateway@test").Normalise(OpcUaFrame.From(group));

        Assert.NotEqual(QualityOverall.Good, telemetry.QualityOverall);
    }

    [Fact]
    public void AMissingSourceTimestampIsSynthesisedAndSaysSo()
    {
        // Section 1.5: pretending the gateway clock is the source clock is what makes freshness lie.
        var group = StepValues(1)
            .Select(v => new DataValue(v.Value.WrappedValue, StatusCodes.Good, DateTime.MinValue, DateTime.MinValue))
            .ToArray();

        var telemetry = new TelemetryNormaliser("eq-001", "edge-gateway@test").Normalise(OpcUaFrame.From(group));

        Assert.True(telemetry.HasFlag(Channel.Event, QualityFlag.TimestampSynthesised));
        Assert.Equal(telemetry.IngestTimeUtc, telemetry.EventTimeUtc);
    }
}
