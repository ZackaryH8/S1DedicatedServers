using System;
using System.Collections.Generic;
using HarmonyLib;
using MelonLoader;
#if IL2CPP
using Il2CppFishNet.Object.Synchronizing;
using EnvironmentManagerType = Il2CppScheduleOne.Weather.EnvironmentManager;
using LandVehicleType = Il2CppScheduleOne.Vehicles.LandVehicle;
using VehicleSettingsType = Il2CppScheduleOne.Experimental.VehicleSettings;
using WeatherConditionsType = Il2CppScheduleOne.Weather.WeatherConditions;
using WeatherVolumeType = Il2CppScheduleOne.Weather.WeatherVolume;
using WheelDataType = Il2CppScheduleOne.Experimental.WheelData;
using WheelOverrideDataType = Il2CppScheduleOne.Experimental.WheelOverrideData;
using WheelType = Il2CppScheduleOne.Vehicles.Wheel;
#else
using FishNet.Object.Synchronizing;
using EnvironmentManagerType = ScheduleOne.Weather.EnvironmentManager;
using LandVehicleType = ScheduleOne.Vehicles.LandVehicle;
using VehicleSettingsType = ScheduleOne.Experimental.VehicleSettings;
using WeatherConditionsType = ScheduleOne.Weather.WeatherConditions;
using WeatherVolumeType = ScheduleOne.Weather.WeatherVolume;
using WheelDataType = ScheduleOne.Experimental.WheelData;
using WheelOverrideDataType = ScheduleOne.Experimental.WheelOverrideData;
using WheelType = ScheduleOne.Vehicles.Wheel;
#endif
using UnityEngine;

namespace DedicatedServerMod.Shared.Patches
{
    internal static class WeatherStabilityLog
    {
        private static readonly HashSet<string> WarningKeys = new HashSet<string>(StringComparer.Ordinal);

        internal static void WarningOnce(string key, string message)
        {
            lock (WarningKeys)
            {
                if (!WarningKeys.Add(key))
                {
                    return;
                }
            }

            MelonLogger.Warning(message);
        }
    }

