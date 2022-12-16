using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor.Callbacks;
using UnityEditor;
using System.IO;
using UnityEditor.iOS.Xcode;

public class IOSPostBuild
{
    [PostProcessBuild(1000)]
    public static void PostProcessXcode(BuildTarget buildTarget, string buildPath)
    {
        if (buildTarget != BuildTarget.iOS)
        {
            return;
        }

        PBXProject proj = new PBXProject();
        string projPath = PBXProject.GetPBXProjectPath(buildPath);
        proj.ReadFromFile(projPath);

        string mainTargetGUID = proj.GetUnityMainTargetGuid();
        string mainTargetName = "Unity-iPhone";
        string unityFrameworkGUID = proj.GetUnityFrameworkTargetGuid();


        proj.AddCapability(mainTargetGUID, PBXCapabilityType.BackgroundModes);


        // Copy entitlement file and link it to project (needed for APNS, apple sign-in etc.)
        var entSourcePath = Application.dataPath + "/../Packages/io.getready.rgn.signin.apple/ReadyGamesNetwork/Resources/IOSEntitlement.entitlements";
        var entFileName = Path.GetFileName(entSourcePath);
        var entDstPath = buildPath + "/" + mainTargetName + "/" + entFileName;

        if (File.Exists(entDstPath))
        {
            FileUtil.ReplaceFile(entSourcePath, entDstPath);
        }
        else
        {
            FileUtil.CopyFileOrDirectory(entSourcePath, entDstPath);
        }

        proj.AddFile(mainTargetName + "/" + entFileName, entFileName);
        proj.AddBuildProperty(mainTargetGUID, "CODE_SIGN_ENTITLEMENTS", mainTargetName + "/" + entFileName);
        proj.WriteToFile(projPath);
        File.WriteAllText(projPath, proj.WriteToString());

        // Get plist
        string plistPath = buildPath + "/Info.plist";
        PlistDocument plist = new PlistDocument();
        plist.ReadFromString(File.ReadAllText(plistPath));
        PlistElementDict rootDict = plist.root;
        // Add export compliance for TestFlight builds
        rootDict.SetString("ITSAppUsesNonExemptEncryption", "false");
        PlistElementArray arr = rootDict.CreateArray("UIBackgroundModes");
        arr.AddString("remote-notification");
        arr.AddString("fetch");
        PlistElementDict dict = rootDict.values["NSAppTransportSecurity"].AsDict();
        dict.values.Remove("NSAllowsArbitraryLoadsInWebContent");
        File.WriteAllText(plistPath, plist.WriteToString());
    }

}
