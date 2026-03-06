# Water Ground + Interactive Ripples — Implementation Guide

## Overview

Three new files turn the **Ground** object in `3D_LoginArea` into a stylised sunset-water surface with interactive ripples that appear as the player walks.

| File | Purpose |
|------|---------|
| `Assets/Shaders/MiSide_WaterGround.shader` | URP HLSL water shader — animated waves, Fresnel reflections, procedural surface detail, and support for up to 10 interactive ripples. |
| `Assets/Scripts/WaterRippleManager.cs` | Component that lives on the **Ground** object. Manages a ring-buffer of ripple data and pushes it to the shader every frame. |
| `Assets/Scripts/PlayerFootRippleSpawner.cs` | Component that lives on the **XR Origin**. Detects horizontal movement and spawns ripples at the player's feet. |

---

## How It Works

### Shader — `MiSide/WaterGround`

1. **Vertex stage** — Three layered sine waves gently displace Y to create calm ocean-like motion.  
2. **Fragment stage**
   - **Wave normal** is computed analytically (no extra texture samples).
   - **Ripple normal** — a loop over up to 10 active ripples computes expanding ring waves and their analytical gradient for per-pixel normal perturbation.
   - **Surface detail** — three more sine layers add fine-scale normal variation (caustic-like shimmer).
   - These three normals are blended together.
   - **Fresnel** controls how much of the sky/horizon colour is mixed into the base water colour — giving the calm, mirror-like sunset look from the reference image.
   - **Sun specular** adds a bright highlight from the main directional light.
   - **Ripple glow** brightens fragment colour at ripple peaks so the rings catch light.

### Scripts

`WaterRippleManager` keeps a `Vector4[10]` ring-buffer:

| Component | Meaning |
|-----------|---------|
| `.x` `.y` | World-space X and Z of the ripple centre |
| `.z` | `Time.time` when the ripple was spawned |
| `.w` | Strength (0 = inactive, 1 = full) |

Each frame it expires old entries and calls `Material.SetVectorArray` to upload the array to the GPU.

`PlayerFootRippleSpawner` measures horizontal distance travelled. Once the player has moved `stepDistance` metres (default 0.5 m), it raycasts downward to find the water surface and tells the manager to spawn a ripple.

---

## Setup Steps (Unity Editor)

### 1. Create the Water Material

1. **Project window → right-click → Create → Material**.
2. Name it `MAT_WaterGround`.
3. In the Inspector, change the shader dropdown to **MiSide → WaterGround**.
4. Adjust colours to taste (see *Recommended Settings* below).

### 2. Apply Material to the Ground

1. Open **`Assets/Scenes/3D_LoginArea`**.
2. Select the **Ground** GameObject in the Hierarchy.
3. In the Inspector, find its **Mesh Renderer** (or Renderer) component.
4. Drag `MAT_WaterGround` into the **Materials** slot.
5. If the Ground plane has very few vertices, consider replacing it with a subdivided plane (e.g. 64×64) so vertex-level wave displacement looks smooth. A simple Quad still works — the ripple and detail effects are fragment-level.

### 3. Add `WaterRippleManager` to the Ground

1. Select the **Ground** GameObject.
2. **Add Component → WaterRippleManager**.
3. It will auto-grab the Renderer's material on Awake. No manual wiring needed.
4. Set **Ripple Fade Duration** to match the material's `_RippleFadeDuration` (default `2.0`).

### 4. Add `PlayerFootRippleSpawner` to the XR Origin

1. Find or instantiate the **XR Origin (XR Rig)** prefab in the scene
   (located at `Assets/Samples/XR Interaction Toolkit/3.3.1/Starter Assets/Prefabs/XR Origin (XR Rig).prefab`).
2. Select the **XR Origin** root GameObject.
3. **Add Component → PlayerFootRippleSpawner**.
4. **Ripple Manager** — drag the **Ground** object (which now has `WaterRippleManager`) into this slot.  
   *If left empty it will auto-find the manager at Start.*
5. **Tracking Target** — leave empty to use the XR Origin's own transform, or drag the **Main Camera** if you want ripples at the camera's feet.
6. **Water Layer Mask** — if the Ground is on a specific layer (e.g. `Water`), set this mask accordingly. Otherwise leave as `Everything`.

### 5. (Optional) Collider for Raycasting

The `PlayerFootRippleSpawner` raycasts downward to hit the water surface.  
Make sure the **Ground** has a **Collider** (Box or Mesh Collider). If it already has one (walkable ground), you're set. If not:

1. Select Ground → **Add Component → Box Collider**.
2. Size it to cover the ground area.

---

## Recommended Material Settings (Sunset Aesthetic)

These defaults are already baked into the shader but you can fine-tune them:

| Property | Value | Notes |
|----------|-------|-------|
| Shallow Color | `(0.32, 0.22, 0.28, 0.94)` | Warm dark mauve |
| Deep Color | `(0.10, 0.06, 0.15, 0.97)` | Dark indigo |
| Horizon Reflection | `(0.88, 0.58, 0.32, 1.0)` | Warm golden sunset — **key colour** |
| Reflection Strength | `0.65` | Increase for more mirror-like surface |
| Fresnel Power | `3.5` | Higher = reflections only at grazing angles |
| Wave Amplitude | `0.01` | Very calm water |
| Wave Speed | `0.5` | Slow, gentle |
| Detail Strength | `0.08` | Subtle surface shimmer |
| Specular Power | `150` | Tight sun highlight |
| Specular Intensity | `1.0` | Bright but not blown out |
| Ripple Speed | `3.0` | How fast rings expand (m/s) |
| Ripple Frequency | `18` | Number of rings within the width |
| Ripple Amplitude | `0.012` | Subtle height displacement |
| Ripple Lifetime | `2.0` | Seconds before full fade |

### For a More Mirror-Like / Still-Water Look

- Set **Wave Amplitude** to `0.003` (nearly flat).
- Increase **Reflection Strength** to `0.85`.
- Increase **Fresnel Power** to `5.0`.
- Decrease **Detail Strength** to `0.03`.

### For a Livelier Ocean Look

- Set **Wave Amplitude** to `0.05`.
- Increase **Wave Speed** to `1.5`.
- Increase **Detail Strength** to `0.15`.

---

## Troubleshooting

| Issue | Fix |
|-------|-----|
| **No ripples appear** | Ensure `WaterRippleManager` is on the Ground, `PlayerFootRippleSpawner` is on XR Origin, and the Ground has a Collider. |
| **Ripples are invisible but spawning** | Check that the material's `_RippleAmplitude` and `_RippleWidth` are non-zero. Increase `_RippleAmplitude` to `0.03` to test. |
| **Water looks flat / no waves** | The Ground mesh may have very few vertices. Replace with a subdivided plane or increase `Detail Strength` for fragment-level visual waves. |
| **Pink / magenta surface** | The shader failed to compile. Open the Console for errors — likely a URP version mismatch. The shader targets URP 14+ (Unity 2022.3+). |
| **SRP Batcher warning** | The ripple array (`_RippleData`) is intentionally outside the CBUFFER (arrays cannot be batched). Since there is only one water ground, this has no performance impact. |
| **Performance on Quest** | Reduce `maxRipples` to 5 in both the script and the `#define MAX_RIPPLES` in the shader. Reduce `Detail Strength` to 0. |

---

## File Locations

```
Assets/
├── Shaders/
│   └── MiSide_WaterGround.shader      ← Water surface shader
├── Scripts/
│   ├── WaterRippleManager.cs           ← Ripple data manager (on Ground)
│   └── PlayerFootRippleSpawner.cs      ← Movement detector  (on XR Origin)
```