    [HarmonyPatch(typeof(WheelType), "Awake")]
    internal static class WheelAwakePatches
    {
        private static void Postfix(WheelType __instance)
        {
            try
            {
                // Use reflection to access private fields since direct field access fails in IL2CPP
                var settingsField = typeof(WheelType).GetField("_settings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var defaultDataField = typeof(WheelType).GetField("_defaultData", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (settingsField == null || defaultDataField == null)
                {
                    WeatherStabilityLog.WarningOnce(
                        "wheel-awake-reflection-failed",
                        "Could not access wheel private fields via reflection; wheel settings recovery disabled.");
                    return;
                }

                var currentSettings = settingsField.GetValue(__instance) as VehicleSettingsType;
                if (currentSettings != null)
                {
                    return;
                }

                var defaultData = defaultDataField.GetValue(__instance) as WheelDataType;
                var newSettings = defaultData?.Settings?.Clone() ?? new VehicleSettingsType();

                settingsField.SetValue(__instance, newSettings);
                WeatherStabilityLog.WarningOnce(
                    "wheel-awake-default-settings",
                    "Recovered missing wheel settings during Wheel.Awake; vehicle weather friction will use a safe fallback.");
            }
            catch (Exception ex)
            {
                WeatherStabilityLog.WarningOnce(
                    "wheel-awake-exception",
                    $"Exception during wheel settings recovery: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(WheelType), nameof(WheelType.OnWeatherChange))]
    internal static class WheelOnWeatherChangePatches
    {
        private static bool Prefix(WheelType __instance, WeatherConditionsType newConditions)
        {
            try
            {
                // Use reflection to access private fields since direct field access fails in IL2CPP
                var settingsField = typeof(WheelType).GetField("_settings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var defaultDataField = typeof(WheelType).GetField("_defaultData", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var rainOverrideDataField = typeof(WheelType).GetField("_rainOverrideData", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var vehicleField = typeof(WheelType).GetField("_vehicle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (settingsField == null || defaultDataField == null || rainOverrideDataField == null || vehicleField == null)
                {
                    WeatherStabilityLog.WarningOnce(
                        "wheel-weather-reflection-failed",
                        "Could not access wheel private fields via reflection; weather updates may be unstable.");
                    return true; // Let original method run
                }

                var defaultData = defaultDataField.GetValue(__instance) as WheelDataType;
                var rainOverrideData = rainOverrideDataField.GetValue(__instance) as WheelOverrideDataType;
                var vehicle = vehicleField.GetValue(__instance) as LandVehicleType;
                var currentSettings = settingsField.GetValue(__instance) as VehicleSettingsType;

                VehicleSettingsType resolvedSettings = defaultData?.Settings?.Clone()
                    ?? currentSettings?.Clone()
                    ?? new VehicleSettingsType();

                if (newConditions == null)
                {
                    settingsField.SetValue(__instance, resolvedSettings);
                    WeatherStabilityLog.WarningOnce(
                        "wheel-null-weather-conditions",
                        "Wheel.OnWeatherChange received null weather conditions; keeping default wheel settings.");
                    return false;
                }

                bool canApplyRainOverride = newConditions.Rainy > 0f
                    && vehicle != null
                    && !vehicle.IsUnderCover
                    && rainOverrideData?.Settings != null;

                if (canApplyRainOverride)
                {
                    resolvedSettings = resolvedSettings.Blend(rainOverrideData.Settings, newConditions.Rainy);
                }
                else if (newConditions.Rainy > 0f && rainOverrideData?.Settings == null)
                {
                    WeatherStabilityLog.WarningOnce(
                        "wheel-missing-rain-override",
                        "A wheel is missing rain override data after the weather update; using default friction settings.");
                }

                if (vehicle == null)
                {
                    WeatherStabilityLog.WarningOnce(
                        "wheel-missing-vehicle",
                        "A wheel could not resolve its parent vehicle during weather updates; using default friction settings.");
                }

                settingsField.SetValue(__instance, resolvedSettings);
                return false;
            }
            catch (Exception ex)
            {
                WeatherStabilityLog.WarningOnce(
                    "wheel-weather-exception",
                    $"Exception during wheel weather update: {ex.GetType().Name}: {ex.Message}");
                return true; // Let original method run on error
            }
        }
    }

    [HarmonyPatch(typeof(LandVehicleType), nameof(LandVehicleType.OnWeatherChange))]
    internal static class LandVehicleOnWeatherChangePatches
    {
        private static bool Prefix(LandVehicleType __instance, WeatherConditionsType newConditions)
        {
            if (__instance?.wheels == null || __instance.wheels.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < __instance.wheels.Count; i++)
            {
                WheelType wheel = __instance.wheels[i];
                if (wheel == null)
                {
                    continue;
                }

                try
                {
                    wheel.OnWeatherChange(newConditions);
                }
                catch (Exception ex)
                {
                    WeatherStabilityLog.WarningOnce(
                        "land-vehicle-wheel-weather-exception",
                        $"A vehicle wheel threw during weather updates and was isolated to keep the weather loop alive: {ex.GetType().Name}: {ex.Message}");
                }
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(EnvironmentManagerType), "BlendWeatherProfiles")]
    internal static class EnvironmentManagerBlendWeatherProfilesPatches
    {
        private static readonly AnimationCurve FallbackBlendCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);

        private static bool Prefix(EnvironmentManagerType __instance)
        {
            try
            {
                // Use reflection to access private fields since direct field access fails in IL2CPP
                var activeWeatherVolumesField = typeof(EnvironmentManagerType).GetField("_activeWeatherVolumes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var targetWeatherVolumeIndexField = typeof(EnvironmentManagerType).GetField("_targetWeatherVolumeIndex", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var neighbourWeatherVolumeIndexField = typeof(EnvironmentManagerType).GetField("_neighbourWeatherVolumeIndex", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var hasWeatherVolumeNeighbourField = typeof(EnvironmentManagerType).GetField("_hasWeatherVolumeNeighbour", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var targetWeatherBlendValueField = typeof(EnvironmentManagerType).GetField("_targetWeatherBlendValue", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var neighbourWeatherBlendValueField = typeof(EnvironmentManagerType).GetField("_neighbourWeatherBlendValue", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var blendCurveField = typeof(EnvironmentManagerType).GetField("_blendCurve", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (activeWeatherVolumesField == null || targetWeatherVolumeIndexField == null ||
                    neighbourWeatherVolumeIndexField == null || hasWeatherVolumeNeighbourField == null ||
                    targetWeatherBlendValueField == null || neighbourWeatherBlendValueField == null ||
                    blendCurveField == null)
                {
                    WeatherStabilityLog.WarningOnce(
                        "environment-blend-reflection-failed",
                        "Could not access EnvironmentManager private fields via reflection; weather blending disabled.");
                    return false;
                }

                var activeWeatherVolumes = activeWeatherVolumesField.GetValue(__instance) as SyncList<WeatherVolumeType>;
                var targetWeatherVolumeIndex = (int)targetWeatherVolumeIndexField.GetValue(__instance);
                var neighbourWeatherVolumeIndex = (int)neighbourWeatherVolumeIndexField.GetValue(__instance);
                var hasWeatherVolumeNeighbour = (bool)hasWeatherVolumeNeighbourField.GetValue(__instance);
                var targetWeatherBlendValue = (float)targetWeatherBlendValueField.GetValue(__instance);
                var neighbourWeatherBlendValue = (float)neighbourWeatherBlendValueField.GetValue(__instance);
                var blendCurve = blendCurveField.GetValue(__instance) as AnimationCurve;

                if (activeWeatherVolumes == null || activeWeatherVolumes.Count == 0)
                {
                    return false;
                }

                if (targetWeatherVolumeIndex < 0 || targetWeatherVolumeIndex >= activeWeatherVolumes.Count)
                {
                    for (int i = 0; i < activeWeatherVolumes.Count; i++)
                    {
                        WeatherVolumeType volume = activeWeatherVolumes[i];
                        if (volume == null)
                        {
                            continue;
                        }

                        volume.SetNeighbourVolume(null);
                        volume.BlendEffects(0f, blendCurve ?? FallbackBlendCurve);
                    }

                    return false;
                }

                WeatherVolumeType targetVolume = activeWeatherVolumes[targetWeatherVolumeIndex];
                WeatherVolumeType neighbourVolume = null;
                bool hasValidNeighbour = hasWeatherVolumeNeighbour
                    && neighbourWeatherVolumeIndex >= 0
                    && neighbourWeatherVolumeIndex < activeWeatherVolumes.Count;

                if (hasValidNeighbour)
                {
                    neighbourVolume = activeWeatherVolumes[neighbourWeatherVolumeIndex];
                    hasValidNeighbour = neighbourVolume != null;
                }

                AnimationCurve finalBlendCurve = blendCurve ?? FallbackBlendCurve;

                for (int i = 0; i < activeWeatherVolumes.Count; i++)
                {
                    WeatherVolumeType volume = activeWeatherVolumes[i];
                    if (volume == null)
                    {
                        continue;
                    }

                    if (i == targetWeatherVolumeIndex)
                    {
                        volume.SetNeighbourVolume(hasValidNeighbour ? neighbourVolume : null);
                        volume.BlendEffects(hasValidNeighbour ? Mathf.Clamp01(targetWeatherBlendValue) : 1f, finalBlendCurve);
                        continue;
                    }

                    if (hasValidNeighbour && i == neighbourWeatherVolumeIndex)
                    {
                        volume.SetNeighbourVolume(targetVolume);
                        volume.BlendEffects(Mathf.Clamp01(neighbourWeatherBlendValue), finalBlendCurve);
                        continue;
                    }

                    volume.SetNeighbourVolume(null);
                    volume.BlendEffects(0f, finalBlendCurve);
                }

                return false;
            }
            catch (Exception ex)
            {
                WeatherStabilityLog.WarningOnce(
                    "environment-blend-exception",
                    $"Exception during weather blending: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }
}
