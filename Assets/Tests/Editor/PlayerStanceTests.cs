using System.Collections.Generic;
using System.Reflection;
using Game.Player;
using NUnit.Framework;
using UnityEngine;

public sealed class PlayerStanceTests
{
    private readonly List<GameObject> _objects = new List<GameObject>();

    [TearDown]
    public void TearDown()
    {
        for (int i = _objects.Count - 1; i >= 0; i--)
        {
            if (_objects[i] != null)
            {
                Object.DestroyImmediate(_objects[i]);
            }
        }

        _objects.Clear();
        Physics.SyncTransforms();
    }

    [Test]
    public void StandingCapsule_HasValidHeightAndCenter()
    {
        const float standingHeight = 1.8f;
        const float radius = 0.33f;
        Vector3 center = PlayerStanceGeometry.CenterPreservingBottom(
            new Vector3(0f, 0.9f, 0f),
            standingHeight,
            standingHeight);

        Assert.That(standingHeight, Is.GreaterThanOrEqualTo(radius * 2f));
        Assert.That(center.y, Is.EqualTo(0.9f).Within(0.0001f));
    }

    [Test]
    public void CrouchCapsule_HasValidHeightAndCenter()
    {
        Vector3 center = PlayerStanceGeometry.CenterPreservingBottom(
            new Vector3(0f, 0.9f, 0f),
            1.8f,
            0.9f);

        Assert.That(0.9f, Is.GreaterThanOrEqualTo(0.33f * 2f));
        Assert.That(center.y, Is.EqualTo(0.45f).Within(0.0001f));
    }

    [Test]
    public void BottomY_RemainsFixedWhenCrouching()
    {
        Vector3 standingCenter = new Vector3(0.15f, 1.1f, -0.2f);
        float standingBottom = PlayerStanceGeometry.BottomY(standingCenter, 2f);
        Vector3 crouchingCenter = PlayerStanceGeometry.CenterPreservingBottom(
            standingCenter,
            2f,
            1.1f);

        Assert.That(
            PlayerStanceGeometry.BottomY(crouchingCenter, 1.1f),
            Is.EqualTo(standingBottom).Within(0.0001f));
    }

    [Test]
    public void BottomY_RemainsFixedWhenStanding()
    {
        Vector3 crouchingCenter = new Vector3(0f, 0.55f, 0f);
        Vector3 standingCenter = PlayerStanceGeometry.CenterPreservingBottom(
            crouchingCenter,
            1.1f,
            2f);

        Assert.That(
            PlayerStanceGeometry.BottomY(standingCenter, 2f),
            Is.EqualTo(PlayerStanceGeometry.BottomY(crouchingCenter, 1.1f)).Within(0.0001f));
    }

    [TestCase(0.1f, 0.4f, 0.8f)]
    [TestCase(-2f, 0.33f, 0.66f)]
    public void Height_IsNeverBelowCapsuleDiameter(float requested, float radius, float expected)
    {
        Assert.That(
            PlayerStanceGeometry.NormalizeHeight(requested, radius, 1.8f),
            Is.EqualTo(expected).Within(0.0001f));
    }

    [Test]
    public void InvalidHeights_AreNormalizedWithoutNaNOrInfinity()
    {
        float fromNaN = PlayerStanceGeometry.NormalizeHeight(float.NaN, 0.33f, 1.8f);
        float fromInfinity = PlayerStanceGeometry.NormalizeHeight(float.PositiveInfinity, 0.33f, float.NaN);

        Assert.That(PlayerStanceGeometry.IsFinite(fromNaN), Is.True);
        Assert.That(PlayerStanceGeometry.IsFinite(fromInfinity), Is.True);
        Assert.That(fromNaN, Is.EqualTo(1.8f).Within(0.0001f));
        Assert.That(fromInfinity, Is.EqualTo(0.66f).Within(0.0001f));
    }

