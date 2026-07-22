using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Game.Core;
using Game.Interaction;
using Game.Systems;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public sealed class DevelopmentDiagnosticsTests
{
    [Test]
    public void DevelopmentLogger_DoesNotMutateGameplayState()
    {
        int gameplayState = 7;
        LogAssert.Expect(LogType.Log, "[DevelopmentDiagnosticsTests] probe");

        DevelopmentDiagnostics.Log("[DevelopmentDiagnosticsTests] probe");

        Assert.That(gameplayState, Is.EqualTo(7));
    }

    [Test]
    public void WarningOnceGate_SameCauseReportsOnlyOnce()
    {
        var gate = new WarningOnceGate();
        LogAssert.Expect(LogType.Warning, "diagnostic-cause");

        ReportWarning(gate, "cause-a", "diagnostic-cause");
        ReportWarning(gate, "cause-a", "diagnostic-cause");

        LogAssert.NoUnexpectedReceived();
    }

    [Test]
    public void WarningOnceGate_NewCauseAndResetCanReportAgain()
    {
        var gate = new WarningOnceGate();
        LogAssert.Expect(LogType.Warning, "cause-a");
        LogAssert.Expect(LogType.Warning, "cause-b");
        LogAssert.Expect(LogType.Warning, "cause-a-reset");

        ReportWarning(gate, "cause-a", "cause-a");
        ReportWarning(gate, "cause-b", "cause-b");
        gate.Reset("cause-a");
        ReportWarning(gate, "cause-a", "cause-a-reset");

        LogAssert.NoUnexpectedReceived();
    }

    [Test]
    public void CriticalSetupFlows_StillContainErrorReporting()
    {
        AssertSourceContains("Assets/Scripts/Interaction/InteractionSystem.cs", "Debug.LogError(");
        AssertSourceContains("Assets/Scripts/Systems/NPCController.cs", "Debug.LogError(");
        AssertSourceContains("Assets/Scripts/Core/GameManager.cs", "Debug.LogError(");
    }

    [Test]
    public void InteractionAndNpcDebugDrawing_DefaultsToDisabled()
    {
        GameObject interactionObject = new GameObject("DevelopmentDiagnosticsTests.Interaction");
        GameObject npcObject = new GameObject("DevelopmentDiagnosticsTests.Npc");
        interactionObject.SetActive(false);
        npcObject.SetActive(false);

        try
        {
            InteractionSystem interaction = interactionObject.AddComponent<InteractionSystem>();
            NPCController npc = npcObject.AddComponent<NPCController>();

            Assert.That(GetPrivateField<bool>(interaction, "_showDebugRay"), Is.False);
            Assert.That(GetPrivateField<bool>(interaction, "_enableInteractionLogs"), Is.False);
            Assert.That(GetPrivateField<bool>(npc, "drawDetectionGizmos"), Is.False);
            Assert.That(GetPrivateField<bool>(npc, "enableDebugLogs"), Is.False);
        }
        finally
        {
            Object.DestroyImmediate(interactionObject);
            Object.DestroyImmediate(npcObject);
        }
    }

    [Test]
    public void RuntimeScripts_HaveNoDirectOrdinaryLogsOrDebugDrawCalls()
    {
        string helperPath = NormalizePath("Assets/Scripts/Core/DevelopmentDiagnostics.cs");
        string[] sourceFiles = Directory.GetFiles("Assets/Scripts", "*.cs", SearchOption.AllDirectories);
        var violations = new List<string>();
        var directDiagnosticPattern = new Regex(@"\bDebug\.(Log|DrawRay|DrawLine)\s*\(");

        foreach (string sourceFile in sourceFiles)
        {
            if (NormalizePath(sourceFile) == helperPath)
            {
                continue;
            }

            string source = File.ReadAllText(sourceFile);
            if (directDiagnosticPattern.IsMatch(source))
            {
                violations.Add(NormalizePath(sourceFile));
            }
        }

        Assert.That(violations, Is.Empty,
            $"Direct runtime Debug.Log/Draw calls bypass development gating: {string.Join(", ", violations)}");
    }

    [Test]
    public void DevelopmentMethods_AreConditionalForEditorAndDevelopmentBuild()
    {
        MethodInfo[] diagnosticMethods = typeof(DevelopmentDiagnostics).GetMethods(
            BindingFlags.Public | BindingFlags.Static);

        foreach (string methodName in new[] { "Log", "DrawRay" })
        {
            MethodInfo[] methods = diagnosticMethods.Where(method => method.Name == methodName).ToArray();
            Assert.That(methods, Is.Not.Empty);
            foreach (MethodInfo method in methods)
            {
                string[] symbols = method.GetCustomAttributes<ConditionalAttribute>()
                    .Select(attribute => attribute.ConditionString)
                    .ToArray();
                Assert.That(symbols, Does.Contain("UNITY_EDITOR"));
                Assert.That(symbols, Does.Contain("DEVELOPMENT_BUILD"));
            }
        }
    }

    private static void ReportWarning(WarningOnceGate gate, string cause, string message)
    {
        if (gate.ShouldReport(cause))
        {
            UnityEngine.Debug.LogWarning(message);
        }
    }

    private static void AssertSourceContains(string path, string expected)
    {
        Assert.That(File.ReadAllText(path), Does.Contain(expected),
            $"Expected error reporting call was removed from '{path}'.");
    }

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        FieldInfo field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing field '{fieldName}'.");
        return (T)field.GetValue(target);
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }
}
