using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;

namespace Game.Networking
{
    /// <summary>
    /// Reflection-backed Relay bridge to keep compile safety when Unity Services packages vary.
    /// </summary>
    public static class NetworkRelayService
    {
        public readonly struct RelayHostStartResult
        {
            public RelayHostStartResult(bool success, object relayServerData, string joinCode, string error)
            {
                Success = success;
                RelayServerData = relayServerData;
                JoinCode = joinCode ?? string.Empty;
                Error = error ?? string.Empty;
            }

            public bool Success { get; }
            public object RelayServerData { get; }
            public string JoinCode { get; }
            public string Error { get; }
        }

        public readonly struct RelayClientJoinResult
        {
            public RelayClientJoinResult(bool success, object relayServerData, string error)
            {
                Success = success;
                RelayServerData = relayServerData;
                Error = error ?? string.Empty;
            }

            public bool Success { get; }
            public object RelayServerData { get; }
            public string Error { get; }
        }

        public static async Task<RelayHostStartResult> PrepareHostAsync(int maxPeers, string connectionType)
        {
            try
            {
                await InitializeServicesAndAuthenticateAsync();
                object relayInstance = GetRelayServiceInstance();
                MethodInfo createAllocation = FindMethod(relayInstance.GetType(), "CreateAllocationAsync", typeof(int));
                object allocationTask = InvokeWithPrimaryArgument(createAllocation, relayInstance, maxPeers);
                object allocation = await AwaitTaskWithResultAsync(allocationTask);

                object allocationId = GetPropertyValue(allocation, "AllocationId");
                MethodInfo getJoinCode = FindMethod(relayInstance.GetType(), "GetJoinCodeAsync", allocationId.GetType());
                object joinCodeTask = InvokeWithPrimaryArgument(getJoinCode, relayInstance, allocationId);
                object joinCodeResult = await AwaitTaskWithResultAsync(joinCodeTask);
                string joinCode = joinCodeResult as string ?? string.Empty;

                object relayServerData = CreateRelayServerData(allocation, connectionType);
                return new RelayHostStartResult(true, relayServerData, joinCode, string.Empty);
            }
            catch (Exception ex)
            {
                string error = BuildErrorMessage(ex);
                Debug.LogError($"[NetworkRelayService] Host relay preparation failed: {error}");
                return new RelayHostStartResult(false, null, string.Empty, error);
            }
        }

        public static async Task<RelayClientJoinResult> PrepareClientAsync(string joinCode, string connectionType)
        {
            if (string.IsNullOrWhiteSpace(joinCode))
            {
                return new RelayClientJoinResult(false, null, "Enter a valid Relay join code.");
            }

            try
            {
                await InitializeServicesAndAuthenticateAsync();
                object relayInstance = GetRelayServiceInstance();
                MethodInfo joinAllocation = FindMethod(relayInstance.GetType(), "JoinAllocationAsync", typeof(string));
                object joinTask = InvokeWithPrimaryArgument(joinAllocation, relayInstance, joinCode.Trim());
                object joinAllocationResult = await AwaitTaskWithResultAsync(joinTask);

                object relayServerData = CreateRelayServerData(joinAllocationResult, connectionType);
                return new RelayClientJoinResult(true, relayServerData, string.Empty);
            }
            catch (Exception ex)
            {
                string error = BuildErrorMessage(ex);
                Debug.LogError($"[NetworkRelayService] Client relay preparation failed: {error}");
                return new RelayClientJoinResult(false, null, error);
            }
        }

