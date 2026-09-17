//=============================================================================
//
// Copyright (c) Qualcomm Technologies, Inc. and/or its subsidiaries. 
// SPDX-License-Identifier: BSD-3-Clause-Clear
//
//=============================================================================


// ============================================================================
// ANF Package Managed Logging Abstraction
// ============================================================================
//
// PURPOSE
// -------
// This file defines the package-local C# logging facade for the ANF Unity
// package.  All managed log output must route through ANFLogger rather than
// calling Debug.Log / Debug.LogWarning / Debug.LogError directly, so that:
//   1. Log verbosity is controlled by a single shared level setting.
//   2. The managed level is kept in sync with the native (C++) log level.
//   3. Debug and Trace output is stripped from non-debug builds.
//
// LEVEL MAPPING
// -------------
// The managed AnfLogLevel enum maps to the native anf::LogLevel enum
// (defined in native/include/AnfLog.h) as follows:
//
//   Managed AnfLogLevel  │ Native anf::LogLevel │ Native int │ Unity LogType
//   ─────────────────────────────────────────────────────────────────────────
//   Off                   │ (no native equivalent)│ (clamped)  │ (suppressed)
//   Error                 │ LogLevel::Error        │ 4          │ LogType.Error
//   Warning               │ LogLevel::Warn         │ 3          │ LogType.Warning
//   Info                  │ LogLevel::Info         │ 2          │ LogType.Log  [ANF/Info]
//   Debug                 │ LogLevel::Debug        │ 1          │ LogType.Log  [ANF/Debug]
//   Trace                 │ LogLevel::Verbose      │ 0          │ LogType.Log  [ANF/Trace]
//
// Because Unity's ILogger / Debug.unityLogger uses only three LogType values
// (Error, Warning, Log), Info / Debug / Trace are all emitted as LogType.Log.
// To distinguish them in the Unity Console and in logcat, each level is
// prefixed with a tag:
//   [ANF/Info]  — informational messages
//   [ANF/Debug] — developer debug messages (debug builds only)
//   [ANF/Trace] — fine-grained trace messages (debug builds only)
//
// NATIVE LOG ROUTING
// ------------------
// The native plugin (anf_unity) emits its own log lines via two paths,
// selected based on whether a Unity log callback is registered:
//
//   1. Unity callback (primary): AnfSetUnityLogCallback registers a C# delegate
//      that the native side calls for each log line that passes the native verbosity
//      filter.  The callback receives (int level, string message) and routes the
//      message to Debug.Log / Debug.LogWarning / Debug.LogError directly (not
//      through ANFLogger, to avoid double-filtering).  When the callback is set,
//      Android logcat emission is skipped entirely.
//      The callback is installed and owned by AnfSampleController via its
//      _EmitStartupDiagnostics() [RuntimeInitializeOnLoadMethod].
//
//   2. Android logcat fallback: __android_log_print with tag "[ANF-Native]".
//      Used only when no Unity callback is registered — for example, before
//      AnfSampleController._EmitStartupDiagnostics() runs, or in non-sample
//      builds that do not install a callback.  These lines are filterable with:
//        adb logcat -s "[ANF-Native]"
//
// INITIALIZATION AND LEVEL CHANGE FLOW
// -------------------------------------
// When the managed log level is set (via ANFLogger.Level = ...), the
// following sequence occurs:
//
//   1. ANFLogger stores the new AnfLogLevel value.
//   2. ANFLogger calls AnfLogLevelMapper.ToNativeLevel(level) to obtain
//      the corresponding native integer (0–4, or clamped for Off).
//   3. ANFLogger calls AnfLogLevelMapper.ApplyNativeLevel(nativeLevel),
//      which invokes AnfSetLogVerbosity(nativeLevel) via P/Invoke.
//      This updates the native verbosity threshold atomically (thread-safe
//      per the native implementation in AnfLog.cpp).
//   4. The native side immediately begins filtering log messages at the new
//      threshold.  Messages below the threshold are discarded in the native
//      EmitLogLine function before the Unity callback is invoked.

//
// DEVELOPMENT-ONLY APIS
// ---------------------
// ANFLogger.Debug(...) and ANFLogger.Trace(...) are guarded by:
//   #if UNITY_EDITOR || DEBUG
// This means they are compiled out entirely in release (non-development)
// player builds.  In debug builds and in the Editor they are compiled
// in but still subject to the runtime IsEnabled(AnfLogLevel.Debug/Trace)
// check, so they produce no output unless the level is set low enough.
//
// Call sites that construct expensive log strings for Debug/Trace should
// also guard the string construction:
//   #if UNITY_EDITOR || DEBUG
//   if (ANFLogger.IsEnabled(AnfLogLevel.Debug))
//       ANFLogger.Debug($"expensive: {ComputeExpensiveString()}");
//   #endif

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Scripting;

