using System;
using System.Collections.Generic;
using System.Reflection;
using Expansions.Serenity;
using HarmonyLib;
using UnityEngine;

namespace com.github.lhervier.ksp.fix.roboticslooselock
{
    /// <summary>
    /// Gives a robotic part (piston, hinge, rotation servo, rotor) its motor drive back once on-rails time warp is
    /// over. The OnModifyServo override of each of them zeroes the drive of its joint (spring, damper, maximum force)
    /// when it runs during on-rails warp, and nothing configures it again when the warp ends: a locked servo, held by
    /// that drive only, stays limp. OnModifyServo runs during warp when a vessel loads (Part.Start), or when a setting
    /// of the servo changes.
    /// </summary>
    internal static class RestoreDriveAfterWarp
    {
        // Each stock servo declares its own override. Patching OnModifyServo rather than BaseServo.ModifyServo also
        // catches BaseServo.CalculateResourceDrain, which calls OnModifyServo directly.
        private static readonly Type[] ServoTypes =
        {
            typeof(ModuleRoboticServoPiston),
            typeof(ModuleRoboticServoHinge),
            typeof(ModuleRoboticRotationServo),
            typeof(ModuleRoboticServoRotor)
        };

        /// <summary>BaseServo.ModifyServo(object) is protected.</summary>
        private static readonly MethodInfo ModifyServoMethod =
            AccessTools.DeclaredMethod(typeof(BaseServo), "ModifyServo", new[] { typeof(object) });

        private static Action<BaseServo, object> modifyServo;

        /// <summary>Servos whose drive was zeroed during the current on-rails warp.</summary>
        private static readonly HashSet<BaseServo> zeroed = new HashSet<BaseServo>();
        private static readonly List<BaseServo> buffer = new List<BaseServo>();

        /// <summary>Whether the patches are installed; <see cref="DriveRestorer"/> does nothing otherwise.</summary>
        internal static bool Installed { get; private set; }

        /// <summary>Patches the OnModifyServo override of the four stock servos.</summary>
        /// <exception cref="MissingMemberException">
        /// One of the servos does not declare OnModifyServo, or BaseServo has no ModifyServo(object).
        /// </exception>
        public static void Install(Harmony harmony)
        {
            if (ModifyServoMethod == null)
            {
                throw new MissingMemberException("BaseServo.ModifyServo(object)");
            }
            var targets = new List<MethodInfo>();
            foreach (Type type in ServoTypes)
            {
                MethodInfo method = AccessTools.DeclaredMethod(type, nameof(BaseServo.OnModifyServo));
                if (method == null)
                {
                    throw new MissingMemberException($"{type.Name}.OnModifyServo");
                }
                targets.Add(method);
            }
            modifyServo = AccessTools.MethodDelegate<Action<BaseServo, object>>(ModifyServoMethod);
            var prefix = new HarmonyMethod(typeof(RestoreDriveAfterWarp), nameof(OnModifyServoPrefix));
            foreach (MethodInfo target in targets)
            {
                harmony.Patch(target, prefix: prefix);
            }
            Installed = true;
        }

        /// <summary>True while the stock OnModifyServo overrides take their "zero the drive" branch.</summary>
        private static bool InOnRailsWarp()
        {
            // Same test as the stock OnModifyServo overrides.
            return TimeWarp.CurrentRateIndex > 0 && TimeWarp.WarpMode == TimeWarp.Modes.HIGH;
        }

        /// <summary>Remembers a servo whose drive is about to be zeroed by the on-rails warp.</summary>
        private static void OnModifyServoPrefix(BaseServo __instance)
        {
            if (!HighLogic.LoadedSceneIsFlight || !InOnRailsWarp())
            {
                return;
            }
            if (zeroed.Add(__instance) && Log.IsDebugEnabled)
            {
                Log.Debug($"Drive of {Describe(__instance)} zeroed by on-rails warp " +
                    $"(rate index {TimeWarp.CurrentRateIndex})");
            }
        }

        /// <summary>
        /// Once on-rails warp is over, configures again the drive of every servo zeroed during it, as a change in its
        /// part action window would. Does nothing while the warp lasts or when no servo was zeroed.
        /// </summary>
        internal static void RestoreIfWarpOver()
        {
            if (zeroed.Count == 0 || InOnRailsWarp())
            {
                return;
            }
            // Copied first: ModifyServo -> OnModifyServo -> the prefix must not change the set being walked.
            buffer.Clear();
            buffer.AddRange(zeroed);
            zeroed.Clear();
            int restored = 0;
            foreach (BaseServo servo in buffer)
            {
                // Unity null: the part may have been destroyed or unloaded since.
                if (servo == null || servo.part == null || servo.part.State == PartStates.DEAD)
                {
                    continue;
                }
                try
                {
                    modifyServo(servo, null);
                    restored++;
                    if (Log.IsDebugEnabled)
                    {
                        Log.Debug($"Drive of {Describe(servo)} configured again after warp " +
                            $"(packed {servo.part.packed}, locked {servo.servoIsLocked}, " +
                            $"engaged {servo.servoMotorIsEngaged})");
                    }
                }
                catch (Exception e)
                {
                    Log.Error($"Could not configure the drive of {Describe(servo)} again: {e}");
                }
            }
            buffer.Clear();
            if (restored > 0)
            {
                Log.Info($"Drive of {restored} robotic part(s) configured again after on-rails warp");
            }
        }

        /// <summary>Forgets every servo: called when the flight scene ends, which destroys them all.</summary>
        internal static void Forget()
        {
            zeroed.Clear();
        }

        /// <summary>Names a servo for the log: part, persistent id, vessel.</summary>
        private static string Describe(BaseServo servo)
        {
            Part part = servo.part;
            if (part == null)
            {
                return servo.GetType().Name;
            }
            string vessel = part.vessel != null ? part.vessel.vesselName : "?";
            return $"{part.partInfo?.name} ({part.persistentId}) of {vessel}";
        }
    }

    /// <summary>
    /// In flight, polls each frame for the end of on-rails warp and has <see cref="RestoreDriveAfterWarp"/> give the
    /// servos zeroed during it their drive back. Polling rather than GameEvents.onTimeWarpRateChanged keeps the test
    /// the stock code makes, whatever the order in which TimeWarp updates its rate index and fires that event.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class DriveRestorer : MonoBehaviour
    {
        private void Update()
        {
            if (RestoreDriveAfterWarp.Installed)
            {
                RestoreDriveAfterWarp.RestoreIfWarpOver();
            }
        }

        private void OnDestroy()
        {
            RestoreDriveAfterWarp.Forget();
        }
    }
}
