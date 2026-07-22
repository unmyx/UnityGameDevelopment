using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.Rendering;

public sealed class VolumeProfileAssetTests
{
    private const string ProfilePath = "Assets/Settings/DefaultVolumeProfile.asset";
    private const string GlobalSettingsPath = "Assets/Settings/UniversalRenderPipelineGlobalSettings.asset";
    private const string ProfileGuid = "ab09877e2e707104187f6f83e2f62510";
    private const int InitialSerializedObjectCount = 29;
    private const int RemovedMissingComponentCount = 5;
    private const int ReferencedComponentCount = 19;

    private static readonly string[] RemovedComponentNames =
    {
        "CopyPasteTestComponent1",
        "CopyPasteTestComponent2",
        "CopyPasteTestComponent3",
        "VolumeComponentSupportedEverywhere",
        "VolumeComponentSupportedOnAnySRP"
    };

    private static readonly string[] PreservedUnreferencedComponentNames =
    {
        "OutlineVolumeComponent",
        "TestAnimationCurveVolumeComponent",
        "OasisFogVolumeComponent",
        "TestVolume"
    };

    [Test]
    public void DefaultVolumeProfile_ExistsAndGlobalSettingsRetainsReference()
    {
        VolumeProfile profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
        Assert.That(profile, Is.Not.Null);
        Assert.That(AssetDatabase.AssetPathToGUID(ProfilePath), Is.EqualTo(ProfileGuid));

        string globalSettings = File.ReadAllText(GlobalSettingsPath);
        Assert.That(
            globalSettings,
            Does.Contain($"m_VolumeProfile: {{fileID: 11400000, guid: {ProfileGuid}, type: 2}}"));
    }

    [Test]
    public void DefaultVolumeProfile_ContainsNoNullScriptRecordsAndRemovedExactlyFiveKnownRecords()
    {
        string profileYaml = File.ReadAllText(ProfilePath);

        Assert.That(profileYaml, Does.Not.Contain("m_Script: {fileID: 0}"));
        Assert.That(RemovedComponentNames, Has.Length.EqualTo(RemovedMissingComponentCount));
        foreach (string componentName in RemovedComponentNames)
        {
            Assert.That(profileYaml, Does.Not.Contain($"m_Name: {componentName}"));
        }

        int serializedObjectCount = Regex.Matches(profileYaml, @"(?m)^--- !u!114 &").Count;
        Assert.That(
            serializedObjectCount,
            Is.EqualTo(InitialSerializedObjectCount - RemovedMissingComponentCount));
    }

    [Test]
    public void DefaultVolumeProfile_PreservesEveryReferencedValidComponent()
    {
        VolumeProfile profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
        Assert.That(profile, Is.Not.Null);
        Assert.That(profile.components, Has.Count.EqualTo(ReferencedComponentCount));
        Assert.That(profile.components, Has.None.Null);
        Assert.That(profile.components.All(component => component is VolumeComponent), Is.True);

        string profileYaml = File.ReadAllText(ProfilePath);
        foreach (string componentName in PreservedUnreferencedComponentNames)
        {
            Assert.That(profileYaml, Does.Contain($"m_Name: {componentName}"));
        }
    }

    [Test]
    public void DefaultVolumeProfile_ReimportsWithoutLosingValidComponents()
    {
        AssetDatabase.ImportAsset(
            ProfilePath,
            ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

        VolumeProfile profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
        Assert.That(profile, Is.Not.Null);
        Assert.That(profile.components, Has.Count.EqualTo(ReferencedComponentCount));
        Assert.That(profile.components, Has.None.Null);
    }
}
