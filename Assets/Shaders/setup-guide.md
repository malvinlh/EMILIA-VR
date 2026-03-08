# Phase 4: Per-Scene Setup Guide — Unity Editor 6000.3.10f1

This guide tells you exactly where to find every setting referenced in Phase 4 of the shader/lighting plan.

---

## 4.1 Volume Profiles

### Creating Volume Profile Assets

1. **Project window** > right-click `Assets/Settings/` folder
2. **Create > Rendering > URP Volume Profile** (or **Volume Profile** under Rendering)
3. Name them: `VolumeProfile_3D_Chat`, `VolumeProfile_3D_Journal`, `VolumeProfile_3D_LoginArea`, `VolumeProfile_3D_StartArea`

### Attaching a Volume to a Scene

1. **Hierarchy window** > right-click > **Volume > Global Volume**
2. In the **Inspector**, set **Mode** to **Global**
3. Drag the per-scene Volume Profile asset into the **Profile** field

### Adding Overrides (all done inside the Volume component in Inspector)

Click **Add Override** button at the bottom of the Volume component:

#### Bloom

- **Add Override > Post-processing > Bloom**
- **Threshold**: tick the checkbox to enable override, then set value (0.65–0.80 depending on scene)
- **Intensity**: tick checkbox, set value (0.10–0.25)
- **Scatter**: tick checkbox, set value (0.60–0.75)

| Setting | 3D_Chat | 3D_Journal | 3D_LoginArea | 3D_StartArea |
|---|---|---|---|---|
| Threshold | 0.75 | 0.70 | 0.65 | 0.80 |
| Intensity | 0.15 | 0.20 | 0.25 | 0.10 |
| Scatter | 0.65 | 0.70 | 0.75 | 0.60 |

#### Tonemapping

- **Add Override > Post-processing > Tonemapping**
- **Mode**: tick checkbox, set to **Neutral** (all 4 scenes)
- Do NOT use ACES — it adds contrast that fights the soft pastel look

#### Vignette

- **Add Override > Post-processing > Vignette**
- **Intensity**: tick checkbox, set value (0.05–0.08, never above 0.1 for VR comfort)

| Scene | Vignette Intensity |
|---|---|
| 3D_Chat | 0.08 |
| 3D_Journal | 0.05 |
| 3D_LoginArea | 0.08 |
| 3D_StartArea | 0.05 |

#### Color Adjustments

- **Add Override > Post-processing > Color Adjustments**
- **Color Filter / Post Exposure**: available here but not needed
- **Color Temperature** (under White Balance): tick checkbox, set value (-3 to +8)
- **Contrast**: tick checkbox, set value (-5 to -2)
- **Saturation**: tick checkbox, set value (-5 to 0)

> **Note on Color Temperature in Unity 6000.3.10f1**: White Balance (Temperature and Tint) is under **Add Override > Post-processing > White Balance** as a separate override, not inside Color Adjustments.

| Setting | 3D_Chat | 3D_Journal | 3D_LoginArea | 3D_StartArea |
|---|---|---|---|---|
| Color Temperature | +5 (warm) | +8 (golden) | -3 (cool) | +2 (neutral-warm) |
| Contrast | -5 (soft) | -5 (soft) | -3 | -2 |
| Saturation | -5 (pastel) | 0 | -5 (pastel) | 0 |

#### VR Safety: Disable Motion Blur & Depth of Field

- If **Motion Blur** override exists in any profile, set **Intensity** to **0** or remove the override entirely
- If **Depth of Field** override exists, remove it or set **Mode** to **Off**
- **Fix `SampleSceneProfile.asset`**: Project window > `Assets/Settings/SampleSceneProfile.asset` > find the Motion Blur override > disable it or set intensity to 0 (currently 0.6)

---

## 4.2 Lighting Setup

### Directional Light (Main Light — exists in every scene)

1. **Hierarchy window** > select the existing **Directional Light** (or create one: right-click > **Light > Directional Light**)
2. In the **Inspector** panel, under the **Light** component:
   - **Color**: click the color swatch to set temperature (use the color temperature toggle to switch to Kelvin mode)
     - To enable Kelvin mode: click the color field > in the Color Picker, toggle **Temperature** mode at the bottom
   - **Color Temperature** (Kelvin values):
     - 3D_Chat: ~4500K (warm)
     - 3D_Journal: ~3500K (golden hour)
     - 3D_LoginArea: ~6500K (cool blue-white)
     - 3D_StartArea: ~5000K (balanced)
   - **Intensity**: set in the Intensity field
     - 3D_Chat: 1.2
     - 3D_Journal: 1.5
     - 3D_LoginArea: 0.8
     - 3D_StartArea: 1.0
   - **Shadow Type**: set to **Soft Shadows** (dropdown under Shadows section)
   - **Rotation** (Transform component): adjust X rotation for light angle
     - 3D_Journal: X = 30–40 degrees (low angle for golden hour)

