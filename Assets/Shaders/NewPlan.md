# Unity 6000.3.10f1 Setup Quick-Reference

Supplementary guide for [whimsical-frolicking-adleman.md](whimsical-frolicking-adleman.md) — maps every setting to its exact Unity Editor location.

---

## a. Pipeline & Renderer Assets

| What | Where |
|---|---|
| URP pipeline asset per quality tier | `Edit > Project Settings > Quality` → select tier → **Render Pipeline Asset** field |
| PC pipeline asset | `Assets/Settings/PC_RPAsset.asset` → Inspector |
| Mobile pipeline asset | `Assets/Settings/Mobile_RPAsset.asset` → Inspector |
| Renderer settings (rendering path, depth/opaque texture) | Select `Assets/Settings/PC_Renderer.asset` or `Mobile_Renderer.asset` → Inspector |
| SSAO (PC only) | Select `PC_Renderer.asset` → Inspector → **Renderer Features** list → `ScreenSpaceAmbientOcclusion` |
| URP Global Settings | `Edit > Project Settings > Graphics` → **URP** tab — or select `Assets/Settings/UniversalRenderPipelineGlobalSettings.asset` |
| Default Render Pipeline | `Edit > Project Settings > Graphics` → **Default Render Pipeline** field |
| Shader stripping | `Edit > Project Settings > Graphics` → **URP** tab → **Shader Stripping** section |

### Current Config Snapshot

- **PC**: Full resolution, 4-cascade 2048 shadows, SSAO on, HDR on, depth + opaque textures on, SRP Batcher on
- **Mobile**: 0.8× render scale (FSR upscale), 1-cascade 1024 shadows, no SSAO, no additional light shadows, depth/opaque textures off

---

## b. Volume Profiles & Post-Processing

| What | Where |
|---|---|
| Per-scene profiles | `Assets/Settings/3D_Chat.asset`, `3D_Journal.asset`, `3D_LoginArea.asset`, `3D_StartArea.asset` |
| Default volume profile | `Assets/Settings/DefaultVolumeProfile.asset` (referenced by URP Global Settings) |
| Sample scene profile | `Assets/Settings/SampleSceneProfile.asset` |
| Edit overrides | Select profile `.asset` → Inspector → **Add Override** button |
| Edit via scene | Hierarchy → select Volume GameObject → Inspector → **Volume** component → profile field |
| **Fix Motion Blur** | Select `SampleSceneProfile.asset` → uncheck Motion Blur override or set intensity to 0 |

### VR Safety Reminders

- Motion Blur: **OFF** (nausea)
- Depth of Field: **OFF** (contradicts VR accommodation)
- Vignette: **max 0.1**
- Tonemapping: **Neutral** (not ACES — ACES adds contrast, counterproductive for soft pastels)

---

## c. Lighting (Per-Scene)

| What | Where |
|---|---|
| Lighting window | `Window > Rendering > Lighting` |
| Skybox material | Lighting window → **Environment** tab → **Skybox Material** |
| Ambient light source/color | Same tab → **Environment Lighting** section |
| Baked GI | Lighting window → **Lightmapping Settings** → enable **Baked Global Illumination** |
| Realtime GI | Same section → toggle **Realtime Global Illumination** |
| Light Probes | Lighting window → **Light Probe Groups** section |
| Fog | Volume Profile → **Add Override** → `Fog` (URP uses volume-based fog in Unity 6, **not** legacy fog in the Lighting window) |

---

## d. Shader Workflow

| What | Where |
|---|---|
| Custom shaders | `Assets/Shaders/` — `.shader` and `.hlsl` files |
| Custom Editor GUIs | `Assets/Shaders/Editor/` — `MiSideShaderGUI.cs`, `MiSideCharacterShaderGUI.cs`, etc. |
| Assign shader to material | Select material → Inspector → **Shader** dropdown → `MiSide/Environment`, `MiSide/Character`, etc. |
| SRP Batcher check | `Window > Analysis > Frame Debugger` → start capture → **SRP Batch** column |
| Shader variants / compile | Select `.shader` → Inspector → **Compile and show code** / **Show** button |
| Preloaded shaders | `Edit > Project Settings > Graphics` → **Shader Loading** section |

---

## e. Quality Settings

| What | Where |
|---|---|
| Quality tiers | `Edit > Project Settings > Quality` — two presets: **Mobile** and **PC** |
| Per-tier pipeline link | Each tier → **Render Pipeline Asset** field → `Mobile_RPAsset` / `PC_RPAsset` |
| Shadow distance | Same panel → **Shadows** section (currently 40m both tiers) |
| Skin weights | Same panel (Mobile: 2 bones, PC: 4 bones) |
| LOD bias | Same panel (Mobile: 1.0×, PC: 2.0×) |

---

## f. VR / XR Settings

| What | Where |
|---|---|
| XR Plug-in Management | `Edit > Project Settings > XR Plug-in Management` |
| Single Pass Instanced | Same panel → select platform tab → provider settings → **Rendering Mode** |
| Frame Debugger (stereo) | `Window > Analysis > Frame Debugger` |

---

## g. Editor Tools (MiSide)

| Tool | Script | Menu Path |
|---|---|---|
| Material Converter | `Assets/Shaders/Editor/MiSideMaterialConverter.cs` | `Tools > MiSide` (check script for exact entry) |
| Character Tuner | `Assets/Shaders/Editor/MiSideCharacterTuner.cs` | `Tools > MiSide` |
| Character Shader GUI | `Assets/Shaders/Editor/MiSideCharacterShaderGUI.cs` | Auto-applied via `CustomEditor` in shader |
| Environment Shader GUI | `Assets/Shaders/Editor/MiSideShaderGUI.cs` | Auto-applied via `CustomEditor` in shader |
| Beach Material Consolidator | `Assets/Shaders/Editor/BeachMaterialConsolidator.cs` | `Tools > MiSide` |
