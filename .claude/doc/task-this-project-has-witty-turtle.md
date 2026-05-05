# Plan: Fix Passthrough Leaking Through Glass and Vignette

## Context

On Meta Quest 3 with OpenXR, the **alpha channel of the eye buffer is the passthrough compositor key** — any pixel with alpha < 1 blends with the real-world passthrough underlay, even in VR mode. Because `arCameraManager.enabled = true` is always set in `PassthroughManager.Awake()` (line 91), OpenXR has a passthrough layer registered at all times. Two things produce sub-1 alpha pixels in VR mode:

1. **Glass shader** — outputs `finalAlpha` between 0.15–0.75 (from `_GlassColor.a` Fresnel blend), and ShaderGUI switches the material to `SrcAlpha / OneMinusSrcAlpha` transparent mode when glass is enabled.
2. **Tunneling vignette** — `Blend SrcAlpha OneMinusSrcAlpha` applies to **both** RGB and Alpha channels. The feathering gradient writes alpha < 1 into the eye buffer even over VR geometry, and that fraction bleeds through to passthrough in the compositor.

The `TunnelingVignetteBootstrap.cs` is not the bug source — it is a clean wrapper around XRI's `TunnelingVignetteController`. No changes needed to the Bootstrap.

---

## Fix 1 — Glass Shader (2 files)

### `Assets/Shaders/MiSide_Environment.shader` — line 330

Remove the semi-transparent alpha output from the `_GLASS` block. The reflective glass look (Fresnel + cubemap probe) works without alpha transparency — treat glass as an **opaque reflective surface**.

```hlsl
// BEFORE
finalAlpha = lerp(_GlassColor.a, saturate(_GlassColor.a + 0.6), fresnel);

// AFTER
finalAlpha = 1.0; // Opaque — reflection probe + Fresnel give the glass look without passthrough leak
```

### `Assets/Shaders/Editor/MiSideShaderGUI.cs` — lines 252–261 (`UpdateKeywordsAndQueue`)

Remove the transparent-mode switch when glass is enabled. Glass no longer needs `SrcAlpha/OneMinusSrcAlpha`, `ZWrite Off`, or queue 3000.

```csharp
// BEFORE (glass branch inside UpdateKeywordsAndQueue)
if (glass)
{
    mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
    mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
    mat.SetFloat("_ZWrite", 0f);
    mat.SetOverrideTag("RenderType", "Transparent");
    if (mat.renderQueue < 3000)
        mat.renderQueue = 3000;
}

// AFTER — glass stays opaque (blend/ZWrite/queue unchanged from defaults)
// (remove the `if (glass)` block entirely; the `else` branch already
//  resets blend to One/Zero when glass is off, so no new code needed)
```

That means the entire `if (glass) { ... }` block (lines 253–261) is deleted. The surrounding `else` block already handles the non-glass case and resets to opaque. The keyword toggle (`SetKeyword`) at line 250 is kept.

---

## Fix 2 — Tunneling Vignette Shader

### `Assets/Samples/XR Interaction Toolkit/3.3.1/Starter Assets/TunnelingVignette/TunnelingVignette.shader` — lines 17–19

Change the pass blend to separate the alpha-channel blend using `BlendOp ... Max`. This takes the **maximum** of the vignette alpha and the existing framebuffer alpha, so feathering pixels over rendered VR geometry (dst alpha = 1) always produce alpha = 1.

```hlsl
// BEFORE
Blend SrcAlpha OneMinusSrcAlpha
ZTest Always
ZWrite Off

// AFTER
BlendOp Add, Max
Blend SrcAlpha OneMinusSrcAlpha, One One
ZTest Always
ZWrite Off
```

`BlendOp Add, Max` means:
- **RGB**: Add (standard alpha blend, unchanged visually)
- **Alpha**: Max — `out_a = max(vignette_alpha, existing_alpha)`

In feathering over VR geometry (dst_a = 1): `max(0.5, 1) = 1` ✓  
In the black edge (vignette_alpha = 1): `max(1, any) = 1` ✓  
In the center aperture over VR geometry (dst_a = 1, vignette_alpha = 0): `max(0, 1) = 1` ✓  

---

## Critical Files

| File | Lines | Change |
|------|-------|--------|
| `Assets/Shaders/MiSide_Environment.shader` | 330 | `finalAlpha = 1.0` |
| `Assets/Shaders/Editor/MiSideShaderGUI.cs` | 253–261 | Delete the `if (glass)` transparent block |
| `Assets/Samples/XR Interaction Toolkit/3.3.1/Starter Assets/TunnelingVignette/TunnelingVignette.shader` | 17–19 | Add `BlendOp Add, Max`; separate blend operands |

---

## Verification

1. **Glass**: In Play Mode on device or in Editor, enable glass on a material and inspect it from different angles — the surface should show Fresnel reflections with no real-world bleed-through. Confirm render queue stays at 2000 (Geometry) in the Inspector.
2. **Vignette**: Trigger locomotion (ContinuousMove) in Play Mode — the black vignette edge should fade cleanly into the VR scene with no visible real-world ring in the feathering zone.
3. **Passthrough mode**: Enter MR journaling to confirm the glass and vignette fixes don't break the intentional passthrough transition.