### Point Lights (3D_Chat — Indoor Cozy Room)

1. **Hierarchy window** > right-click > **Light > Point Light**
2. Place 2–3 at lamp/fixture positions in the Scene view
3. **Inspector** settings:
   - **Color**: warm tone (~3000–3500K or manually set warm orange)
   - **Intensity**: 0.5–0.8
   - **Range**: 5–8 meters
   - **Shadow Type**: optional (Soft Shadows or No Shadows for performance)

### Spot Lights (3D_LoginArea — Ethereal Accents)

1. **Hierarchy window** > right-click > **Light > Spot Light**
2. Place 2 warm spot lights aimed at focal points (pedestal/book)
3. **Inspector** settings:
   - **Color**: ~3500K warm
   - **Intensity**: 1.0
   - **Spot Angle / Inner Spot Angle**: adjust to focus the cone

### Ambient Lighting (Environment Settings)

1. **Menu bar** > **Window > Rendering > Lighting** (opens the Lighting window)
2. Click the **Environment** tab at the top
3. Under **Environment Lighting**:
   - **Source**: set to **Color** (for flat ambient) or **Gradient** (for sky/equator/ground control)
   - If **Color** mode: set the ambient color directly
   - If **Gradient** mode: set Sky, Equator, Ground colors individually

| Scene | Ambient Mode | Color / Values |
|---|---|---|
| 3D_Chat | Color or Gradient | Warm (0.25, 0.22, 0.20) |
| 3D_Journal | Gradient | Warm sky from gradient, equator matching horizon |
| 3D_LoginArea | Color | Cool blue (0.18, 0.22, 0.28) |
| 3D_StartArea | Color | Neutral warm (0.22, 0.21, 0.20) |

### Sky Material (ToonSkyGradient)

1. **Menu bar** > **Window > Rendering > Lighting** > **Environment** tab
2. **Skybox Material** field: assign a material using `MiSide/ToonSkyGradient` shader
3. Create separate sky materials per scene (different colors):
   - Select the material in **Project window** > **Inspector** shows shader properties
   - Set **Top Color**, **Horizon Color**, **Bottom Color**, **Horizon Band Width**, **Horizon Haze**

| Scene | Top | Horizon | Bottom |
|---|---|---|---|
| 3D_Chat | soft blue | peachy | warm peach |
| 3D_Journal | soft blue-violet | warm horizon band | peach/coral |
| 3D_LoginArea | deep navy | purple-blue | warm amber glow |
| 3D_StartArea | clean blue | white-peach | warm |

### Fog (3D_Journal — Beach Scene)

In URP on Unity 6000.3.10f1, fog is configured via a Volume override:

1. Select the **Global Volume** in the Hierarchy
2. **Add Override > Environment > Fog** (or check the Lighting window > Environment tab for legacy fog settings)
3. Settings:
   - **Mode**: Linear
   - **Color**: warm tint (0.85, 0.8, 0.75)
   - **Start Distance**: 30m
   - **End Distance**: 80m

> Fog replaces Depth of Field for distance softening — DOF must stay OFF for VR.

### Baked Global Illumination (GI)

1. **Menu bar** > **Window > Rendering > Lighting** > **Scene** tab
2. Check **Baked Global Illumination** checkbox
3. Set **Lighting Mode** to **Baked Indirect** (recommended for performance)
4. Mark objects as **Static** in their **Inspector** > top-right **Static** checkbox (or specifically **Contribute GI** under the Static dropdown)
5. Mark lights that should contribute to baking: Light component > **Mode** = **Mixed** or **Baked**
6. Click **Generate Lighting** button at bottom of the Lighting window
7. Scenes that need baking: **3D_Chat**, **3D_Journal**

### SSAO (Screen Space Ambient Occlusion — PC Only)

SSAO is a **Renderer Feature**, not a Volume override in URP:

1. **Project window** > find your URP Renderer asset (check `Assets/Settings/` — typically named `PC_Renderer.asset` or similar)
2. Select it > **Inspector** > scroll to **Renderer Features**
3. Click **Add Renderer Feature > Screen Space Ambient Occlusion**
4. Settings:
   - **Intensity**: 0.3 (subtle depth enhancement)
   - **Radius / Sample Count**: adjust for quality vs. performance
   - **Source**: Depth Normals (requires the DepthNormalsOnly pass in shaders — added in Phase 1.2 and 3.1)

