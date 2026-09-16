using Opc.Ua;
using Opc.Ua.Server;

namespace Mair.EquipmentSimulator.Protocols;

/// <summary>
/// The simulator's OPC UA address space, per <c>OT_PROTOCOL_MAPPING.md</c> §1.2.
/// <para>
/// Ten nodes per equipment under <c>Objects/Equipment/{equipmentId}</c>, of which
/// <b>exactly one is writable</b>: <c>RateSetpoint</c>. That is the OPC UA-level expression of
/// ADR-0002 and FR-035 — there is physically nothing else for a rogue writer to write.
/// </para>
/// </summary>
public sealed class MairNodeManager : CustomNodeManager2
{
    /// <summary>Resolved by URI at session start; never hard-coded to an index (§1.1).</summary>
    public const string NamespaceUri = "urn:mair:equipment:v1";

    private readonly IEquipmentAccess _equipment;
    private readonly IReadOnlyList<string> _equipmentIds;
    private readonly Dictionary<string, EquipmentNodes> _nodes = [];

    public MairNodeManager(
        IServerInternal server,
        ApplicationConfiguration configuration,
        IEquipmentAccess equipment,
        IReadOnlyList<string> equipmentIds)
        : base(server, configuration, NamespaceUri)
    {
        ArgumentNullException.ThrowIfNull(equipment);
        ArgumentNullException.ThrowIfNull(equipmentIds);

        _equipment = equipment;
        _equipmentIds = equipmentIds;
    }

    private sealed class EquipmentNodes
    {
        public required int Index { get; init; }
        public required BaseDataVariableState Rpm { get; init; }
        public required BaseDataVariableState TorqueNm { get; init; }
        public required BaseDataVariableState CurrentA { get; init; }
        public required BaseDataVariableState VoltageV { get; init; }
        public required BaseDataVariableState TemperatureC { get; init; }
        public required BaseDataVariableState VibrationRms { get; init; }
        public required BaseDataVariableState OperationRatePct { get; init; }
        public required BaseDataVariableState State { get; init; }
        public required BaseDataVariableState SequenceNo { get; init; }
        public required BaseDataVariableState RateSetpoint { get; init; }
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            var root = new FolderState(null)
            {
                NodeId = new NodeId("Equipment", NamespaceIndex),
                BrowseName = new QualifiedName("Equipment", NamespaceIndex),
                DisplayName = "Equipment",
                TypeDefinitionId = ObjectTypeIds.FolderType,
                EventNotifier = EventNotifiers.None,
            };

            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
            {
                externalReferences[ObjectIds.ObjectsFolder] = references = [];
            }

