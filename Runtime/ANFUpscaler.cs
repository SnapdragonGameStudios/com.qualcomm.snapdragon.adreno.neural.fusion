//=============================================================================
//
// Copyright (c) Qualcomm Technologies, Inc. and/or its subsidiaries. 
// SPDX-License-Identifier: BSD-3-Clause-Clear
//
//=============================================================================

using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Scripting;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Qualcomm.ANF.Runtime
{
    /// <summary>
    /// ANF Upscaler integration for Unity 6.6's Upscaler Framework.
    /// </summary>
    public class ANFUpscaler : AbstractUpscaler
    {
        public const string UpscalerName = "ANF Upscaler";


        // AnfFormat enum values that correspond to depth buffer formats.
        // These must match the AnfFormat enum in the native ANF SDK headers.
        private const uint _anfFormatD32Float = 3;   // ANF_FORMAT_D32_FLOAT
        private const uint _anfFormatD24S8Unorm = 4; // ANF_FORMAT_D24S8_UNORM

        public override string name => UpscalerName;
        public override bool isTemporal => true;
        public override bool supportsSharpening => false;
        public override bool hasQualityMode => true;

        /// <summary>
        /// Delegates to Unity's built-in STP jitter pattern. Dispatch converts the
        /// framework-provided <c>UpscalingIO.subpixelJitter</c> value to the
        /// ANF SDK jitter convention at the managed/native boundary; the upscaler
        /// singleton does not cache temporal state.
        /// </summary>
        public override void CalculateJitter(int frameIndex, float upscaleRatio, out Vector2 jitter, out bool allowScaling)
        {
            base.CalculateJitter(frameIndex, upscaleRatio, out jitter, out allowScaling);
#if UNITY_EDITOR || DEBUG
            if (Time.frameCount % 30 == 0 && ANFLogger.IsEnabled(AnfLogLevel.Trace))
            {
                ANFLogger.LogTrace($"CalculateJitter: frameIndex={frameIndex} upscaleRatio={upscaleRatio:F3} jitter=({jitter.x:F6},{jitter.y:F6}) allowScaling={allowScaling}");
            }
#endif
        }

        public override IUpscalerContext CreateContext(UpscalerOptions options, Vector2Int displayResolution)
        {
            if (options != null && !(options is ANFUpscalerOptions))
            {
                throw new ArgumentException($"ANF upscaler requires {nameof(ANFUpscalerOptions)} options when options are provided.", nameof(options));
            }

            return new ANFUpscalerContext(displayResolution);
        }

        public override UpscalerResolutionInfo GetResolutionInfo(Vector2Int displayResolution, UpscalerOptions options)
        {
            if (displayResolution.x > 0 && displayResolution.y > 0 &&
                (displayResolution.x & 1) == 0 && (displayResolution.y & 1) == 0)
            {
                return UpscalerResolutionInfo.Fixed(new Vector2Int(displayResolution.x / 2, displayResolution.y / 2));
            }

            return base.GetResolutionInfo(displayResolution, options);
        }

        public override float CalculateMipBias(Vector2Int preUpscaleResolution, Vector2Int postUpscaleResolution)
        {
            // Phase A1 decision: ANF uses AbstractUpscaler's default log2 render/display bias
            // for exact 2x upscaling. Do not apply the DLSS/FSR2 extra -1.0f without visual
            // tuning or SDK-specific evidence.
            return base.CalculateMipBias(preUpscaleResolution, postUpscaleResolution);
        }

        public ANFUpscaler()
        {
            ANFLogger.LogInfo("Upscaler: ANFUpscaler constructor called. Per-camera native initialization is deferred to ANFUpscalerContext first use.");
        }

        /// <summary>
        /// Supplemental direct cleanup entrypoint. Unity's standard upscaler framework stores
        /// instances as IUpscaler/AbstractUpscaler and does not call this automatically, so
        /// deterministic cleanup is owned by <see cref="ANFUpscalerLifecycle"/>.
        /// </summary>
        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }

        internal bool TrySetDispatchParamsForTests(ANFUpscalerContext context, uint sequenceId)
        {
            if (context == null || !context.IsNativeReadyForScheduling)
            {
                return false;
            }
            bool setDispatchParams = TrySetNativeDispatchParams(
                context,
                sequenceId,
                dispatchJitter: Vector2.zero,
                effectiveReset: false,
                depthFormat: _anfFormatD32Float,
                preUpscaleResolution: new Vector2Int(960, 540),
                postUpscaleResolution: new Vector2Int(1920, 1080));

            return setDispatchParams;
        }


        // No finalizer: calling Vulkan teardown functions from a GC finalizer
        // thread is unsafe and can crash the graphics driver. Callers must
        // invoke Dispose() explicitly (the Upscaler Framework calls Dispose on
        // upscaler instances when they are deregistered or the pipeline tears down).

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (renderGraph == null)
            {
                ANFLogger.LogWarning("RecordRenderGraph: renderGraph is null. Upscaler will be skipped.");
                return;
            }

            if (frameData == null)
            {
                ANFLogger.LogWarning("RecordRenderGraph: frameData is null. Upscaler will be skipped.");
                return;
            }

            if (!frameData.Contains<UpscalingIO>())
            {
                ANFLogger.LogWarning("RecordRenderGraph: frameData does NOT contain UpscalingIO. Upscaler will be skipped.");
                return;
            }

            var io = frameData.Get<UpscalingIO>();

            // ── FrameState boundary log (throttled every 30 frames) ───────────
            // Provides hard evidence of what the upscaler sees at the RG boundary:
            // screen dimensions, orientation, pre/post upscale resolutions, and
            // whether the cameraColor handle is valid.
#if UNITY_EDITOR || DEBUG
            if (Time.frameCount % 30 == 0 && ANFLogger.IsEnabled(AnfLogLevel.Trace))
            {
                string colorDesc = "invalid";
                if (io.cameraColor.IsValid())
                {
                    try
                    {
                        var cd = renderGraph.GetTextureDesc(io.cameraColor);
                        colorDesc = $"fmt={cd.colorFormat} size={cd.width}x{cd.height}";
                    }
                    catch (System.Exception ex)
                    {
                        colorDesc = $"GetTextureDesc threw: {ex.GetType().Name}";
                    }
                }
                ANFLogger.LogTrace(
                    $"FrameState: frame={Time.frameCount} " +
                    $"Screen={Screen.width}x{Screen.height} orient={Screen.orientation} " +
                    $"preUpscale={io.preUpscaleResolution} postUpscale={io.postUpscaleResolution} " +
                    $"cameraColor.IsValid={io.cameraColor.IsValid()} [{colorDesc}]");
            }
#endif

            if (!(io.context is ANFUpscalerContext anfContext))
            {
                ANFLogger.LogWarning("Upscaler: UpscalingIO.context is missing or not an ANFUpscalerContext; bypassing.");
                return;
            }

            // ── ANF-internal readiness check ─────────────────────────────────
            // ANF decides here whether it can upscale this frame.  Each check
            // below is an ANF-internal degradation: the upscaler reports WHY it
            // cannot produce output, then returns before native init/output allocation.
            // This separation prevents ANF from silently recording pipeline-level
            // fallback events and ensures the decision layer (ANF) is distinct
            // from the execution layer (bypass blit).
            if (!ValidateUnsupportedPath(io.numActiveViews, io.dynamicResolution, io.preUpscaleResolution, io.postUpscaleResolution, out string unsupportedReason))
            {
                ANFLogger.LogWarning($"Upscaler: unsupported ANF dispatch path ({unsupportedReason}); bypassing.");
                return;
            }

            // Validate input texture formats before scheduling the pass.
            // A format mismatch is an ANF-internal constraint: the SDK requires
            // specific input formats that the current pipeline configuration does
            // not satisfy.
            if (!ValidateFormats(renderGraph, io, out uint depthFormat))
            {
                // ValidateFormats already logs the specific format mismatch.
                return;
            }

            // Native initialization is mandatory-lazy: only initialize once ANF is actually
            // active and render prerequisites have reached RecordRenderGraph. If this frame
            // cannot initialize (for example non-Vulkan), later valid frames may retry.
            if (!anfContext.EnsureNativeInitialized("RecordRenderGraph"))
            {
                ANFLogger.LogWarning("Upscaler: ANF SDK handle/render event is not initialised — cannot upscale this frame.");
                return;
            }

            var outputRes = io.postUpscaleResolution;

            // ── SDK output texture (B10G11R11_UFloatPack32) ──────────────────
            // The native SDK declares its output resource as ANF_FORMAT_B10G11R11_UFLOAT
            // (see AnfBackendAdapter.cpp).  The SDK rejects R16G16B16A16_FLOAT for the
            // output resource on-device ("Invalid format 6 provided for input resource
            // with label (3)" → DispatchTechnique fails with INVALID_PARAMETER).  The
            // working Unreal SR path also creates the output as PF_FloatR11G11B10 by
            // default.  We MUST create the SDK output texture in the matching format so
            // the SDK's VkImage format check passes and it writes valid data.
            var sdkOutputDesc = new TextureDesc(outputRes.x, outputRes.y)
            {
                colorFormat = GraphicsFormat.B10G11R11_UFloatPack32,
                enableRandomWrite = true,
                clearBuffer = false,
                name = "ANF_SDKOutput",
                dimension = TextureDimension.Tex2D,
                slices = 1,
                depthBufferBits = DepthBits.None,
                msaaSamples = MSAASamples.None,
                useMipMap = false,
                autoGenerateMips = false,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 1,
            };
            TextureHandle sdkOutput = renderGraph.CreateTexture(sdkOutputDesc);

            uint seq = ANFUpscalerContext.AllocateSequenceId();

            // ── resetHistory logic ────────────────────────────────────────────
            // Pass io.resetHistory directly to the SDK as a level-triggered reset
            // signal.  URP sets io.resetHistory=true for one or more frames after
            // a camera cut or new history allocation (see UniversalRenderPipeline.cs
            // resetHistoryFrames counter), so the SDK receives reset=1 on every
            // frame where URP requests a history flush — including the very first
            // frame after activation.  No rising-edge tracking is needed.
            bool effectiveReset = io.resetHistory;
            if (effectiveReset)
            {
                Debug.Log("ANF: Reset");
            }
            var dispatchJitter = GetDispatchJitter(io.subpixelJitter);

            // Diagnostic log — throttled to every 30 frames to avoid logcat spam.
            // Includes SDK output and converted output descriptor format/dimensions so
            // the Tester can confirm the format pipeline on device via logcat.
            // Shows the jitter values that are passed through to the SDK.
#if UNITY_EDITOR || DEBUG
            var inputColorDesc = renderGraph.GetTextureDesc(io.cameraColor);
            if (Time.frameCount % 30 == 0 && ANFLogger.IsEnabled(AnfLogLevel.Trace))
            {
                ANFLogger.LogTrace(
                    $"RecordRenderGraph: seq={seq} " +
                    $"jitter=({io.subpixelJitter.x:F6},{io.subpixelJitter.y:F6}) " +
                    $"sdkJitter=({dispatchJitter.x:F6},{dispatchJitter.y:F6}) " +
                    $"io.resetHistory={io.resetHistory} effectiveReset={effectiveReset} " +
                    $"depthFormat={depthFormat} " +
                    $"preRes={io.preUpscaleResolution} postRes={io.postUpscaleResolution} " +
                    $"inputFmt={inputColorDesc.colorFormat} " +
                    $"inputSize={inputColorDesc.width}x{inputColorDesc.height} " +
                    $"sdkOutputFmt={sdkOutputDesc.colorFormat} " +
                    $"sdkOutputSize={sdkOutputDesc.width}x{sdkOutputDesc.height}");
            }
#endif

            // Pass Unity framework-provided jitter through unchanged to the native SDK.
            if (!TrySetNativeDispatchParams(
                    anfContext,
                    seq,
                    dispatchJitter,
                    effectiveReset,
                    depthFormat,
                    io.preUpscaleResolution,
                    io.postUpscaleResolution))
            {
                return;
            }

            try
            {
                using (var builder = renderGraph.AddUnsafePass<PassData>("ANF Upscale Pass", out var passData))
                {
                    passData.inputColor = io.cameraColor;
                    passData.depth = io.cameraDepth;
                    passData.motionVectors = io.motionVectorColor;
                    passData.outputColor = sdkOutput;
                    passData.sequenceId = seq;
                    passData.renderEventFunc = ANFUpscalerLifecycle.RenderEventFunc;

                    builder.UseTexture(passData.inputColor, AccessFlags.Read);
                    builder.UseTexture(passData.depth, AccessFlags.Read);
                    builder.UseTexture(passData.motionVectors, AccessFlags.Read);
                    builder.UseTexture(passData.outputColor, AccessFlags.Write);

                    builder.AllowPassCulling(false);

                    builder.SetRenderFunc((PassData data, UnsafeGraphContext context) =>
                    {
                        var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

                        // Note: The ANF SDK runs in command-list mode, not immediate-dispatch mode
                        // (AnfTechniqueCreateInfo.flags never sets ANF_TECHNIQUE_CREATE_FLAG_DISPATCH_IMMEDIATE;
                        // see HasSupportedTechniqueCreateInputs in AnfUnityNativeBridge.cpp, which requires
                        // techniqueFlags == 0). AnfDispatchTechnique records its compute work (plus its own
                        // internal pre/post barriers) directly into the same Unity-owned VkCommandBuffer that
                        // IssuePluginCustomBlit hands to the native callback — it does not submit its own
                        // command buffer or require explicit semaphore wait/signal handoff with Unity.
                        // We still MUST ensure outputDesc.clearBuffer = false so Unity does not clear/overwrite
                        // the SDK's output before or after the native write.

                        // Bind each texture as the active render target before passing it as
                        // the dest argument to IssuePluginCustomBlit.  Unity resolves the
                        // dest UnityRenderBuffer pointer only when the texture is currently
                        // bound; without this binding, AccessRenderBufferTexture in the
                        // native bridge receives a null/stale pointer and resolves the wrong
                        // VkImage, causing black output.

                        // Command 0: SetInputColor — bind inputColor so Unity resolves its
                        // UnityRenderBuffer pointer, then pass it as both source and dest so
                        // the native bridge stores it as m_inputColorHandle for the Upscale
                        // command.  commandParam=sequenceId.
                        cmd.SetRenderTarget(data.inputColor);
                        cmd.IssuePluginCustomBlit(data.renderEventFunc, 0, data.inputColor, data.inputColor, data.sequenceId, 0);

                        // Command 1: SetDepthMv — bind motionVectors so Unity resolves its
                        // UnityRenderBuffer pointer, then pass depth as source and motion
                        // vectors as dest.  command=1 (SetDepthMv), commandParam=sequenceId,
                        //
                        cmd.SetRenderTarget(data.motionVectors);
                        cmd.IssuePluginCustomBlit(data.renderEventFunc, 1, data.depth, data.motionVectors, data.sequenceId, 0);

                        // Command 2: Upscale — bind outputColor so Unity resolves its
                        // UnityRenderBuffer pointer, then pass SDK output as dest.  The SDK
                        // writes its upscaled result into sdkOutput which matches the native
                        // ANF_FORMAT_B10G11R11_UFLOAT declaration in AnfBackendAdapter.cpp.
                        // command=2 (Upscale), commandParam=sequenceId.
                        cmd.SetRenderTarget(data.outputColor);
                        cmd.IssuePluginCustomBlit(data.renderEventFunc, 2, data.inputColor, data.outputColor, data.sequenceId, 0);
                    });
                }
            }
            catch (System.Exception ex)
            {
                ANFLogger.LogWarning($"Upscaler: failed to record ANF passes for seq={seq}; aborting prepared native dispatch. {ex.GetType().Name}: {ex.Message}");
                ANFUpscalerLifecycle.NativeApi.AbortDispatch(anfContext.NativeHandle, seq);
                return;
            }

            io.cameraColor = sdkOutput;
        }

        private static bool TrySetNativeDispatchParams(
            ANFUpscalerContext context,
            uint sequenceId,
            Vector2 dispatchJitter,
            bool effectiveReset,
            uint depthFormat,
            Vector2Int preUpscaleResolution,
            Vector2Int postUpscaleResolution)
        {
            if (!context.IsNativeReadyForScheduling)
            {
                ANFLogger.LogWarning($"Upscaler: context lifecycle gate blocks SetDispatchParams/scheduling for seq={sequenceId}.");
                return false;
            }

            int setParamsResult = ANFUpscalerLifecycle.NativeApi.SetDispatchParams(
                context.NativeHandle,
                sequenceId,
                dispatchJitter.x,
                dispatchJitter.y,
                effectiveReset ? 1 : 0,
                depthFormat,
                preUpscaleResolution.x,
                preUpscaleResolution.y,
                postUpscaleResolution.x,
                postUpscaleResolution.y);

            if (setParamsResult != AnfNativeApi.DispatchSuccess)
            {
                ANFLogger.LogWarning($"Upscaler: native dispatch params rejected seq={sequenceId} rc={setParamsResult}; bypassing.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Maps a Unity <see cref="DepthBits"/> value to the corresponding ANF depth
        /// format constant.  Extracted from <see cref="ValidateFormats"/> so that the
        /// mapping can be exercised by Editor (EditMode) unit tests without requiring a
        /// live <see cref="RenderGraph"/> or native DLL.
        /// </summary>
        /// <param name="bits">The depth-buffer bit depth reported by the texture descriptor.</param>
        /// <param name="format">
        ///   On success, set to <c>_anfFormatD32Float</c> (3) for 32-bit depth or
        ///   <c>_anfFormatD24S8Unorm</c> (4) for 24-bit depth.
        ///   On failure, set to <c>0</c>.
        /// </param>
        /// <returns>
        ///   <c>true</c> when <paramref name="bits"/> is <see cref="DepthBits.Depth32"/> or
        ///   <see cref="DepthBits.Depth24"/>; <c>false</c> for any other value.
        /// </returns>
        internal static bool TryGetAnfDepthFormat(DepthBits bits, out uint format)
        {
            if (bits == DepthBits.Depth32)
            {
                format = _anfFormatD32Float;
                return true;
            }

            if (bits == DepthBits.Depth24)
            {
                format = _anfFormatD24S8Unorm;
                return true;
            }

            format = 0;
            return false;
        }

        internal static Vector2 GetDispatchJitter(Vector2 subpixelJitter)
        {
            // Unity 6000.6 supplies pipeline-space jitter in UpscalingIO.subpixelJitter.
            // On-device validation shows the ANF SDK expects the X axis in the
            // opposite convention while preserving Y. Keep that Unity-to-SDK
            // conversion explicit and testable at the managed/native dispatch boundary.
            return new Vector2(-subpixelJitter.x, subpixelJitter.y);
        }

        internal static bool ValidateUnsupportedPath(int numActiveViews, DynamicResolutionType? dynamicResolution, Vector2Int preUpscaleResolution, Vector2Int postUpscaleResolution, out string reason)
        {
            if (numActiveViews != 1)
            {
                reason = $"XR/multi-view is not supported (numActiveViews={numActiveViews})";
                return false;
            }

            if (dynamicResolution.HasValue)
            {
                reason = $"dynamic resolution is not supported (type={dynamicResolution.Value})";
                return false;
            }

            if (preUpscaleResolution.x <= 0 || preUpscaleResolution.y <= 0 ||
                postUpscaleResolution.x <= 0 || postUpscaleResolution.y <= 0)
            {
                reason = $"invalid nonpositive dimensions pre={preUpscaleResolution} post={postUpscaleResolution}";
                return false;
            }

            if ((postUpscaleResolution.x & 1) != 0 || (postUpscaleResolution.y & 1) != 0)
            {
                reason = $"odd post-upscale dimensions post={postUpscaleResolution}";
                return false;
            }

            if (postUpscaleResolution.x != preUpscaleResolution.x * 2 ||
                postUpscaleResolution.y != preUpscaleResolution.y * 2)
            {
                reason = $"non-exact-2x dimensions pre={preUpscaleResolution} post={postUpscaleResolution}";
                return false;
            }

            reason = null;
            return true;
        }

        private bool ValidateFormats(RenderGraph renderGraph, UpscalingIO io, out uint depthFormat)
        {
            depthFormat = _anfFormatD32Float;

            var colorDesc = renderGraph.GetTextureDesc(io.cameraColor);
            if (colorDesc.colorFormat != GraphicsFormat.B10G11R11_UFloatPack32)
            {
                ANFLogger.LogWarning($"Invalid input color format {colorDesc.colorFormat}. Expected B10G11R11_UFloat.");
                return false;
            }

            var depthDesc = renderGraph.GetTextureDesc(io.cameraDepth);
            if (!TryGetAnfDepthFormat(depthDesc.depthBufferBits, out depthFormat))
            {
                ANFLogger.LogWarning($"Invalid depth format. Expected 32-bit or 24-bit depth; got {depthDesc.depthBufferBits}. Falling back to bypass blit.");
                return false;
            }

            var mvDesc = renderGraph.GetTextureDesc(io.motionVectorColor);
            if (mvDesc.colorFormat != GraphicsFormat.R16G16_SFloat)
            {
                ANFLogger.LogWarning($"Invalid motion vector format {mvDesc.colorFormat}. Expected R16G16_SFloat.");
                return false;
            }

            // Log format validation success (throttled) so we can confirm formats are correct.
#if UNITY_EDITOR || DEBUG
            if (Time.frameCount % 60 == 0 && ANFLogger.IsEnabled(AnfLogLevel.Trace))
            {
                ANFLogger.LogTrace(
                    $"ValidateFormats OK: " +
                    $"color={colorDesc.colorFormat} " +
                    $"depth={depthDesc.depthBufferBits}(anfFmt={depthFormat}) " +
                    $"mv={mvDesc.colorFormat}");
            }
#endif

            return true;
        }

        private class PassData
        {
            public TextureHandle inputColor;
            public TextureHandle depth;
            public TextureHandle motionVectors;
            public TextureHandle outputColor;
            public uint sequenceId;
            public IntPtr renderEventFunc;
        }
    }

    internal sealed class ANFUpscalerNativeContextRef
    {
        internal IntPtr Handle;
    }

    internal sealed class ANFUpscalerContext : PluginUpscalerContext<ANFUpscalerNativeContextRef, ANFUpscalerOptions>, IUpscalerContext
    {
        private static long s_NextSequenceId;

        private bool _cleanupRequested;

        internal IntPtr NativeHandle => m_NativeContext?.Handle ?? IntPtr.Zero;
        internal bool IsNativeReadyForScheduling => !_cleanupRequested && NativeHandle != IntPtr.Zero;

        public ANFUpscalerContext(Vector2Int displayResolution)
            : base(displayResolution)
        {
            m_NativeContext = new ANFUpscalerNativeContextRef();
            ANFLogger.LogInfo($"Upscaler context: created for displayResolution={displayResolution}; native initialization deferred.");
        }

        internal static uint AllocateSequenceId()
        {
            // Native pending-dispatch lookup is currently keyed by sequence id only.
            // Allocate process-wide monotonic ids so multiple camera contexts cannot
            // overwrite each other's sequence 0/1/... entries before Phase A4 native
            // registry hardening.
            return unchecked((uint)System.Threading.Interlocked.Increment(ref s_NextSequenceId));
        }

        public new bool IsValidForOptions(UpscalerOptions options)
        {
            return (options == null || options is ANFUpscalerOptions) && !_cleanupRequested;
        }

        bool IUpscalerContext.IsValidForOptions(UpscalerOptions options)
        {
            return IsValidForOptions(options);
        }

        protected override bool ValidateOptions(ANFUpscalerOptions options)
        {
            return !_cleanupRequested;
        }

        internal bool EnsureNativeInitialized(string reason)
        {
            if (_cleanupRequested)
            {
                ANFLogger.LogWarning($"Upscaler context: cleanup was already requested; lazy init skipped for {reason}.");
                return false;
            }

            m_NativeContext ??= new ANFUpscalerNativeContextRef();

            if (m_NativeContext.Handle != IntPtr.Zero)
            {
                return ANFUpscalerLifecycle.EnsureRenderEventFunc(reason) != IntPtr.Zero;
            }

            ANFUpscalerLifecycle.AssertMainThread($"Context.EnsureNativeInitialized:{reason}");

            if (!ANFUpscalerLifecycle.BypassGraphicsApiCheckForTests && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Vulkan)
            {
                ANFLogger.LogWarning($"Upscaler context: Unsupported graphics API {SystemInfo.graphicsDeviceType}. ANF requires Vulkan. Lazy init skipped for {reason}.");
                return false;
            }

            ANFLogger.LogInfo($"Upscaler context: lazy native init attempt ({reason}).");
            int initResult = ANFUpscalerLifecycle.NativeApi.Initialise(out var handle);
            if (initResult != 0 || handle == IntPtr.Zero)
            {
                ANFLogger.LogWarning($"Upscaler context: AnfInitialise returned {initResult} (handle={handle}) during lazy init ({reason}).");
                return false;
            }

            m_NativeContext.Handle = handle;
            int enabledResult = ANFUpscalerLifecycle.NativeApi.SetEnabled(m_NativeContext.Handle, 1);
            if (enabledResult != 0)
            {
                ANFLogger.LogWarning($"Upscaler context: AnfSetEnabled returned {enabledResult} during lazy init ({reason}); deferring native destroy to DestroyNativeContext(CommandBuffer) cleanup callback path.");
                ShutdownNative(ANFUpscalerLifecycle.ShutdownReason.ContextCleanup, terminal: true, fromCleanupCallback: false, cmd: null, nativeContext: m_NativeContext);
                return false;
            }
            ANFLogger.LogInfo($"Upscaler context: lazy init succeeded (handle={m_NativeContext.Handle}).");

            return ANFUpscalerLifecycle.EnsureRenderEventFunc(reason) != IntPtr.Zero;
        }

        protected override void DestroyNativeContext(CommandBuffer cmd, ANFUpscalerNativeContextRef nativeContext)
        {
            ShutdownNative(ANFUpscalerLifecycle.ShutdownReason.ContextCleanup, terminal: true, fromCleanupCallback: true, cmd: cmd, nativeContext);
        }

        internal bool ShutdownNative(ANFUpscalerLifecycle.ShutdownReason reason, bool terminal, bool fromCleanupCallback = false)
        {
            return ShutdownNative(reason, terminal, fromCleanupCallback, null, m_NativeContext);
        }

        internal bool ShutdownNative(ANFUpscalerLifecycle.ShutdownReason reason, bool terminal, bool fromCleanupCallback, CommandBuffer cmd)
        {
            return ShutdownNative(reason, terminal, fromCleanupCallback, cmd, m_NativeContext);
        }

        private bool ShutdownNative(ANFUpscalerLifecycle.ShutdownReason reason, bool terminal, bool fromCleanupCallback, CommandBuffer cmd, ANFUpscalerNativeContextRef nativeContext)
        {
            ANFUpscalerLifecycle.AssertMainThread($"Context.ShutdownNative:{reason}");

            IntPtr liveHandle = nativeContext?.Handle ?? IntPtr.Zero;

            bool hadHandle = liveHandle != IntPtr.Zero;
            if (_cleanupRequested && !hadHandle)
            {
                return false;
            }

            if (!hadHandle)
            {
                if (terminal)
                {
                    _cleanupRequested = true;
                }
                ANFLogger.LogInfo($"Upscaler context: shutdown no-op reason={reason} terminal={terminal}; no handle present.");
                return false;
            }

            var handle = liveHandle;
            if (cmd == null)
            {
                string message = $"Upscaler context: Cleanup/Shutdown called without CommandBuffer; retaining native handle until DestroyNativeContext(CommandBuffer) can schedule cleanup callback handle={handle}.";
                if (ANFUpscalerLifecycle.ShouldDowngradeNullCommandBufferShutdown(reason))
                {
                    ANFLogger.LogInfo(message);
                }
                else
                {
                    ANFLogger.LogWarning(message);
                }

                if (terminal)
                {
                    _cleanupRequested = true;
                }

                return false;
            }

            IntPtr cleanupEventFunc = ANFUpscalerLifecycle.NativeApi.GetCleanupEventFunc();
            if (cleanupEventFunc == IntPtr.Zero)
            {
                ANFLogger.LogWarning($"Upscaler context: cleanup event function unavailable; retaining native handle until cleanup callback path is available handle={handle}.");
                if (terminal)
                {
                    _cleanupRequested = true;
                }

                return false;
            }

            var scheduler = ANFUpscalerLifecycle.CleanupEventSchedulerForTests;
            if (scheduler != null)
            {
                // DestroyNativeContext(CommandBuffer) contract: schedule one render-thread
                // cleanup event carrying the native context handle.
                scheduler(cmd, cleanupEventFunc, 0, handle);
            }
            else
            {
                // Keep runtime cleanup path identical to the test scheduler contract.
                cmd.IssuePluginEventAndData(cleanupEventFunc, 0, handle);
            }

            if (nativeContext != null)
            {
                nativeContext.Handle = IntPtr.Zero;
            }

            _cleanupRequested = terminal || _cleanupRequested;
            ANFLogger.LogInfo($"Upscaler context: native destroy requested reason={reason} terminal={terminal} cleanupCallback={fromCleanupCallback} handle={handle}.");
            return true;
        }
    }

#if UNITY_EDITOR
    [InitializeOnLoad]
#endif
    // [Preserve] keeps this class and its methods from being stripped by IL2CPP.
    // RegisterANFUpscaler is not referenced by any other type; it is discovered
    // solely via [RuntimeInitializeOnLoadMethod] and [InitializeOnLoad].  Without
    // [Preserve] and the link.xml entry, IL2CPP may remove it before the Unity
    // runtime can find it, causing ANFUpscaler to never be registered.
    [Preserve]
    static class RegisterANFUpscaler
    {
        // Editor: [InitializeOnLoad] triggers this constructor so the upscaler
        // appears in the Inspector dropdown without a Play-mode session.
        [Preserve]
        static RegisterANFUpscaler()
        {
            ANFLogger.LogInfo("Upscaler: RegisterANFUpscaler static ctor — registering ANFUpscaler.");
            ANFUpscalerLifecycle.EnsureSubscribed();
            UpscalerRegistry.Register<ANFUpscaler, ANFUpscalerOptions>(ANFUpscaler.UpscalerName);
        }

        // Runtime: SubsystemRegistration fires before the render pipeline is
        // constructed, ensuring ANFUpscaler is in UpscalerRegistry when URP
        // snapshots the registry during pipeline-asset load.
        [Preserve]
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void InitRuntime()
        {
            // Android startup marker — confirms Qualcomm.ANF.Runtime is active
            // independently of sample intent parsing (ci/test_android.py).
#if UNITY_ANDROID && !UNITY_EDITOR
            Debug.Log("[ANF] Runtime initialized (Qualcomm.ANF.Runtime, SubsystemRegistration).");
#endif
            ANFUpscalerLifecycle.SubsystemRegistrationReset();
            ANFLogger.LogInfo("Upscaler: RegisterANFUpscaler.InitRuntime (SubsystemRegistration) — registering ANFUpscaler.");
            UpscalerRegistry.Register<ANFUpscaler, ANFUpscalerOptions>(ANFUpscaler.UpscalerName);
            ANFUpscalerLifecycle.EnsureSubscribed();
        }
    }
}
