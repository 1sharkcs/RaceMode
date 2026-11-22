using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace RaceMode
{
    // Switches on HWiNFO's sharing for Race Mode, in HWiNFO's own per-user settings (HKCU, no admin needed):
    //  - Sensors\EnableVSB = 1: "Enable reporting to Gadget" (the setting that shares readings at all);
    //  - Sensors\<sensor>\<reading>\InVSB = 1 for the CPU package temperature HWiNFO shows on Intel CPUs
    //    ("CPU [#0]: DTS" -> Temp510 CPU Package, Temp511 Core Max; "CPU [#0]: Enhanced" -> Temp4 CPU Package).
    // HWiNFO reads these when it starts and writes its own copy back when it exits, so this must only run while
    // HWiNFO is closed. Readings it already reports are left exactly as they are.
    public static class HwinfoSetup
    {
        // Tests (RACEMODE_TEST_ROOT) use a separate key, never HWiNFO's real settings.
        public static string KeyPath
        {
            get { return string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RACEMODE_TEST_ROOT")) ? @"Software\HWiNFO64" : @"Software\RaceModeTest\HWiNFO64"; }
        }

        static readonly string[][] CpuReadings = {
            new[] { "F0000400_0", "Temp510" },   // Intel DTS: CPU Package
            new[] { "F0000400_0", "Temp511" },   // Intel DTS: Core Max
            new[] { "F0002A00_0", "Temp4" },     // Intel Enhanced: CPU Package
        };

        public static bool SharingOn()
        {
            try
            {
                using (var s = Registry.CurrentUser.OpenSubKey(KeyPath + @"\Sensors"))
                    return s != null && Convert.ToInt32(s.GetValue("EnableVSB", 0)) == 1;
            }
            catch { return false; }
        }

        // Returns how many readings were newly switched on. Throws if HKCU can't be written.
        public static int Apply()
        {
            using (var s = Registry.CurrentUser.CreateSubKey(KeyPath + @"\Sensors"))
            {
                s.SetValue("EnableVSB", 1, RegistryValueKind.DWord);
                // New readings go after every one HWiNFO already shares (VSBidx is their order in the shared list).
                int next = 0;
                foreach (var sensor in s.GetSubKeyNames())
                    using (var sk = s.OpenSubKey(sensor))
                        if (sk != null)
                            foreach (var reading in sk.GetSubKeyNames())
                                using (var rk = sk.OpenSubKey(reading))
                                {
                                    object v = rk == null ? null : rk.GetValue("VSBidx");
                                    if (v is int && (int)v >= next) next = (int)v + 1;
                                }
                int added = 0;
                foreach (var r in CpuReadings)
                    using (var k = s.CreateSubKey(r[0] + "\\" + r[1]))
                    {
                        object on = k.GetValue("InVSB");
                        if (on is int && (int)on == 1) continue;
                        k.SetValue("InVSB", 1, RegistryValueKind.DWord);
                        if (!(k.GetValue("VSBidx") is int)) k.SetValue("VSBidx", next++, RegistryValueKind.DWord);
                        if (k.GetValue("Color") == null) k.SetValue("Color", 0x808080, RegistryValueKind.DWord);
                        added++;
                    }
                return added;
            }
        }
    }
}