// Allow the Editor test assembly to access internal types (e.g. AnfNativeLogBridge)
// for reflection-based signature verification tests.
[assembly: InternalsVisibleTo("Qualcomm.ANF.Editor.Tests")]

namespace Qualcomm.ANF.Runtime
{
    // =========================================================================
    // AnfLogLevel — managed log-level enum
    // =========================================================================

    /// <summary>
    /// Managed log-level enum for the ANF package.
    ///
    /// <para>
    /// Integer values are chosen to match the native <c>anf::LogLevel</c> enum
    /// (see <c>native/include/AnfLog.h</c>) for the levels that have a native
    /// counterpart.  <see cref="Off"/> has no native equivalent; when the managed
    /// level is <see cref="Off"/>, the native level is clamped to
    /// <see cref="Error"/> (4) so that native error messages are still visible.
    /// </para>
    ///
    /// <para>
    /// Ordering: lower integer = more verbose.  A message at level L is emitted
    /// when <c>L &gt;= currentLevel</c>.
    /// </para>
    /// </summary>
    public enum AnfLogLevel
    {
        /// <summary>Fine-grained trace output (most verbose). Development builds only.</summary>
        Trace = 0,

        /// <summary>Developer debug information. Development builds only.</summary>
        Debug = 1,

        /// <summary>General informational messages.</summary>
        Info = 2,

        /// <summary>Recoverable warnings.</summary>
        Warning = 3,

        /// <summary>Non-recoverable errors (least verbose).</summary>
        Error = 4,

        /// <summary>
        /// Suppress all managed log output.  The native level is clamped to
        /// <see cref="Error"/> so native error messages remain visible.
        /// </summary>
        Off = 5,
    }

    // =========================================================================
    // AnfLogLevelMapper — centralized managed↔native level mapping
    // =========================================================================

    /// <summary>
    /// Centralized helper that maps <see cref="AnfLogLevel"/> to the native
    /// <c>anf::LogLevel</c> integer and applies the level to the native plugin.
    ///
    /// <para>
    /// This is the single point of truth for the managed-to-native level
    /// mapping.  All code that needs to synchronize the managed and native
    /// log levels must go through this class.
    /// </para>
    ///
    /// <para>
    /// Native level integers (must match <c>anf::LogLevel</c> in
    /// <c>native/include/AnfLog.h</c>):
    /// <list type="bullet">
    ///   <item>0 = Verbose (most verbose)</item>
    ///   <item>1 = Debug</item>
    ///   <item>2 = Info (native default)</item>
    ///   <item>3 = Warn</item>
    ///   <item>4 = Error (least verbose)</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class AnfLogLevelMapper
    {
        // Native level integer constants — must match anf::LogLevel in AnfLog.h.
        // Public so that test assemblies can reference them in [TestCase] attributes.
        public const int NativeLevelVerbose = 0;
        public const int NativeLevelDebug = 1;
        public const int NativeLevelInfo = 2;
        public const int NativeLevelWarn = 3;
        public const int NativeLevelError = 4;

        /// <summary>
        /// Maps a managed <see cref="AnfLogLevel"/> to the corresponding native
        /// <c>anf::LogLevel</c> integer.
        ///
        /// <para>
        /// <see cref="AnfLogLevel.Off"/> has no native equivalent; it maps to
        /// <see cref="NativeLevelError"/> (4) so that native error messages are
        /// still visible even when managed output is fully suppressed.
        /// </para>
        /// </summary>
        /// <param name="level">The managed log level to convert.</param>
        /// <returns>
        ///   The native integer in the range [0, 4] that corresponds to
        ///   <paramref name="level"/>.
        /// </returns>
        public static int ToNativeLevel(AnfLogLevel level)
        {
            switch (level)
            {
                case AnfLogLevel.Trace: return NativeLevelVerbose;
                case AnfLogLevel.Debug: return NativeLevelDebug;
                case AnfLogLevel.Info: return NativeLevelInfo;
                case AnfLogLevel.Warning: return NativeLevelWarn;
                case AnfLogLevel.Error: return NativeLevelError;
                case AnfLogLevel.Off: return NativeLevelError; // clamp: keep native errors visible
                default: return NativeLevelInfo; // safe fallback
            }
        }

        /// <summary>
        /// Maps a native <c>anf::LogLevel</c> integer to the closest managed
        /// <see cref="AnfLogLevel"/>.  Used when the native level is set
        /// externally (e.g. via Android intent) and the managed level needs to
        /// be synchronized.
        /// </summary>
        /// <param name="nativeLevel">
        ///   The native integer level (0–4).  Out-of-range values are clamped.
        /// </param>
        /// <returns>The closest managed <see cref="AnfLogLevel"/>.</returns>
        public static AnfLogLevel FromNativeLevel(int nativeLevel)
        {
            switch (nativeLevel)
            {
                case NativeLevelVerbose: return AnfLogLevel.Trace;
                case NativeLevelDebug: return AnfLogLevel.Debug;
                case NativeLevelInfo: return AnfLogLevel.Info;
                case NativeLevelWarn: return AnfLogLevel.Warning;
                case NativeLevelError: return AnfLogLevel.Error;
                default:
                    // Clamp: values below 0 → Trace; values above 4 → Off.
                    return nativeLevel < NativeLevelVerbose ? AnfLogLevel.Trace : AnfLogLevel.Off;
            }
        }

        /// <summary>
        /// Applies a native log-level integer to the native plugin by calling
        /// <c>AnfSetLogVerbosity</c> via P/Invoke.  Swallows
        /// <see cref="System.DllNotFoundException"/> and
        /// <see cref="System.EntryPointNotFoundException"/> so that the managed
        /// logger remains functional even when the native library is absent
        /// (e.g. in headless Editor tests).
        /// </summary>
        /// <param name="nativeLevel">
        ///   The native integer level to forward (0–4).
        /// </param>
        public static void ApplyNativeLevel(int nativeLevel)
        {
            try
            {
                AnfNativeLogBridge.SetLogVerbosity(nativeLevel);
            }
            catch (System.DllNotFoundException)
            {
                // Native library absent — managed logging still works.
            }
            catch (System.EntryPointNotFoundException)
            {
                // Entry point missing in native library — managed logging still works.
            }
        }
    }