        private static async Task InitializeServicesAndAuthenticateAsync()
        {
            Type unityServicesType = FindType(
                "Unity.Services.Core.UnityServices",
                "Unity.Services.Core.UnityServices, Unity.Services.Core");
            MethodInfo initializeAsync = unityServicesType.GetMethod("InitializeAsync", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (initializeAsync == null)
            {
                throw new InvalidOperationException("Unity Services Core is not available. Install Unity Services Multiplayer package.");
            }

            object initTask = initializeAsync.Invoke(null, null);
            await AwaitTaskAsync(initTask);

            Type authenticationType = FindType(
                "Unity.Services.Authentication.AuthenticationService",
                "Unity.Services.Authentication.AuthenticationService, Unity.Services.Authentication");
            object authInstance = authenticationType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (authInstance == null)
            {
                throw new InvalidOperationException("Unity Authentication service is unavailable.");
            }

            bool isSignedIn = (bool)(authInstance.GetType().GetProperty("IsSignedIn", BindingFlags.Public | BindingFlags.Instance)?.GetValue(authInstance) ?? false);
            if (!isSignedIn)
            {
                MethodInfo signIn = authInstance.GetType().GetMethod("SignInAnonymouslyAsync", BindingFlags.Public | BindingFlags.Instance);
                if (signIn == null)
                {
                    throw new InvalidOperationException("Authentication SignInAnonymouslyAsync API is unavailable.");
                }

                object signInTask = signIn.GetParameters().Length == 0
                    ? signIn.Invoke(authInstance, null)
                    : signIn.Invoke(authInstance, new object[] { null });
                await AwaitTaskAsync(signInTask);
            }
        }

        private static object GetRelayServiceInstance()
        {
            Type relayServiceType = FindType(
                "Unity.Services.Relay.RelayService",
                "Unity.Services.Relay.RelayService, Unity.Services.Relay");
            object relayInstance = relayServiceType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (relayInstance == null)
            {
                throw new InvalidOperationException("Unity Relay service is unavailable.");
            }

            return relayInstance;
        }

        private static object CreateRelayServerData(object allocation, string connectionType)
        {
            Type relayServerDataType = FindType(
                "Unity.Networking.Transport.Relay.RelayServerData",
                "Unity.Networking.Transport.Relay.RelayServerData, Unity.Transport");

            ConstructorInfo constructor = relayServerDataType
                .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(c =>
                {
                    ParameterInfo[] parameters = c.GetParameters();
                    return parameters.Length == 2
                           && parameters[0].ParameterType.IsAssignableFrom(allocation.GetType())
                           && parameters[1].ParameterType == typeof(string);
                });

            if (constructor == null)
            {
                throw new InvalidOperationException("RelayServerData allocation constructor was not found.");
            }

            return constructor.Invoke(new object[] { allocation, connectionType });
        }

        private static MethodInfo FindMethod(Type type, string methodName, Type firstParameterType)
        {
            MethodInfo method = type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m =>
                {
                    if (!string.Equals(m.Name, methodName, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    ParameterInfo[] parameters = m.GetParameters();
                    return parameters.Length >= 1 && parameters[0].ParameterType.IsAssignableFrom(firstParameterType);
                });

            if (method == null)
            {
                throw new MissingMethodException(type.FullName, methodName);
            }

            return method;
        }

        private static Type FindType(params string[] typeNames)
        {
            foreach (string typeName in typeNames)
            {
                if (string.IsNullOrWhiteSpace(typeName))
                {
                    continue;
                }

                Type resolved = Type.GetType(typeName);
                if (resolved != null)
                {
                    return resolved;
                }

                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    resolved = assembly.GetType(typeName);
                    if (resolved != null)
                    {
                        return resolved;
                    }
                }
            }

            throw new TypeLoadException($"Unable to resolve required type. Tried: {string.Join(", ", typeNames)}");
        }

        private static object InvokeWithPrimaryArgument(MethodInfo method, object target, object primaryArgument)
        {
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length == 0)
            {
                return method.Invoke(target, null);
            }

            object[] args = new object[parameters.Length];
            args[0] = primaryArgument;
            for (int i = 1; i < parameters.Length; i++)
            {
                if (parameters[i].HasDefaultValue)
                {
                    args[i] = parameters[i].DefaultValue;
                    continue;
                }

                args[i] = parameters[i].ParameterType.IsValueType
                    ? Activator.CreateInstance(parameters[i].ParameterType)
                    : null;
            }

            return method.Invoke(target, args);
        }

        private static object GetPropertyValue(object instance, string propertyName)
        {
            if (instance == null)
            {
                throw new ArgumentNullException(nameof(instance));
            }

            PropertyInfo property = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property == null)
            {
                throw new MissingMemberException(instance.GetType().FullName, propertyName);
            }

            return property.GetValue(instance);
        }

        private static async Task AwaitTaskAsync(object taskObject)
        {
            if (taskObject is not Task task)
            {
                throw new InvalidOperationException("Expected Task return type from Unity Services API.");
            }

            await task.ConfigureAwait(false);
        }

        private static async Task<object> AwaitTaskWithResultAsync(object taskObject)
        {
            if (taskObject is not Task task)
            {
                throw new InvalidOperationException("Expected Task return type from Unity Services API.");
            }

            await task.ConfigureAwait(false);
            PropertyInfo resultProperty = task.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
            if (resultProperty == null)
            {
                throw new InvalidOperationException("Expected Task<T> return type from Unity Services API.");
            }

            return resultProperty.GetValue(task);
        }

        private static string BuildErrorMessage(Exception exception)
        {
            Exception root = exception;
            while (root.InnerException != null)
            {
                root = root.InnerException;
            }

            return root.Message;
        }
    }
}