    [Test]
    public void CameraY_MatchesStandingHeight()
    {
        Assert.That(
            PlayerStanceGeometry.CameraYForHeight(1.8f, 0.9f, 1.8f, 0.75f, 1.5f),
            Is.EqualTo(1.5f).Within(0.0001f));
    }

    [Test]
    public void CameraY_MatchesCrouchingHeight()
    {
        Assert.That(
            PlayerStanceGeometry.CameraYForHeight(0.9f, 0.9f, 1.8f, 0.75f, 1.5f),
            Is.EqualTo(0.75f).Within(0.0001f));
    }

    [Test]
    public void IntermediateHeight_KeepsCenterAndCameraSynchronized()
    {
        const float intermediateHeight = 1.35f;
        Vector3 center = PlayerStanceGeometry.CenterPreservingBottom(
            new Vector3(0f, 0.9f, 0f),
            1.8f,
            intermediateHeight);
        float cameraY = PlayerStanceGeometry.CameraYForHeight(
            intermediateHeight,
            0.9f,
            1.8f,
            0.75f,
            1.5f);

        Assert.That(PlayerStanceGeometry.BottomY(center, intermediateHeight), Is.Zero.Within(0.0001f));
        Assert.That(center.y, Is.EqualTo(0.675f).Within(0.0001f));
        Assert.That(cameraY, Is.EqualTo(1.125f).Within(0.0001f));
    }

    [Test]
    public void RepeatedGeometryApplication_DoesNotDriftCapsule()
    {
        Vector3 standingCenter = new Vector3(0.2f, 0.9f, -0.4f);
        Vector3 result = standingCenter;
        for (int i = 0; i < 100; i++)
        {
            result = PlayerStanceGeometry.CenterPreservingBottom(standingCenter, 1.8f, 0.9f);
        }

        Assert.That(result, Is.EqualTo(new Vector3(0.2f, 0.45f, -0.4f)));
    }

    [Test]
    public void NoCeiling_AllowsStanding()
    {
        PlayerController controller = CreateCrouchedController();

        Assert.That(controller.CanStandUp(), Is.True);
    }

    [Test]
    public void LowCeiling_BlocksStanding()
    {
        PlayerController controller = CreateCrouchedController();
        CreateCeiling(1.15f, isTrigger: false);

        Assert.That(controller.CanStandUp(), Is.False);
    }

    [Test]
    public void BlockedStanding_DoesNotChangeCrouchedGeometry()
    {
        PlayerController controller = CreateCrouchedController();
        CharacterController capsule = controller.GetComponent<CharacterController>();
        CreateCeiling(1.15f, isTrigger: false);
        float originalBottom = PlayerStanceGeometry.BottomY(capsule.center, capsule.height);

        for (int i = 0; i < 8; i++)
        {
            Assert.That(controller.CanStandUp(), Is.False);
        }

        Assert.That(capsule.height, Is.EqualTo(0.9f).Within(0.0001f));
        Assert.That(PlayerStanceGeometry.BottomY(capsule.center, capsule.height), Is.EqualTo(originalBottom).Within(0.0001f));
    }

    [Test]
    public void RemovingCeiling_AllowsStandingWithoutAnotherInput()
    {
        PlayerController controller = CreateCrouchedController();
        GameObject ceiling = CreateCeiling(1.15f, isTrigger: false);
        Assert.That(controller.CanStandUp(), Is.False);

        Object.DestroyImmediate(ceiling);
        Physics.SyncTransforms();

        Assert.That(controller.CanStandUp(), Is.True);
    }

    [Test]
    public void TriggerCeiling_DoesNotBlockStanding()
    {
        PlayerController controller = CreateCrouchedController();
        CreateCeiling(1.15f, isTrigger: true);

        Assert.That(controller.CanStandUp(), Is.True);
    }

