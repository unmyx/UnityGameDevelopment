using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Game.Networking;
using Game.Systems;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public sealed class NpcCatchEventContractTests
{
    private const string NetworkAuthorityPath = "Assets/Scripts/Networking/NetworkSessionProgressAuthority.cs";
    private const string NpcControllerPath = "Assets/Scripts/Systems/NPCController.cs";
    private const string RemovedEventPath = "Assets/Scripts/Core/Events/NpcCatchTriggeredEvent.cs";

    [Test]
    public void NpcCatchTriggeredEvent_HasZeroProductionPublishersAndSubscribers()
    {
        string[] productionFiles = Directory.GetFiles("Assets/Scripts", "*.cs", SearchOption.AllDirectories);
        string productionSource = string.Join("\n", productionFiles.Select(File.ReadAllText));

        int publisherCount = Regex.Matches(
            productionSource,
            @"EventBus\s*\.\s*Publish\s*\(\s*new\s+NpcCatchTriggeredEvent\b").Count;
        int subscriberCount = Regex.Matches(
            productionSource,
            @"EventBus\s*\.\s*Subscribe\s*<\s*NpcCatchTriggeredEvent\s*>").Count;

        Assert.That(File.Exists(RemovedEventPath), Is.False);
        Assert.That(publisherCount, Is.Zero, "NpcCatchTriggeredEvent publisher count changed.");
        Assert.That(subscriberCount, Is.Zero, "NpcCatchTriggeredEvent subscriber count changed.");
    }

    [Test]
    public void NetworkCatchRpc_DeduplicatesBeforeCallingDirectLieFlowExactlyOnce()
    {
        string source = File.ReadAllText(NetworkAuthorityPath);
        string method = ExtractMethodBody(source, "private void SendNpcCatchTriggeredClientRpc(");

        Assert.That(CountOccurrences(method, "TryConsumeNpcCatch(ownerKey, catchToken)"), Is.EqualTo(1));
        Assert.That(CountOccurrences(method, "TryBeginLocalLieMinigameFromCatch(response);"), Is.EqualTo(1));
        Assert.That(
            method.IndexOf("TryConsumeNpcCatch(ownerKey, catchToken)", StringComparison.Ordinal),
            Is.LessThan(method.IndexOf("TryBeginLocalLieMinigameFromCatch(response);", StringComparison.Ordinal)));
        Assert.That(method, Does.Not.Contain("NpcCatchTriggeredEvent"));
    }

    [Test]
    public void OfflineAndNetworkCatchBranches_EachHaveOneExistingStartRoute()
    {
        string npcSource = File.ReadAllText(NpcControllerPath);
        string catchMethod = ExtractMethodBody(npcSource, "private void HandleAuthoritativeCatch()");
        string networkSource = File.ReadAllText(NetworkAuthorityPath);
        string networkRpc = ExtractMethodBody(networkSource, "private void SendNpcCatchTriggeredClientRpc(");

        Assert.That(CountOccurrences(catchMethod, "NetworkSessionProgressAuthority.TryRegisterNpcCatch("), Is.EqualTo(1));
        Assert.That(CountOccurrences(catchMethod, "TriggerLieMinigame();"), Is.EqualTo(1));
        Assert.That(CountOccurrences(networkRpc, "TryBeginLocalLieMinigameFromCatch(response);"), Is.EqualTo(1));
    }

    [Test]
    public void RepeatedNetworkCatchToken_IsConsumedAtMostOnce()
    {
        FieldInfo consumedKeysField = typeof(NetworkSessionProgressAuthority).GetField(
            "ConsumedNpcCatchKeys",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(consumedKeysField, Is.Not.Null);
        HashSet<string> consumedKeys = (HashSet<string>)consumedKeysField.GetValue(null);
        consumedKeys.Clear();

        try
        {
            MethodInfo tryConsume = typeof(NetworkSessionProgressAuthority).GetMethod(
                "TryConsumeNpcCatch",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(tryConsume, Is.Not.Null);

            Assert.That((bool)tryConsume.Invoke(null, new object[] { "player:1", 77UL }), Is.True);
            Assert.That((bool)tryConsume.Invoke(null, new object[] { "player:1", 77UL }), Is.False);
            Assert.That(consumedKeys, Has.Count.EqualTo(1));
        }
        finally
        {
            consumedKeys.Clear();
        }
    }

    [Test]
    public void OfflineCatchWithoutMinigameManager_RemainsSafeAndWarningGated()
    {
        Assert.That(Game.Minigames.MinigameManager.Instance, Is.Null);
        GameObject npcObject = new GameObject("NpcCatchEventContract.NPC");
        NPCController controller = npcObject.AddComponent<NPCController>();

        try
        {
            MethodInfo trigger = typeof(NPCController).GetMethod(
                "TriggerLieMinigame",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(trigger, Is.Not.Null);

            LogAssert.Expect(LogType.Warning, "[NPCController] MinigameManager instance not found.");
            Assert.DoesNotThrow(() => trigger.Invoke(controller, null));
            Assert.DoesNotThrow(() => trigger.Invoke(controller, null));

            Assert.That(GetPrivateField<bool>(controller, "hasCaughtPlayer"), Is.False);
            Assert.That(GetPrivateField<bool>(controller, "triggeredMinigame"), Is.False);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(npcObject);
        }
    }

    private static string ExtractMethodBody(string source, string signature)
    {
        int signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.That(signatureIndex, Is.GreaterThanOrEqualTo(0), $"Missing method signature '{signature}'.");
        int openingBrace = source.IndexOf('{', signatureIndex);
        Assert.That(openingBrace, Is.GreaterThan(signatureIndex));

        int depth = 0;
        for (int i = openingBrace; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(openingBrace, i - openingBrace + 1);
                }
            }
        }

        Assert.Fail($"Method '{signature}' has no balanced body.");
        return string.Empty;
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int searchIndex = 0;
        while ((searchIndex = source.IndexOf(value, searchIndex, StringComparison.Ordinal)) >= 0)
        {
            count++;
            searchIndex += value.Length;
        }

        return count;
    }

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing field '{fieldName}'.");
        return (T)field.GetValue(target);
    }
}
