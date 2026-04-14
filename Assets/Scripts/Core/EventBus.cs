using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Core
{
    /// <summary>
    /// Type-safe event bus using event payload type as the routing key.
    /// </summary>
    public static class EventBus
    {
        private static readonly Dictionary<Type, Delegate> Subscribers = new Dictionary<Type, Delegate>();

        public static void Subscribe<T>(Action<T> callback)
        {
            if (callback == null)
            {
                return;
            }

            Type eventType = typeof(T);
            if (Subscribers.TryGetValue(eventType, out Delegate existing))
            {
                Subscribers[eventType] = (Action<T>)existing + callback;
                return;
            }

            Subscribers[eventType] = callback;
        }

        public static void Unsubscribe<T>(Action<T> callback)
        {
            if (callback == null)
            {
                return;
            }

            Type eventType = typeof(T);
            if (!Subscribers.TryGetValue(eventType, out Delegate existing))
            {
                return;
            }

            Action<T> updated = (Action<T>)existing - callback;
            if (updated == null)
            {
                Subscribers.Remove(eventType);
                return;
            }

            Subscribers[eventType] = updated;
        }

        public static void Publish<T>(T eventData)
        {
            Type eventType = typeof(T);
            if (!Subscribers.TryGetValue(eventType, out Delegate existing))
            {
                return;
            }

            if (!(existing is Action<T> callbacks))
            {
                return;
            }

            Delegate[] invocationList = callbacks.GetInvocationList();
            for (int i = 0; i < invocationList.Length; i++)
            {
                Action<T> callback = invocationList[i] as Action<T>;
                if (callback == null)
                {
                    continue;
                }

                try
                {
                    callback(eventData);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }
    }
}