    // =========================================================================
    // AnfNativeLogBridge — P/Invoke declarations for native log control
    // =========================================================================

    /// <summary>
    /// Low-level P/Invoke bridge for native log-level control and Unity log
    /// callback registration.
    ///
    /// <para>
    /// This class is intentionally kept separate from <see cref="ANFLogger"/>
    /// so that the P/Invoke declarations are isolated and can be tested or
    /// replaced independently.
    /// </para>
    ///
    /// <para>
    /// The native functions are exported by <c>anf_unity</c>
    /// (<c>anf_unity.dll</c> on Windows, <c>libanf_unity.so</c> on Android).
    /// </para>
    ///
    /// <para>
    /// <b>Callback ownership:</b> The Unity log callback is installed and owned
    /// by <c>AnfSampleController._EmitStartupDiagnostics()</c> via
    /// <c>[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]</c>.  This class
    /// exposes the P/Invoke declaration; it does not install the callback itself.
    /// </para>
    /// </summary>
    internal static class AnfNativeLogBridge
    {
        private const string _libraryName = "anf_unity";

        /// <summary>
        /// Sets the native logger verbosity threshold.
        /// Maps to <c>anf::SetLogVerbosity(static_cast&lt;anf::LogLevel&gt;(level))</c>.
        /// Valid values: 0=Verbose, 1=Debug, 2=Info (default), 3=Warn, 4=Error.
        /// Out-of-range values are clamped by the native implementation.
        /// Thread-safe per <c>AnfLog.cpp</c> (uses <c>std::atomic&lt;int&gt;</c>).
        /// </summary>
        [DllImport(_libraryName, EntryPoint = "AnfSetLogVerbosity")]
        internal static extern void SetLogVerbosity(int level);

        /// <summary>
        /// Delegate type for the native Unity log callback.
        /// The native side calls this for each log line that passes the verbosity
        /// filter.  <paramref name="level"/> is the native integer level (0–4);
        /// <paramref name="message"/> is the formatted log line.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void NativeLogCallback(int level, string message);

