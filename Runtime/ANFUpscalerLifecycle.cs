//=============================================================================
//
// Copyright (c) Qualcomm Technologies, Inc. and/or its subsidiaries. 
// SPDX-License-Identifier: BSD-3-Clause-Clear
//
//=============================================================================

using System;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Qualcomm.ANF.Runtime
{
    internal interface IAnfNativeApi
    {
        int Initialise(out IntPtr handle);
        int SetEnabled(IntPtr handle, int enabled);
        int SetDispatchParams(IntPtr handle, uint sequenceId, float jitterX, float jitterY, int reset, uint depthFormat, int inputWidth, int inputHeight, int outputWidth, int outputHeight);
        void AbortDispatch(IntPtr handle, uint sequenceId);
        IntPtr GetRenderEventFunc();
        IntPtr GetCleanupEventFunc();
    }

    internal sealed class AnfNativeApi : IAnfNativeApi
    {
        private const string LibraryName = "anf_unity";

        [DllImport(LibraryName, EntryPoint = "AnfInitialise")]
        private static extern int AnfInitialise(out IntPtr handle);


        [DllImport(LibraryName, EntryPoint = "AnfSetEnabled")]
        private static extern int AnfSetEnabled(IntPtr handle, int enabled);

        internal const int DispatchSuccess = 0;
        internal const int DispatchEntryPointMissing = int.MinValue;

        [StructLayout(LayoutKind.Sequential)]
        private struct AnfDispatchParams
        {
            public uint sequenceId;
            public float jitterX;
            public float jitterY;
            public int reset;
            public uint depthFormat;
            public uint inputWidth;
            public uint inputHeight;
            public uint outputWidth;
            public uint outputHeight;
            public uint techniqueId;
            public uint clientApi;
            public uint maxInFlight;
            public uint techniqueFlags;
        }

        [DllImport(LibraryName, EntryPoint = "AnfSetDispatchParams")]
        private static extern int AnfSetDispatchParamsNative(IntPtr handle, ref AnfDispatchParams dispatchParams);

        [DllImport(LibraryName, EntryPoint = "AnfAbortDispatch")]
        private static extern void AnfAbortDispatch(IntPtr handle, uint sequenceId);

        [DllImport(LibraryName, EntryPoint = "AnfGetRenderEventFunc")]
        private static extern IntPtr AnfGetRenderEventFunc();

        [DllImport(LibraryName, EntryPoint = "AnfGetCleanupEventFunc")]
        private static extern IntPtr AnfGetCleanupEventFunc();

        public int Initialise(out IntPtr handle) => AnfInitialise(out handle);

        public int SetEnabled(IntPtr handle, int enabled) => AnfSetEnabled(handle, enabled);

        public int SetDispatchParams(IntPtr handle, uint sequenceId, float jitterX, float jitterY, int reset, uint depthFormat, int inputWidth, int inputHeight, int outputWidth, int outputHeight)
        {
            try
            {
                var p = new AnfDispatchParams
                {
                    sequenceId = sequenceId,
                    jitterX = jitterX,
                    jitterY = jitterY,
                    reset = reset,
                    depthFormat = depthFormat,
                    inputWidth = (uint)inputWidth,
                    inputHeight = (uint)inputHeight,
                    outputWidth = (uint)outputWidth,
                    outputHeight = (uint)outputHeight,
                    techniqueId = 0,
                    clientApi = 0,
                    // Unity rotates through three graphics command buffers on the target device.
                    // Give ANF a matching number of internal in-flight slots so a later frame
                    // cannot reuse technique resources while an earlier dispatch is still queued.
                    maxInFlight = 3,
                    techniqueFlags = 0,
                };
                return AnfSetDispatchParamsNative(handle, ref p);
            }
            catch (EntryPointNotFoundException)
            {
                ANFLogger.LogWarning("Upscaler: native AnfSetDispatchParams is unavailable; bypassing ANF.");
                return DispatchEntryPointMissing;
            }
        }

        public void AbortDispatch(IntPtr handle, uint sequenceId)
        {
            try { AnfAbortDispatch(handle, sequenceId); }
            catch (EntryPointNotFoundException) { }
        }

        public IntPtr GetRenderEventFunc() => AnfGetRenderEventFunc();

        public IntPtr GetCleanupEventFunc()
        {
            try { return AnfGetCleanupEventFunc(); }
            catch (EntryPointNotFoundException) { return IntPtr.Zero; }
        }
    }

    internal static class ANFUpscalerLifecycle
    {
        internal enum ShutdownReason
        {
            SubsystemRegistration,
            RenderPipelineDisposed,
            RenderPipelineReconcile,
            ExitingPlayMode,
            ExitingEditMode,
            BeforeAssemblyReload,
            ApplicationQuitting,
            DirectDispose,
            ContextCleanup,
            NonTerminalReinit,
            TestReset,
        }

        private static readonly object s_Gate = new object();
        private static IAnfNativeApi s_NativeApi = new AnfNativeApi();
        private static IntPtr s_RenderEventFunc = IntPtr.Zero;
        private static int s_MainThreadId;
        private static bool s_Subscribed;
        private static bool s_BypassGraphicsApiCheckForTests;
        private static int s_SubscriptionSetupCount;

        internal static Action<CommandBuffer, IntPtr, int, IntPtr> CleanupEventSchedulerForTests;

        internal static IAnfNativeApi NativeApi => s_NativeApi;
        internal static IntPtr RenderEventFunc => s_RenderEventFunc;
        internal static bool BypassGraphicsApiCheckForTests => s_BypassGraphicsApiCheckForTests;
        internal static int SubscriptionSetupCount { get { lock (s_Gate) { return s_SubscriptionSetupCount; } } }
        internal static int CurrentGeneration => 0;
        internal static long NextOrder => 0;
        internal static int TrackedInstanceCount => 0;
        internal static int TrackedContextCount => 0;
        internal static int ExpectedShutdownBypassEligibleCount => 0;

        internal static bool CanInstanceSchedule(ANFUpscaler instance) => true;
        internal static bool CanContextSchedule(ANFUpscalerContext context) => true;

        internal static void RegisterInstance(ANFUpscaler instance)
        {
            EnsureMainThreadCaptured();
            EnsureSubscribed();
        }

        internal static void UnregisterInstance(ANFUpscaler instance)
        {
            ZeroRenderEventFuncIfNoLiveHandles("UnregisterInstance");
        }

        internal static void RegisterContext(ANFUpscalerContext context, out int generation, out long order)
        {
            generation = 0;
            order = 0;
            EnsureMainThreadCaptured();
            EnsureSubscribed();
        }

        internal static void UnregisterContext(ANFUpscalerContext context)
        {
            ZeroRenderEventFuncIfNoLiveHandles("UnregisterContext");
        }

        internal static void EnsureSubscribed()
        {
            EnsureMainThreadCaptured();
            if (s_Subscribed)
            {
                return;
            }

            RenderPipelineManager.activeRenderPipelineDisposed -= OnActiveRenderPipelineDisposed;
            RenderPipelineManager.activeRenderPipelineDisposed += OnActiveRenderPipelineDisposed;
#if UNITY_EDITOR
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
#endif
            Application.quitting -= OnApplicationQuitting;
            Application.quitting += OnApplicationQuitting;
            lock (s_Gate)
            {
                s_SubscriptionSetupCount++;
                s_Subscribed = true;
            }
            ANFLogger.LogInfo("Upscaler lifecycle: subscribed lifecycle hooks.");
        }

        internal static void SubsystemRegistrationReset()
        {
            EnsureMainThreadCaptured();
            lock (s_Gate)
            {
                s_RenderEventFunc = IntPtr.Zero;
                s_Subscribed = false;
                s_SubscriptionSetupCount = 0;
            }
            ANFLogger.LogInfo("Upscaler lifecycle: SubsystemRegistration cleanup-before-static-reset complete.");
        }

        internal static IntPtr EnsureRenderEventFunc(string reason)
        {
            AssertMainThread($"EnsureRenderEventFunc:{reason}");
            if (s_RenderEventFunc != IntPtr.Zero)
            {
                return s_RenderEventFunc;
            }

            s_RenderEventFunc = s_NativeApi.GetRenderEventFunc();
            ANFLogger.LogInfo($"Upscaler lifecycle: render-event function fetched/refetched for {reason}: {s_RenderEventFunc}.");
            return s_RenderEventFunc;
        }

        internal static void ShutdownAll(ShutdownReason reason, bool terminal, int? generationGuard = null, long? createdBeforeOrEqual = null, bool prune = true)
        {
            if (terminal)
            {
                ZeroRenderEventFuncIfNoLiveHandles($"terminal {reason}");
            }
        }

        internal static void ShutdownAllContexts(ShutdownReason reason, bool terminal, int? generationGuard = null, long? createdBeforeOrEqual = null, bool prune = true)
        {
            if (terminal)
            {
                ZeroRenderEventFuncIfNoLiveHandles($"terminal-contexts {reason}");
            }
        }

        internal static void ShutdownContextsOwnedBy(ANFUpscaler owner, ShutdownReason reason, bool terminal)
        {
            if (terminal)
            {
                ZeroRenderEventFuncIfNoLiveHandles($"terminal-owned-contexts {reason}");
            }
        }

        internal static void AssertMainThread(string operation)
        {
            EnsureMainThreadCaptured();
            if (Thread.CurrentThread.ManagedThreadId == s_MainThreadId)
            {
                return;
            }

            string message = $"ANF lifecycle native operation '{operation}' attempted off Unity main thread (current={Thread.CurrentThread.ManagedThreadId}, main={s_MainThreadId}).";
            Debug.Assert(false, message);
            throw new InvalidOperationException(message);
        }

        private static void EnsureMainThreadCaptured()
        {
            Interlocked.CompareExchange(ref s_MainThreadId, Thread.CurrentThread.ManagedThreadId, 0);
        }

        private static void OnActiveRenderPipelineDisposed()
        {
            ZeroRenderEventFuncIfNoLiveHandles("RenderPipelineDisposed");
        }

        internal static bool TryGetActiveExpectedTerminalShutdown(out long token, out ShutdownReason reason)
        {
            token = 0;
            reason = default;
            return false;
        }

        internal static bool IsExpectedShutdownTokenActive(long token) => false;

        internal static bool IsExpectedLifecycleShutdownReason(ShutdownReason reason)
        {
            return reason == ShutdownReason.ExitingPlayMode ||
                   reason == ShutdownReason.ExitingEditMode ||
                   reason == ShutdownReason.BeforeAssemblyReload ||
                   reason == ShutdownReason.ApplicationQuitting ||
                   reason == ShutdownReason.SubsystemRegistration;
        }

        internal static bool ShouldDowngradeNullCommandBufferShutdown(ShutdownReason reason)
        {
            return IsExpectedLifecycleShutdownReason(reason);
        }

        internal static bool CanCreateExpectedShutdownBypass(ANFUpscaler instance, long token, out ShutdownReason reason)
        {
            reason = default;
            return false;
        }

        private static long BeginExpectedTerminalShutdown(ShutdownReason reason)
        {
            return 0;
        }

        private static void ClearExpectedTerminalShutdown()
        {
        }

        internal static void ShutdownPipelineGeneration(int generation, long cutoff)
        {
            ZeroRenderEventFuncIfNoLiveHandles("ShutdownPipelineGeneration");
        }

#if UNITY_EDITOR
        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingPlayMode || change == PlayModeStateChange.ExitingEditMode)
            {
                ZeroRenderEventFuncIfNoLiveHandles($"PlayMode:{change}");
            }
        }

        private static void OnBeforeAssemblyReload()
        {
            ZeroRenderEventFuncIfNoLiveHandles("BeforeAssemblyReload");
        }
