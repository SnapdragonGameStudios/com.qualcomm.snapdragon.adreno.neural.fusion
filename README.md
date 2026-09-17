# ANF Upscaler — `com.qualcomm.snapdragon.adreno.neural.fusion`

**Adreno Neural Fusion (ANF) Super Resolution** for Unity's Universal Render Pipeline.

Integrates with Unity's Upscaler Framework (Unity 6.6+) and appears in the
**URP Asset → Upscaling Filter** dropdown as **"ANF Upscaler"**.

---

## Requirements

| Requirement | Value |
|---|---|
| Unity | Unity 6000.6.0b6 or later  |
| Target platform | Android (Hawi SoC and newer) |
| Scripting backend | IL2CPP |
| Graphics API | Vulkan |
| Native plugins | `libanf_unity.so` and `libanf.so` in `Runtime/Plugins/Android/arm64-v8a/` |

---

## Setup

### 1. Enable the scripting define symbol

Add the following scripting define symbol to your Unity project via
**Project Settings → Player → Other Settings → Scripting Define Symbols**:

```text
ENABLE_UPSCALER_FRAMEWORK
```

Without this define the `Qualcomm.ANF.Runtime` assembly will not compile and
the upscaler will not appear in the URP Asset dropdown.

### 2. Install the package

Add the following entry to `Packages/manifest.json`:

```json
"com.qualcomm.snapdragon.adreno.neural.fusion": "git://github.com/SnapdragonGameStudios/com.qualcomm.snapdragon.adreno.neural.fusion.git"
```

This can also be done through the Package Manager window in the Unity Editor.

The package ships with prebuilt native binaries under `Runtime/Plugins/` —
no separate native build step is required to use the package.

### 3. Configure URP

1. Open your **URP Asset** in the Inspector.
2. Set **Render Scale** to `0.5` for the ANF path. The runtime validates exact 2× input/output pairs (for example 960×540 → 1920×1080 or 1280×720 → 2560×1440);
3. Under **Quality → Upscaling Filter**, select **ANF Upscaler**.
4. Build and deploy to a supported Android device.


## Limitations and Runtime Behaviour

- Android Vulkan on supported Qualcomm SoCs is the primary target.
- ANF Super Resolution currently requires render scale `0.5` / positive exact-2x input-output behavior.
- XR rendering is not supported.
- Frame Generation is not in scope.
- Windows on Snapdragon is not supported for this package release.
- In Unity 6.6, `ANFUpscaler.RecordRenderGraph` creates the SDK output texture and assigns it directly to `io.cameraColor`. URP's normal final presentation path then presents that camera color. There is no secondary presentation feature, secondary camera, or handoff render texture in the current design.
- When ANF is unavailable or unsupported, the package should report unavailable/fallback state and keep rendering through a supported path.

## License
The precompiled SDK binary at `Runtime/Plugins/Android/arm64-v8a/libanf.so` is distributed under the [QTI No-Login Binary License](LICENSE.md).

The C# plugin source code in `Runtime/` and precompiled plugin binary at `Runtime/Plugins/Android/arm64-v8a/libanf_unity.so` are available under the [BSD 3-Clause License](LICENSE-BSD-3-Clause.md).

