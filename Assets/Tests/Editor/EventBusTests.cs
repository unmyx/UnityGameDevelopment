using System.Reflection;
using Game.Core;
using NUnit.Framework;

public class EventBusTests
{
    private readonly struct TestEvent
    {
        public TestEvent(int value)
        {
            Value = value;
        }

        public int Value { get; }
    }

    [SetUp]
    public void SetUp()
    {
        ResetEventBus();
    }

    [TearDown]
    public void TearDown()
    {
        ResetEventBus();
    }

    [Test]
    public void SubscribePublishAndUnsubscribe_UsesTypeSafePayload()
    {
        int receivedValue = 0;
        System.Action<TestEvent> callback = eventData => receivedValue = eventData.Value;

        EventBus.Subscribe(callback);
        EventBus.Publish(new TestEvent(42));
        Assert.That(receivedValue, Is.EqualTo(42));

        EventBus.Unsubscribe(callback);
        EventBus.Publish(new TestEvent(99));
        Assert.That(receivedValue, Is.EqualTo(42));
    }

    [Test]
    public void Publish_NotifiesMultipleSubscribersOnceEach()
    {
        int firstCount = 0;
        int secondCount = 0;
        System.Action<TestEvent> first = _ => firstCount++;
        System.Action<TestEvent> second = _ => secondCount++;

        EventBus.Subscribe(first);
        EventBus.Subscribe(second);
        EventBus.Publish(new TestEvent(1));

        Assert.That(firstCount, Is.EqualTo(1));
        Assert.That(secondCount, Is.EqualTo(1));
    }

    [Test]
    public void PublishWithoutSubscriber_DoesNotThrow()
    {
        Assert.DoesNotThrow(() => EventBus.Publish(new TestEvent(1)));
    }

    [Test]
    public void SubsystemReset_RemovesExistingSubscribers()
    {
        int callbackCount = 0;
        EventBus.Subscribe<TestEvent>(_ => callbackCount++);
        EventBus.Publish(new TestEvent(1));
        Assert.That(callbackCount, Is.EqualTo(1));

        ResetEventBus();
        EventBus.Publish(new TestEvent(2));

        Assert.That(callbackCount, Is.EqualTo(1));
    }

    private static void ResetEventBus()
    {
        MethodInfo method = typeof(EventBus).GetMethod(
            "ResetSubscribers",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.That(method, Is.Not.Null);
        method.Invoke(null, null);
    }
}
