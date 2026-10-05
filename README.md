# Adreno Neural Fusion (ANF) for Unity

**Adreno Neural Fusion (ANF) Super Resolution** for Unity's Universal Render Pipeline (URP).

ANF integrates with Unity's Upscaler Framework (Unity 6.6+) and appears in the
**URP Asset -> Quality -> Upscaling Filter** dropdown as **ANF Upscaler**.

## Resources

| Resource | |
|---|---|
| [Adreno Neural Fusion Native SDK](https://github.com/SnapdragonGameStudios/adreno-neural-fusion) | Native Android Vulkan setup, rendering requirements, technique creation, dispatch, synchronization, validation, and release checks |
| [Snapdragon™ Profiler](https://www.qualcomm.com/developer/software/snapdragon-profiler) | Profile and analyze ANF-enabled applications on Snapdragon™ devices |
| [Snapdragon Game Toolkit](https://www.qualcomm.com/developer/snapdragon-game-toolkit) | Comprehensive resources for Snapdragon devices that use Android, Linux, and Windows.  |


## Requirements

| Requirement | |
|---|---|
| Unity | Unity 6000.6.2f1 or later |
| Render pipeline | Universal Render Pipeline 17.6.0 or later |
| Target platform | Android (Snapdragon 8 Elite Extreme Gen 6) |
| CPU architecture | ARM64 only |
| Scripting backend | IL2CPP |
| Graphics API | Vulkan only |
| Native plugins | `libanf_unity.so` and `libanf.so`, included under `Runtime/Plugins/Android/arm64-v8a/` |

The package includes the required Android native binaries. Consumers do not
need to build native code.

## Installation

### Package Manager

1. In Unity, open **Window -> Package Manager**.
2. Select **+ -> Add package from git URL**.
3. Enter:

   ```text
   git://github.com/SnapdragonGameStudios/com.qualcomm.snapdragon.adreno.neural.fusion.git
   ```

Git must be installed and available on your system for Unity to install a Git
package.

### `manifest.json`

Alternatively, add the following entry to your project's `Packages/manifest.json`:

```json
"com.qualcomm.snapdragon.adreno.neural.fusion": "git://github.com/SnapdragonGameStudios/com.qualcomm.snapdragon.adreno.neural.fusion.git"
```

For a package checked out locally, use Unity's **Add package from disk** option
and select its `package.json` file.

## Project Setup

### 1. Enable the Upscaler Framework

Add this scripting define symbol for the Android target through **Project
Settings -> Player -> Other Settings -> Scripting Define Symbols**:

```text
ENABLE_UPSCALER_FRAMEWORK
```

Without this define, the `Qualcomm.ANF.Runtime` assembly does not compile and
the upscaler does not appear in the URP Asset dropdown.

### 2. Configure Android Player Settings

Under **File -> Build Profiles -> Android -> Player Settings**, configure:

| Setting | Required value |
|---|---|
| Scripting Backend | IL2CPP |
| Target Architectures | ARM64 |
| Graphics APIs | Vulkan; remove other graphics APIs for the Android target |

### 3. Configure Every Active URP Asset

Projects can select different URP Assets for different quality levels. Repeat
these steps for each URP Asset that the Android build can use:

1. Select the URP Asset in the Project window.
2. Set **Render Scale** to `0.5`.
3. Under **Quality -> Upscaling Filter**, select **ANF Upscaler**.
4. Build and deploy to a supported Android device.

ANF requires an exact 2x relationship between its input and output resolution.
For example, a 1920x1080 display requires a 960x540 input. The output width and
height must be even.

## Runtime Requirements and Limitations

ANF processes a frame only when all of the following are true:

| Requirement | Constraint |
|---|---|
| Render scale | `0.5`, producing an exact 2x input-to-output resolution |
| Views | One active view; XR and multiview are unsupported |
| Dynamic resolution | Unsupported |
| Input color | `B10G11R11_UFloatPack32` |
| Depth | 24-bit or 32-bit depth |
| Motion vectors | `R16G16_SFloat` |
| Sharpening | Unsupported |
| Frame generation | Not included in this package |

The input color, depth, and motion-vector formats are supplied by URP. If a
custom renderer feature, render target, or pipeline setting changes them to an
unsupported format, ANF bypasses that frame.

On an unsupported graphics API, unsupported runtime path, incompatible format,
or native initialization failure, ANF logs a warning and bypasses its upscale
pass. Check the Unity Console or device log before reporting an issue.

The package is intended for Android ARM64 Vulkan builds. Do not enable ANF for
other build targets or Android CPU architectures.

## Verification and Troubleshooting

| Symptom | Check |
|---|---|
| **ANF Upscaler** is absent from the URP Asset dropdown | Confirm the package is installed, URP 17.6.0 or later is resolved, and `ENABLE_UPSCALER_FRAMEWORK` is set for Android. |
| ANF logs an unsupported graphics API warning | Configure Android to use Vulkan only. |
| ANF logs an unsupported dispatch-path warning | Use one view, disable XR and dynamic resolution, and ensure the final output dimensions are even with a 2x input/output relationship. |
| ANF logs an invalid format warning | Check custom render targets and renderer features. ANF requires the formats listed above. |
| ANF appears configured but output is bypassed | Confirm the build is Android ARM64, IL2CPP, Vulkan, and running on supported hardware. |

Managed messages use the `[ANF/Info]`, `[ANF/Debug]`, and `[ANF/Trace]` prefixes.
To increase managed and native logging verbosity during diagnosis:

```csharp
using Qualcomm.ANF.Runtime;

ANFLogger.Level = AnfLogLevel.Debug;
```

On a connected Android device, inspect ANF-related output with:

```text
adb logcat -s Unity:* "[ANF-Native]:*"
```

Include the Unity version, URP version, device model, SoC, Android version,
graphics API, and relevant Unity Console or logcat output when filing a bug.

## Support and Reporting Issues

Use the repository's [issue tracker](https://github.com/SnapdragonGameStudios/com.qualcomm.snapdragon.adreno.neural.fusion/issues)
for bug reports and feature requests. See the [changelog](CHANGELOG.md) for
release history.

## License and Redistribution

The precompiled ANF SDK binary at
`Runtime/Plugins/Android/arm64-v8a/libanf.so` is distributed under the
[QTI No-Login Binary License](LICENSE.md).

The C# plugin source under `Runtime/` and the precompiled Unity plugin binary
at `Runtime/Plugins/Android/arm64-v8a/libanf_unity.so` are available under the
[BSD 3-Clause License](LICENSE-BSD-3-Clause.md).