    [Test]
    public void OwnChildCollider_DoesNotBlockStanding()
    {
        PlayerController controller = CreateCrouchedController();
        GameObject ownColliderObject = Track(new GameObject("OwnUpperCollider"));
        ownColliderObject.transform.SetParent(controller.transform, false);
        ownColliderObject.transform.localPosition = new Vector3(0f, 1.15f, 0f);
        ownColliderObject.AddComponent<BoxCollider>().size = new Vector3(0.4f, 0.2f, 0.4f);
        Physics.SyncTransforms();

        Assert.That(controller.CanStandUp(), Is.True);
    }

    [Test]
    public void ChangedStandingHeight_IsUsedByClearanceCheck()
    {
        PlayerController controller = CreateCrouchedController(standingHeight: 2.4f, crouchingHeight: 1f);
        CreateCeiling(2.05f, isTrigger: false);

        Assert.That(controller.CanStandUp(), Is.False);
    }

    [Test]
    public void ApplyingCrouch_DoesNotChangeHorizontalPositionOrCameraRotation()
    {
        PlayerController controller = CreateCrouchedController();
        Transform cameraTransform = GetField<Transform>(controller, "_cameraTransform");
        controller.transform.position = new Vector3(3f, 0f, -4f);
        cameraTransform.localRotation = Quaternion.Euler(17f, 8f, 2f);
        Vector3 expectedPosition = controller.transform.position;
        Quaternion expectedRotation = cameraTransform.localRotation;

        Invoke(controller, "ApplyStanceGeometry", 0.9f);

        Assert.That(controller.transform.position, Is.EqualTo(expectedPosition));
        Assert.That(Quaternion.Angle(cameraTransform.localRotation, expectedRotation), Is.LessThan(0.001f));
    }

    private PlayerController CreateCrouchedController(
        float standingHeight = 1.8f,
        float crouchingHeight = 0.9f)
    {
        GameObject root = Track(new GameObject("PlayerStanceTestRoot"));
        root.SetActive(false);
        CharacterController capsule = root.AddComponent<CharacterController>();
        capsule.radius = 0.33f;
        capsule.height = standingHeight;
        capsule.center = new Vector3(0f, standingHeight * 0.5f, 0f);

        GameObject cameraObject = Track(new GameObject("PlayerStanceTestCamera"));
        cameraObject.transform.SetParent(root.transform, false);
        cameraObject.transform.localPosition = new Vector3(0f, 1.5f, 0.05f);

        PlayerController controller = root.AddComponent<PlayerController>();
        SetField(controller, "_normalHeight", standingHeight);
        SetField(controller, "_crouchHeight", crouchingHeight);
        SetField(controller, "_cameraTransform", cameraObject.transform);
        Invoke(controller, "InitializeComponents");

        capsule.height = crouchingHeight;
        capsule.center = PlayerStanceGeometry.CenterPreservingBottom(
            capsule.center,
            standingHeight,
            crouchingHeight);
        root.SetActive(true);
        Physics.SyncTransforms();
        return controller;
    }

    private GameObject CreateCeiling(float centerY, bool isTrigger)
    {
        GameObject ceiling = Track(new GameObject("PlayerStanceTestCeiling"));
        ceiling.transform.position = new Vector3(0f, centerY, 0f);
        BoxCollider collider = ceiling.AddComponent<BoxCollider>();
        collider.size = new Vector3(2f, 0.2f, 2f);
        collider.isTrigger = isTrigger;
        Physics.SyncTransforms();
        return ceiling;
    }

    private GameObject Track(GameObject gameObject)
    {
        _objects.Add(gameObject);
        return gameObject;
    }

    private static void SetField(object target, string name, object value)
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing field '{name}'.");
        field.SetValue(target, value);
    }

    private static T GetField<T>(object target, string name)
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing field '{name}'.");
        return (T)field.GetValue(target);
    }

    private static void Invoke(object target, string name, params object[] arguments)
    {
        MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, $"Missing method '{name}'.");
        method.Invoke(target, arguments);
    }
}
