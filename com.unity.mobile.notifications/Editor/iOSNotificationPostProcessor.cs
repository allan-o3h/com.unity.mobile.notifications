#if UNITY_IOS
using System;
using System.IO;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using UnityEngine;
using Unity.Notifications;
using Unity.Notifications.iOS;

public class iOSNotificationPostProcessor : MonoBehaviour
{
    [PostProcessBuild]
    public static void OnPostprocessBuild(BuildTarget buildTarget, string path)
    {
        if (buildTarget != BuildTarget.iOS)
            return;

        // Check if we have the minimal iOS version set.
        bool hasMinOSVersion;
        try
        {
            var requiredVersion = new Version(10, 0);
            var currentVersion = new Version(PlayerSettings.iOS.targetOSVersionString);
            hasMinOSVersion = currentVersion >= requiredVersion;
        }
        catch (Exception)
        {
            hasMinOSVersion = false;
        }

        if (!hasMinOSVersion)
            Debug.Log("UserNotifications framework is only available on iOS 10.0+, please make sure that you set a correct `Target minimum iOS Version` in Player Settings.");

        var settings = NotificationSettingsManager.Initialize().iOSNotificationSettingsFlat;

        var needLocationFramework = (bool)settings.Find(i => i.Key == "UnityUseLocationNotificationTrigger").Value;
        var addPushNotificationCapability = (bool)settings.Find(i => i.Key == "UnityAddRemoteNotificationCapability").Value;

        var useReleaseAPSEnv = false;
        if (addPushNotificationCapability)
        {
            var useReleaseAPSEnvSetting = settings.Find(i => i.Key == "UnityUseAPSReleaseEnvironment");
            if (useReleaseAPSEnvSetting != null)
                useReleaseAPSEnv = (bool)useReleaseAPSEnvSetting.Value;
        }

        PatchPBXProject(path, needLocationFramework, addPushNotificationCapability, useReleaseAPSEnv);
        PatchPlist(path, settings, addPushNotificationCapability);
        PatchPreprocessor(path, needLocationFramework, addPushNotificationCapability);
    }

