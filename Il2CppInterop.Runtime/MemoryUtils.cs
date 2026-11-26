using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Il2CppInterop.Common.XrefScans;

namespace Il2CppInterop.Runtime;

internal class MemoryUtils
{
    public static unsafe nint FindSignatureInModule(ProcessModule module, SignatureDefinition sigDef)
    {
        // On newer Unity (6000.x) the loaded GameAssembly maps some pages PAGE_NOACCESS / guard pages; the raw
        // linear byte walk in FindSignatureInBlock dereferences them and throws a fatal AccessViolationException.
        // Use VirtualQuery to enumerate the module's regions and scan only the readable committed ones, skipping
        // the rest -- without ever modifying page protections. VirtualQuery and the TerraFX MEMORY_BASIC_INFORMATION
        // fields are Windows-only (the struct is annotated for Windows 6.1+); elsewhere (where this guard-page issue
        // does not arise) fall back to the plain whole-module scan.
        nint ptr = 0;
        if (OperatingSystem.IsWindowsVersionAtLeast(6, 1))
        {
            // Protections that permit reading; there is no single composite constant for these.
            const uint pageReadable = PageReadonly | PageReadwrite | PageWritecopy |
                                      PageExecuteRead | PageExecuteReadwrite | PageExecuteWritecopy;
            var regions = GetModuleRegions(module);
            foreach (var region in regions)
            {
                if (region.State != MemCommit || (region.Protect & PageGuard) != 0 ||
                    (region.Protect & pageReadable) == 0)
                    continue;
                ptr = FindSignatureInBlock((nint)region.BaseAddress, (long)region.RegionSize,
                    sigDef.pattern, sigDef.mask, sigDef.offset);
                if (ptr != 0)
                    break;
            }
        }
        else
        {
            ptr = FindSignatureInBlock(module.BaseAddress, module.ModuleMemorySize,
                sigDef.pattern, sigDef.mask, sigDef.offset);
        }

        if (ptr != 0 && sigDef.xref)
            ptr = XrefScannerLowLevel.JumpTargets(ptr).FirstOrDefault();
        return ptr;
    }

    public static nint FindSignatureInBlock(nint block, long blockSize, string pattern, string mask, long sigOffset = 0)
    {
        return FindSignatureInBlock(block, blockSize, pattern.ToCharArray(), mask.ToCharArray(), sigOffset);
    }

    public static unsafe nint FindSignatureInBlock(nint block, long blockSize, char[] pattern, char[] mask,
        long sigOffset = 0)
    {
        // Stop at blockSize - mask.Length so the inner read (block + address + mask.Length - 1) never passes the
        // end of this block. When the caller scans per-region (Unity 6 readable regions interleaved with guard /
        // PAGE_NOACCESS pages), an overread off the tail would fault the adjacent page -> a fatal
        // AccessViolationException that aborts the chainloader. If the block is smaller than the mask, scan nothing.
        for (long address = 0; address <= blockSize - mask.Length; address++)
        {
            var found = true;
            for (uint offset = 0; offset < mask.Length; offset++)
                if (*(byte*)(address + block + offset) != (byte)pattern[offset] && mask[offset] != '?')
                {
                    found = false;
                    break;
                }

            if (found)
                return (nint)(address + block + sigOffset);
        }

        return 0;
    }

    /// <summary>
    /// Walks the module's address space via <c>VirtualQuery</c>, collecting each memory region so the scan can pick
    /// the readable committed ones. Stops at the first <c>VirtualQuery</c> failure or once the module end is reached.
    /// </summary>
    [SupportedOSPlatform("windows6.1")]
    internal static unsafe List<MemoryBasicInformation> GetModuleRegions(ProcessModule module)
    {
        var regions = new List<MemoryBasicInformation>();
        var moduleEndAddress = (long)module.BaseAddress + module.ModuleMemorySize;
        var currentAddress = (long)module.BaseAddress;
        while (currentAddress < moduleEndAddress)
        {
            MemoryBasicInformation memoryInfo;
            var result = VirtualQuery((void*)currentAddress, &memoryInfo, (nuint)sizeof(MemoryBasicInformation));
            if (result == 0)
                break; // error, or reached the end of the module's mapped memory

            regions.Add(memoryInfo);
            currentAddress = (long)memoryInfo.BaseAddress + (long)memoryInfo.RegionSize;
        }

        return regions;
    }

    // MEMORY_BASIC_INFORMATION and VirtualQuery are declared locally instead of pulling in TerraFX.Interop.Windows:
    // this file only needs those two symbols, and TerraFX is a 17 MB dependency that has to be shipped next to the
    // runtime otherwise. Layout matches the 64-bit Windows definition (sizeof == 48).
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct MemoryBasicInformation
    {
        public void* BaseAddress;
        public void* AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe nuint VirtualQuery(void* lpAddress, MemoryBasicInformation* lpBuffer, nuint dwLength);

    private const uint MemCommit = 0x1000;
    private const uint PageReadonly = 0x02;
    private const uint PageReadwrite = 0x04;
    private const uint PageWritecopy = 0x08;
    private const uint PageExecuteRead = 0x20;
    private const uint PageExecuteReadwrite = 0x40;
    private const uint PageExecuteWritecopy = 0x80;
    private const uint PageGuard = 0x100;

    public struct SignatureDefinition
    {
        public string pattern;
        public string mask;
        public int offset;
        public bool xref;
    }
}