> SSAO requires shaders to output DepthNormals. The revised MiSide_Environment and new MiSide_Character shaders include a DepthNormalsOnly pass for this.

---

## 4.3 Material Assignment

### How to Change a Material's Shader

1. **Project window** > navigate to the material (`.mat` file)
2. Select it > **Inspector** panel
3. At the top, click the **Shader** dropdown menu
4. Navigate to **MiSide > [shader name]**

### Where Each Material Type Lives

| Material Type | Shader Path in Dropdown | Typical Asset Location |
|---|---|---|
| Room/building surfaces | **MiSide/Environment** | `Assets/Graphics/3D/` scene-specific folders |
| Plants/foliage | **MiSide/Environment** | Same — enable **Alpha Clipping** and set **Cull** to Off in Inspector |
| Lamps/monitors/emissive | **MiSide/Environment** | Same — enable **Emission** section in Inspector |
| Water surfaces | **MiSide/ToonWater** | Per-scene water materials |
| Skybox | **MiSide/ToonSkyGradient** | `Assets/Settings/` or `Assets/Materials/Sky/` |
| AZKi character (body, hair, clothes) | **MiSide/Character** | `Assets/Graphics/3D/Character/AZKi/materials/` |
| AZKi eyes | **MiSide/Character** | Same folder — set minimal shadow, disable outline |
| AZKi special (blush, lashes) | **MiSide/Character** | Same folder — disable outline |

### Water Material Properties (3D_Journal and 3D_LoginArea)

Select the water material > Inspector:

| Property | 3D_Journal (Beach) | 3D_LoginArea (Ethereal) |
|---|---|---|
| Shallow Color | (0.5, 0.8, 0.85) | (0.3, 0.6, 0.75) dreamy teal |
| Wave Amplitude | 0.02 (gentle) | low/slow gentle |
| Wave Speed | 1.0 | slow |

### AZKi Character Materials — Bulk Switch via Tuner

Instead of manually switching all 33 materials:

1. **Menu bar** > **Tools** (or wherever MiSideCharacterTuner is registered)
2. Run **MiSide Character Tuner** — it will:
   - Switch all materials under `Assets/Graphics/3D/Character/AZKi/materials/` to `MiSide/Character`
   - Apply tuned toon parameters using the existing serialized UTS property data
   - Delete the 10 unused `mmd_tools_rigid_*` materials

---

## 4.1 (continued) Fix: SampleSceneProfile.asset — Disable Motion Blur

1. **Project window** > `Assets/Settings/SampleSceneProfile.asset`
2. Select it > **Inspector** shows Volume Profile overrides
3. Find **Motion Blur** override
4. Either:
   - Uncheck the override entirely (click the checkbox next to "Motion Blur"), or
   - Set **Intensity** to **0**
5. Current value is 0.6 — must be disabled for VR comfort

---

## 4.2 (continued) 3D_LoginArea — Add Missing Global Volume

This scene currently has **no Global Volume**:

1. Open scene **3D_LoginArea** (File > Open Scene or double-click in Project window)
2. **Hierarchy window** > right-click > **Volume > Global Volume**
3. In Inspector, set **Mode** to **Global**
4. Assign the `VolumeProfile_3D_LoginArea` profile asset to the **Profile** field

---

## Quick Reference: Menu Paths

| What | Where to Find It |
|---|---|
| Lighting window | **Window > Rendering > Lighting** |
| Environment / Ambient | Lighting window > **Environment** tab |
| Skybox Material | Lighting window > Environment tab > **Skybox Material** |
| GI Baking | Lighting window > **Scene** tab > **Baked Global Illumination** |
| Generate Lightmaps | Lighting window > bottom **Generate Lighting** button |
| URP Renderer Asset | **Project window** > `Assets/Settings/` > select the Renderer `.asset` |
| SSAO Renderer Feature | URP Renderer Asset > Inspector > **Renderer Features** > Add |
| Volume Overrides | Select Global Volume in Hierarchy > Inspector > **Add Override** |
| Bloom | Volume > Add Override > **Post-processing > Bloom** |
| Tonemapping | Volume > Add Override > **Post-processing > Tonemapping** |
| Vignette | Volume > Add Override > **Post-processing > Vignette** |
| Color Adjustments | Volume > Add Override > **Post-processing > Color Adjustments** |
| White Balance | Volume > Add Override > **Post-processing > White Balance** |
| Motion Blur | Volume > Add Override > **Post-processing > Motion Blur** |
| Fog | Volume > Add Override > **Environment > Fog** |
| Material Shader | Select material > Inspector > **Shader** dropdown |
| Frame Debugger | **Window > Analysis > Frame Debugger** |
| SRP Batcher check | Frame Debugger > look for "SRP Batcher" compatible entries |
