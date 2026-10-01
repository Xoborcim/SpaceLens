using SpaceLens.Core.Classification;
using SpaceLens.Core.Models;

namespace SpaceLens.Detectors.VirtualMachines;

/// <summary>
/// Virtual machine disks (VMware .vmdk, VirtualBox .vdi, Hyper-V .vhd/.vhdx, QEMU .qcow2) and memory
/// snapshots. WSL and Docker disks are reported by the developer detector instead.
/// </summary>
public sealed class VirtualMachineDetector : DetectorBase
{
    private const long MinimumSize = 100L << 20;

    public override string Id => "vm";

    public override string DisplayName => "Virtual machines";

    public override IEnumerable<StorageFinding> Detect(DetectionContext context, CancellationToken cancellationToken)
    {
        var tree = context.Tree;
        int count = tree.FileRecordCount;
        for (int i = 0; i < count; i++)
        {
            if (!tree.IsLiveFile(i))
            {
                continue;
            }

            ref var file = ref tree.File(i);
            if (file.Category != FileCategory.VirtualMachine || file.Size < MinimumSize)
            {
                continue;
            }

            string name = file.Name;
            if (name.Equals("ext4.vhdx", StringComparison.OrdinalIgnoreCase) || name.StartsWith("docker_data", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("swap.vhdx", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string folder = tree.Dir(file.Directory).Name;
            yield return FileFinding(context, i, "Virtual machines", $"{folder} – {name}", StorageNature.VirtualDisk, LocationCategory.VirtualMachines,
                "Virtual machine disk. It contains the complete file system of a virtual machine, including any files saved inside it.",
                "Remove unused virtual machines from their manager (VMware, VirtualBox, Hyper-V Manager) so that its configuration is cleaned up too.",
                isOpportunity: true);
        }
    }
}