            root.AddReference(ReferenceTypeIds.Organizes, isInverse: true, ObjectIds.ObjectsFolder);
            references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, isInverse: false, root.NodeId));

            for (var index = 0; index < _equipmentIds.Count; index++)
            {
                AddEquipment(root, _equipmentIds[index], index);
            }

            AddPredefinedNode(SystemContext, root);
        }
    }

    private void AddEquipment(FolderState root, string equipmentId, int index)
    {
        var folder = new FolderState(root)
        {
            NodeId = new NodeId($"Eq.{equipmentId}", NamespaceIndex),
            BrowseName = new QualifiedName(equipmentId, NamespaceIndex),
            DisplayName = equipmentId,
            TypeDefinitionId = ObjectTypeIds.FolderType,
        };

        root.AddChild(folder);

        var setpoint = Variable(folder, equipmentId, "RateSetpoint", "OperationRateSetpointPct",
            DataTypeIds.Double, writable: true);

        // The only writable node in the entire address space. The handler rejects an out-of-range
        // value with Bad_OutOfRange rather than clamping it: clamping would mask a control-path
        // defect that the Control Service bounds check should have caught first
        // (EQUIPMENT_SIMULATOR.md §11).
        // OnSimpleWriteValue, not OnWriteValue: the simple form receives the value itself, which
        // is what a range check needs. The full form also carries index range, data encoding,
        // status and timestamp, none of which this validation uses.
        setpoint.OnSimpleWriteValue = (ISystemContext context, NodeState node, ref object value)
            => WriteSetpoint(index, ref value);

        _nodes[equipmentId] = new EquipmentNodes
        {
            Index = index,
            Rpm = Variable(folder, equipmentId, "Rpm", "Rpm", DataTypeIds.Double, writable: false),
            TorqueNm = Variable(folder, equipmentId, "TorqueNm", "TorqueNm", DataTypeIds.Double, writable: false),
            CurrentA = Variable(folder, equipmentId, "CurrentA", "CurrentA", DataTypeIds.Double, writable: false),
            VoltageV = Variable(folder, equipmentId, "VoltageV", "VoltageV", DataTypeIds.Double, writable: false),
            TemperatureC = Variable(folder, equipmentId, "TemperatureC", "TemperatureC", DataTypeIds.Double, writable: false),
            VibrationRms = Variable(folder, equipmentId, "VibrationRms", "VibrationRms", DataTypeIds.Double, writable: false),
            OperationRatePct = Variable(folder, equipmentId, "OperationRatePct", "OperationRatePct", DataTypeIds.Double, writable: false),
            State = Variable(folder, equipmentId, "State", "EquipmentState", DataTypeIds.UInt16, writable: false),
            SequenceNo = Variable(folder, equipmentId, "SequenceNo", "SequenceNo", DataTypeIds.UInt64, writable: false),
            RateSetpoint = setpoint,
        };

        Refresh(equipmentId);
    }

    private ServiceResult WriteSetpoint(int index, ref object value)
    {
        if (value is not double rate || !double.IsFinite(rate))
        {
            return StatusCodes.BadTypeMismatch;
        }

        var result = _equipment.WriteSetpoint(index, rate);

        return result switch
        {
            SetpointResult.Accepted => ServiceResult.Good,

            // Never clamped. The contract names this status code specifically (§11).
            SetpointResult.RejectedOutOfRange => StatusCodes.BadOutOfRange,

            // The value was fine; the equipment refused on state - FAULT, STOPPING, OFFLINE.
            SetpointResult.RejectedState => StatusCodes.BadNotWritable,
            _ => StatusCodes.BadInternalError,
        };
    }

    private BaseDataVariableState Variable(
        NodeState parent, string equipmentId, string identifier, string browseName,
        NodeId dataType, bool writable)
    {
        var node = new BaseDataVariableState(parent)
        {
            // String identifiers exactly as the contract writes them: ns=<n>;s=Eq.{id}.<Field>.
            NodeId = new NodeId($"Eq.{equipmentId}.{identifier}", NamespaceIndex),
            BrowseName = new QualifiedName(browseName, NamespaceIndex),
            DisplayName = browseName,
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            ReferenceTypeId = ReferenceTypeIds.Organizes,
            DataType = dataType,
            ValueRank = ValueRanks.Scalar,
            AccessLevel = writable ? AccessLevels.CurrentReadOrWrite : AccessLevels.CurrentRead,
            UserAccessLevel = writable ? AccessLevels.CurrentReadOrWrite : AccessLevels.CurrentRead,
            MinimumSamplingInterval = 50, // §1.3, Nyquist-safe against the 100 ms publish
            Historizing = false,
        };

        parent.AddChild(node);
        return node;
    }

    /// <summary>
    /// Publishes the latest sample into the address space.
    /// <para>
    /// A <c>null</c> measurement becomes <c>Bad_OutOfService</c> with a null value, never a
    /// substituted number — OPC UA can carry "no trustworthy value" natively, and the gateway maps
    /// that status back to <c>SENSOR_MISSING</c> + <c>null</c> (§1.4, ADR-0018).
    /// </para>
    /// </summary>
    public void Refresh(string equipmentId)
    {
        lock (Lock)
        {
            if (!_nodes.TryGetValue(equipmentId, out var nodes))
            {
                return;
            }

            var sample = _equipment.Latest(nodes.Index);
            var now = DateTime.UtcNow;

            if (sample is null)
            {
                // The equipment is not answering at all.
                foreach (var node in All(nodes))
                {
                    Set(node, null, StatusCodes.BadNoCommunication, now);
                }

                return;
            }

            Set(nodes.Rpm, sample.Rpm, now);
            Set(nodes.TorqueNm, sample.TorqueNm, now);
            Set(nodes.CurrentA, sample.CurrentA, now);
            Set(nodes.VoltageV, sample.VoltageV, now);
            Set(nodes.TemperatureC, sample.TemperatureC, now);
            Set(nodes.VibrationRms, sample.VibrationRms, now);
            Set(nodes.OperationRatePct, sample.OperationRatePct, now);
            Set(nodes.State, (ushort)ModbusRegisterEncoder.StateCode(sample.State), StatusCodes.Good, now);
            Set(nodes.SequenceNo, sample.Sequence, StatusCodes.Good, now);
            Set(nodes.RateSetpoint, sample.OperationRatePct ?? 0d, StatusCodes.Good, now);
        }
    }

    public void RefreshAll()
    {
        foreach (var id in _equipmentIds)
        {
            Refresh(id);
        }
    }

    private static IEnumerable<BaseDataVariableState> All(EquipmentNodes n)
    {
        yield return n.Rpm;
        yield return n.TorqueNm;
        yield return n.CurrentA;
        yield return n.VoltageV;
        yield return n.TemperatureC;
        yield return n.VibrationRms;
        yield return n.OperationRatePct;
        yield return n.State;
        yield return n.SequenceNo;
    }

    private static void Set(BaseDataVariableState node, double? value, DateTime timestamp)
        => Set(node, value, value is null ? StatusCodes.BadOutOfService : StatusCodes.Good, timestamp);

    private static void Set(BaseDataVariableState node, object? value, StatusCode status, DateTime timestamp)
    {
        node.Value = value;
        node.StatusCode = status;
        node.Timestamp = timestamp;
        node.ClearChangeMasks(null, includeChildren: false);
    }
}