    private static void PatchPBXProject(string path, bool needLocationFramework, bool addPushNotificationCapability, bool useReleaseAPSEnv)
    {
        var pbxProjectPath = PBXProject.GetPBXProjectPath(path);

        var pbxProject = new PBXProject();
        pbxProject.ReadFromString(File.ReadAllText(pbxProjectPath));

        string mainTarget;
        string unityFrameworkTarget;

        var unityMainTargetGuidMethod = pbxProject.GetType().GetMethod("GetUnityMainTargetGuid");
        var unityFrameworkTargetGuidMethod = pbxProject.GetType().GetMethod("GetUnityFrameworkTargetGuid");

        if (unityMainTargetGuidMethod != null && unityFrameworkTargetGuidMethod != null)
        {
            mainTarget = (string)unityMainTargetGuidMethod.Invoke(pbxProject, null);
            unityFrameworkTarget = (string)unityFrameworkTargetGuidMethod.Invoke(pbxProject, null);
        }
        else
        {
            mainTarget = pbxProject.TargetGuidByName("Unity-iPhone");
            unityFrameworkTarget = mainTarget;
        }

        // Add necessary frameworks.
        pbxProject.AddFrameworkToProject(unityFrameworkTarget, "UserNotifications.framework", true);
        if (needLocationFramework)
            pbxProject.AddFrameworkToProject(unityFrameworkTarget, "CoreLocation.framework", false);

        File.WriteAllText(pbxProjectPath, pbxProject.WriteToString());

        // Update the entitlements file.
        if (addPushNotificationCapability)
        {
            var entitlementsFileName = pbxProject.GetBuildPropertyForAnyConfig(mainTarget, "CODE_SIGN_ENTITLEMENTS");
            if (entitlementsFileName == null)
            {
                var bundleIdentifier = PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.iOS);
                entitlementsFileName = string.Format("{0}.entitlements", bundleIdentifier.Substring(bundleIdentifier.LastIndexOf(".") + 1));
            }

            var capManager = new ProjectCapabilityManager(pbxProjectPath, entitlementsFileName, "Unity-iPhone");
            capManager.AddPushNotifications(!useReleaseAPSEnv);
            capManager.WriteToFile();
        }
    }

    private static void PatchPlist(string path, List<Unity.Notifications.NotificationSetting> settings, bool addPushNotificationCapability)
    {
        var plistPath = path + "/Info.plist";
        var plist = new PlistDocument();
        plist.ReadFromString(File.ReadAllText(plistPath));

        var rootDict = plist.root;
        var needsToWriteChanges = false;
        
        // Add all the settings to the plist.
        foreach (var setting in settings)
        {
            if (ShouldAddSettingToPlist(setting, rootDict))
            {
                needsToWriteChanges = true;
                if (setting.Value.GetType() == typeof(bool))
                {
                    rootDict.SetBoolean(setting.Key, (bool) setting.Value);
                }
                else if (setting.Value.GetType() == typeof(PresentationOption) ||
                         setting.Value.GetType() == typeof(AuthorizationOption))
                {
                    rootDict.SetInteger(setting.Key, (int) setting.Value);
                }
            }
        }

        // Add "remote-notification" to the list of supported UIBackgroundModes.
        if (addPushNotificationCapability)
        {
            PlistElementArray currentBackgroundModes = (PlistElementArray)rootDict["UIBackgroundModes"];
            if (currentBackgroundModes == null)
                currentBackgroundModes = rootDict.CreateArray("UIBackgroundModes");

            var remoteNotificationElement = new PlistElementString("remote-notification");
            if (!currentBackgroundModes.values.Contains(remoteNotificationElement))
            {
                currentBackgroundModes.values.Add(remoteNotificationElement);
                needsToWriteChanges = true;
            }
        }

        // only write if there have been changes to prevent unnecessary compilation
        if (needsToWriteChanges)
            File.WriteAllText(plistPath, plist.WriteToString());
    }

    // If the plist doesn't contain the key, or it's value is different, we should add/overwrite it.
    private static bool ShouldAddSettingToPlist(Unity.Notifications.NotificationSetting setting,
        PlistElementDict rootDict)
    {
        if (!rootDict.values.ContainsKey(setting.Key))
            return true;
        else if (setting.Value.GetType() == typeof(bool))
            return !rootDict.values[setting.Key].AsBoolean().Equals((bool)setting.Value);
        else if (setting.Value.GetType() == typeof(PresentationOption) || setting.Value.GetType() == typeof(AuthorizationOption))
            return !rootDict.values[setting.Key].AsInteger().Equals((int)setting.Value);
        else
            return false;
    }

    private static void PatchPreprocessor(string path, bool needLocationFramework, bool addPushNotificationCapability)
    {
        var preprocessorPath = path + "/Classes/Preprocessor.h";
        var preprocessor = File.ReadAllText(preprocessorPath);
        var needsToWriteChanges = false;

        if (needLocationFramework && preprocessor.Contains("UNITY_USES_LOCATION"))
        {
            preprocessor = preprocessor.Replace("UNITY_USES_LOCATION 0", "UNITY_USES_LOCATION 1");
            needsToWriteChanges = true;
        }

        if (addPushNotificationCapability && preprocessor.Contains("UNITY_USES_REMOTE_NOTIFICATIONS"))
        {
            preprocessor =
                preprocessor.Replace("UNITY_USES_REMOTE_NOTIFICATIONS 0", "UNITY_USES_REMOTE_NOTIFICATIONS 1");
            needsToWriteChanges = true;
        }

        // only write if there have been changes to prevent unnecessary compilation
        if (needsToWriteChanges)
            File.WriteAllText(preprocessorPath, preprocessor);
    }
}
#endif