        /// <summary>
        /// Registers a callback that the native plugin calls for each log line
        /// that passes the native verbosity filter.  Pass <c>null</c> to
        /// unregister.
        ///
        /// <para>
        /// When a non-null callback is registered, the native side routes all
        /// log messages through it and skips Android logcat emission entirely.
        /// When no callback is registered, the native side falls back to
        /// Android logcat (<c>__android_log_print</c> with tag
        /// <c>[ANF-Native]</c>) or stderr on non-Android platforms.
        /// </para>
        ///
        /// <para>
        /// The callback is invoked from the native render thread; implementations
        /// must be thread-safe.  The registered delegate must be kept alive (e.g.
        /// as a static field) to prevent the GC from collecting it while the
        /// native side holds the function pointer.
        /// </para>
        ///
        /// <para>
        /// <b>Ownership:</b> The callback is installed by
        /// <c>AnfSampleController._EmitStartupDiagnostics()</c>.  Do not
        /// install a second callback from other code paths — doing so would
        /// replace the existing one and may cause log messages to be lost.
        /// </para>
        /// </summary>
        [DllImport(_libraryName, EntryPoint = "AnfSetUnityLogCallback")]
        internal static extern void SetUnityLogCallback(NativeLogCallback callback);
    }

    // =========================================================================
    // ANFLogger — package-local logging facade
    // =========================================================================

    /// <summary>
    /// Package-local logging facade for the ANF Unity package.
    ///
    /// <para>
    /// All managed log output in the ANF package must route through this class
    /// rather than calling <c>Debug.Log</c> / <c>Debug.LogWarning</c> /
    /// <c>Debug.LogError</c> directly.  This ensures that:
    /// <list type="bullet">
    ///   <item>Log verbosity is controlled by a single <see cref="Level"/> setting.</item>
    ///   <item>The managed level is kept in sync with the native log level via
    ///     <see cref="AnfLogLevelMapper"/>.</item>
    ///   <item><see cref="Debug"/> and <see cref="Trace"/> output is stripped from
    ///     non-debug builds at compile time.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Default level:</b> <see cref="AnfLogLevel.Info"/>, matching the native
    /// default (<c>anf::LogLevel::Info</c>).  Verbose and Debug messages are
    /// suppressed by default.
    /// </para>
    ///
    /// <para>
    /// <b>Unity ILogger sink:</b> The logger uses <c>Debug.unityLogger</c> as its
    /// backend sink.  A custom <see cref="ILogger"/> sink can be injected via
    /// <see cref="Sink"/> for testing or custom routing.  The sink must not be
    /// <c>null</c>; setting it to <c>null</c> restores the default
    /// <c>Debug.unityLogger</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Info / Debug / Trace prefixes:</b> Because Unity's <c>LogType</c> cannot
    /// distinguish Info, Debug, and Trace (all map to <c>LogType.Log</c>), each
    /// level is prefixed with a tag:
    /// <list type="bullet">
    ///   <item><c>[ANF/Info]</c> — informational messages</item>
    ///   <item><c>[ANF/Debug]</c> — developer debug messages</item>
    ///   <item><c>[ANF/Trace]</c> — fine-grained trace messages</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Development-only APIs:</b> <see cref="LogDebug"/> and <see cref="LogTrace"/>
    /// are compiled out in non-debug player builds via
    /// <c>#if UNITY_EDITOR || DEBUG</c>.  In debug builds and
    /// the Editor they are still subject to the runtime <see cref="IsEnabled"/>
    /// check.
    /// </para>
    /// </summary>
    [Preserve]
    public static class ANFLogger
    {
        // ── Level prefixes ────────────────────────────────────────────────────
        // Unity LogType.Log cannot distinguish Info / Debug / Trace, so we
        // prefix each level's messages to make them filterable in the Console
        // and in logcat.
        // Public so that test assemblies can assert on prefix values.
        public const string PrefixInfo = "[ANF/Info] ";
        public const string PrefixDebug = "[ANF/Debug] ";
        public const string PrefixTrace = "[ANF/Trace] ";

        // ── State ─────────────────────────────────────────────────────────────
        private static AnfLogLevel _level = AnfLogLevel.Info;
        private static ILogger _sink;

        // ── Sink ──────────────────────────────────────────────────────────────

        /// <summary>
        /// The <see cref="ILogger"/> sink used to emit log messages.
        /// Defaults to <c>Debug.unityLogger</c>.  Setting this to <c>null</c>
        /// restores the default sink.
        ///
        /// <para>
        /// Inject a custom sink in tests to capture log output without writing
        /// to the Unity Console.
        /// </para>
        /// </summary>
        public static ILogger Sink
        {
            get => _sink ?? Debug.unityLogger;
            set => _sink = value;
        }

        // ── Level ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Gets or sets the current managed log level.
        ///
        /// <para>
        /// Setting this property also synchronizes the native log level via
        /// <see cref="AnfLogLevelMapper.ApplyNativeLevel"/> so that managed and
        /// native verbosity thresholds remain consistent.
        /// </para>
        ///
        /// <para>
        /// Default: <see cref="AnfLogLevel.Info"/>, matching the native default.
        /// </para>
        /// </summary>
        public static AnfLogLevel Level
        {
            get => _level;
            set
            {
                _level = value;
                int nativeLevel = AnfLogLevelMapper.ToNativeLevel(value);
                AnfLogLevelMapper.ApplyNativeLevel(nativeLevel);
            }
        }