#endif

        private static void OnApplicationQuitting()
        {
            ZeroRenderEventFuncIfNoLiveHandles("ApplicationQuitting");
        }

        private static void ZeroRenderEventFuncIfNoLiveHandles(string reason)
        {
            lock (s_Gate)
            {
                if (s_RenderEventFunc != IntPtr.Zero)
                {
                    ANFLogger.LogInfo($"Upscaler lifecycle: zeroing render-event function after {reason}; no live handles remain.");
                }

                s_RenderEventFunc = IntPtr.Zero;
            }
        }

#if UNITY_EDITOR || DEBUG
        internal static long BeginExpectedTerminalShutdownForTests(ShutdownReason reason)
        {
            return BeginExpectedTerminalShutdown(reason);
        }

        internal static void ClearExpectedTerminalShutdownForTests()
        {
            ClearExpectedTerminalShutdown();
        }

#if UNITY_EDITOR
        internal static void NotifyPlayModeStateChangedForTests(PlayModeStateChange change)
        {
            OnPlayModeStateChanged(change);
        }
#endif

        internal static void SetNativeApiForTests(IAnfNativeApi nativeApi)
        {
            s_NativeApi = nativeApi ?? new AnfNativeApi();
            s_RenderEventFunc = IntPtr.Zero;
            s_BypassGraphicsApiCheckForTests = false;
        }

        internal static void ResetForTests(IAnfNativeApi nativeApi = null, bool bypassGraphicsApiCheck = false)
        {
            Interlocked.Exchange(ref s_MainThreadId, Thread.CurrentThread.ManagedThreadId);
            lock (s_Gate)
            {
                s_RenderEventFunc = IntPtr.Zero;
                s_Subscribed = false;
                s_SubscriptionSetupCount = 0;
            }

            CleanupEventSchedulerForTests = null;
            s_NativeApi = nativeApi ?? new AnfNativeApi();
            s_BypassGraphicsApiCheckForTests = bypassGraphicsApiCheck;
        }

        internal static void AdvanceGenerationForTests()
        {
        }
#endif
    }
}
