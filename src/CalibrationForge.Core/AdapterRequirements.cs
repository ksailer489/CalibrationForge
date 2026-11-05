using System.Collections.ObjectModel;

namespace CalibrationForge.Core;

public enum CapabilitySupport { Unknown, Supported, NotSupported }
public enum AdapterCapability
{
    PcConnectivity, StandardObdMode09, RawClassicCan11Bit, Configurable500Kbit,
    RawArbitrationIdControl, HostFlowControl, OrderedTransmission, CancellableIo, CalibrationForgeDriver
}
public enum AdapterUse { ObdIdentification, E92ReadKernelTransport }

public sealed class AdapterCapabilities
{
    private readonly IReadOnlyDictionary<AdapterCapability, CapabilitySupport> capabilities;

    public AdapterCapabilities(string name, IReadOnlyDictionary<AdapterCapability, CapabilitySupport> capabilities)
    {
        if (string.IsNullOrWhiteSpace(name)) { throw new ArgumentException("Adapter needs an identifying name.", nameof(name)); }
        ArgumentNullException.ThrowIfNull(capabilities);
        if (capabilities.Any(item => !Enum.IsDefined(item.Key) || !Enum.IsDefined(item.Value)))
        {
            throw new ArgumentException("Adapter contains an unknown capability or support state.", nameof(capabilities));
        }
        Name = name;
        this.capabilities = new ReadOnlyDictionary<AdapterCapability, CapabilitySupport>(capabilities.ToDictionary(item => item.Key, item => item.Value));
    }

    public string Name { get; }
    public CapabilitySupport Support(AdapterCapability capability)
    {
        if (!Enum.IsDefined(capability)) { throw new ArgumentOutOfRangeException(nameof(capability)); }
        return capabilities.TryGetValue(capability, out var support) ? support : CapabilitySupport.Unknown;
    }
}

public sealed record AdapterRequirement(AdapterCapability Capability, string Description);
public sealed record AdapterRequirementResult(string Requirement, CapabilitySupport Support);
public sealed record AdapterAssessment(string Name, AdapterUse Use, IReadOnlyList<AdapterRequirementResult> Results)
{
    public bool RequirementsMet => Results.All(item => item.Support == CapabilitySupport.Supported);
    public string Conclusion => RequirementsMet
        ? "Declared capabilities meet the contract; actual adapter timing, E92 compatibility, and kernel/recovery workflow remain unvalidated."
        : "Requirements are missing or unknown. PC connectivity or basic OBD support alone does not establish E92 kernel-read support.";
}

public static class AdapterRequirements
{
    public static IReadOnlyList<AdapterRequirement> For(AdapterUse use)
    {
        var common = new[]
        {
            new AdapterRequirement(AdapterCapability.PcConnectivity, "A documented PC connection/API"),
            new AdapterRequirement(AdapterCapability.CancellableIo, "Bounded, cancellable receive operations"),
            new AdapterRequirement(AdapterCapability.CalibrationForgeDriver, "An implemented CalibrationForge driver for the exact adapter")
        };
        var specific = use switch
        {
            AdapterUse.ObdIdentification => new[]
            {
                new AdapterRequirement(AdapterCapability.StandardObdMode09, "Standard OBD Mode 09 request/response support")
            },
            AdapterUse.E92ReadKernelTransport => new[]
            {
                new AdapterRequirement(AdapterCapability.RawClassicCan11Bit, "Raw classic CAN with standard 11-bit IDs"),
                new AdapterRequirement(AdapterCapability.Configurable500Kbit, "Configurable 500 kbit/s CAN"),
                new AdapterRequirement(AdapterCapability.RawArbitrationIdControl, "Transmit/receive ID control and filtering for 7E0 / 7E8"),
                new AdapterRequirement(AdapterCapability.HostFlowControl, "Direct control of ISO-TP flow-control frames without competing automatic responses"),
                new AdapterRequirement(AdapterCapability.OrderedTransmission, "Ordered transmission with validated ISO-TP pacing")
            },
            _ => throw new ArgumentOutOfRangeException(nameof(use))
        };
        return Array.AsReadOnly(common.Concat(specific).ToArray());
    }

    public static AdapterAssessment Assess(AdapterCapabilities adapter, AdapterUse use)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        return new AdapterAssessment(adapter.Name, use,
            Array.AsReadOnly(For(use).Select(requirement => new AdapterRequirementResult(requirement.Description,
                adapter.Support(requirement.Capability))).ToArray()));
    }
}