        // ── IsEnabled ─────────────────────────────────────────────────────────

        /// <summary>
        /// Returns <c>true</c> when messages at <paramref name="level"/> would be
        /// emitted given the current <see cref="Level"/> setting.
        ///
        /// <para>
        /// Use this to guard expensive string construction at Debug/Trace call
        /// sites:
        /// <code>
        /// #if UNITY_EDITOR || DEBUG
        /// if (ANFLogger.IsEnabled(AnfLogLevel.Debug))
        ///     ANFLogger.LogDebug($"expensive: {ComputeExpensiveString()}");
        /// #endif
        /// </code>
        /// </para>
        /// </summary>
        /// <param name="level">The level to test.</param>
        /// <returns>
        ///   <c>true</c> when <paramref name="level"/> is at or above the current
        ///   <see cref="Level"/> and <see cref="Level"/> is not
        ///   <see cref="AnfLogLevel.Off"/>.
        /// </returns>
        public static bool IsEnabled(AnfLogLevel level)
        {
            if (_level == AnfLogLevel.Off)
            {
                return false;
            }
            return (int)level >= (int)_level;
        }

        // ── Error ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Emits an error-level message via the configured <see cref="Sink"/>.
        /// Always compiled in (no development-build guard).
        /// </summary>
        /// <param name="message">The message to emit.</param>
        public static void LogError(string message)
        {
            if (!IsEnabled(AnfLogLevel.Error))
            {
                return;
            }
            Sink.LogError(tag: null, message: message);
        }

        // ── Warning ───────────────────────────────────────────────────────────

        /// <summary>
        /// Emits a warning-level message via the configured <see cref="Sink"/>.
        /// Always compiled in (no development-build guard).
        /// </summary>
        /// <param name="message">The message to emit.</param>
        public static void LogWarning(string message)
        {
            if (!IsEnabled(AnfLogLevel.Warning))
            {
                return;
            }
            Sink.LogWarning(tag: null, message: message);
        }

        // ── Info ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Emits an info-level message via the configured <see cref="Sink"/>.
        /// Always compiled in (no development-build guard).
        /// The message is prefixed with <c>[ANF/Info]</c> to distinguish it
        /// from Debug and Trace messages in the Unity Console.
        /// </summary>
        /// <param name="message">The message to emit.</param>
        public static void LogInfo(string message)
        {
            if (!IsEnabled(AnfLogLevel.Info))
            {
                return;
            }
            Sink.Log(PrefixInfo + message);
        }

        // ── Debug (debug builds only) ───────────────────────────────────

        /// <summary>
        /// Emits a debug-level message via the configured <see cref="Sink"/>.
        ///
        /// <para>
        /// <b>Development builds only:</b> this method is compiled out in
        /// non-debug player builds via
        /// <c>#if UNITY_EDITOR || DEBUG</c>.  In debug builds
        /// and the Editor it is still subject to the runtime
        /// <see cref="IsEnabled"/> check.
        /// </para>
        ///
        /// <para>
        /// The message is prefixed with <c>[ANF/Debug]</c>.
        /// </para>
        /// </summary>
        /// <param name="message">The message to emit.</param>
        [System.Diagnostics.Conditional("DEBUG")]
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        public static void LogDebug(string message)
        {
            if (!IsEnabled(AnfLogLevel.Debug))
            {
                return;
            }
            Sink.Log(PrefixDebug + message);
        }

        // ── Trace (debug builds only) ───────────────────────────────────

        /// <summary>
        /// Emits a trace-level message via the configured <see cref="Sink"/>.
        ///
        /// <para>
        /// <b>Development builds only:</b> this method is compiled out in
        /// non-debug player builds via
        /// <c>#if UNITY_EDITOR || DEBUG</c>.  In debug builds
        /// and the Editor it is still subject to the runtime
        /// <see cref="IsEnabled"/> check.
        /// </para>
        ///
        /// <para>
        /// The message is prefixed with <c>[ANF/Trace]</c>.
        /// </para>
        /// </summary>
        /// <param name="message">The message to emit.</param>
        [System.Diagnostics.Conditional("DEBUG")]
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        public static void LogTrace(string message)
        {
            if (!IsEnabled(AnfLogLevel.Trace))
            {
                return;
            }
            Sink.Log(PrefixTrace + message);
        }
    }
}
