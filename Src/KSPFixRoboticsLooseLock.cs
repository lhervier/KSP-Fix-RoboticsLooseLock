using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace com.github.lhervier.ksp.fix.roboticslooselock
{
    /// <summary>
    /// Installs the fix the settings turn on, once, when KSP starts: from then on, a robotic part whose motor drive was
    /// zeroed during on-rails time warp gets it back as soon as that warp is over.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class KSPFixRoboticsLooseLock : MonoBehaviour
    {
        private const string HarmonyId = "com.github.lhervier.ksp.fix.roboticslooselock";

        private void Start()
        {
            Settings.Load();
            string version = typeof(KSPFixRoboticsLooseLock).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            Log.Info($"Version {version}, " +
                $"restoreDriveAfterWarp {Settings.RestoreDriveAfterWarp}, log level {Log.Level}");
            if (!Settings.RestoreDriveAfterWarp)
            {
                return;
            }
            try
            {
                RestoreDriveAfterWarp.Install(new Harmony(HarmonyId));
                Log.Info("Restore drive after warp fix installed");
            }
            catch (Exception e)
            {
                Log.Error("Could not install the restore drive after warp fix, a locked robotic part can still go " +
                    $"limp after on-rails time warp, as without it: {e}");
            }
        }
    }
}
