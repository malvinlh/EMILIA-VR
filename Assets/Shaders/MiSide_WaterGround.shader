Shader "MiSide/WaterGround"
{
    Properties
    {
        [Header(Water Colors)]
        _ShallowColor    ("Shallow Color",           Color) = (0.32, 0.22, 0.28, 0.94)
        _DeepColor       ("Deep Color",              Color) = (0.10, 0.06, 0.15, 0.97)
        _HorizonColor    ("Horizon / Reflection",    Color) = (0.88, 0.58, 0.32, 1)
        _ReflectionStr   ("Reflection Strength",     Range(0, 1))   = 0.65
        _FresnelPower    ("Fresnel Power",           Range(0.5, 10)) = 3.5
        _FresnelBias     ("Fresnel Bias",            Range(0, 0.5))  = 0.04

        [Header(Ambient Waves)]
        _WaveAmplitude   ("Wave Amplitude",          Range(0, 0.15)) = 0.010
        _WaveFrequency   ("Wave Frequency",          Range(0.5, 10)) = 2.5
        _WaveSpeed       ("Wave Speed",              Range(0, 3))    = 0.5

        [Header(Surface Detail)]
        _DetailScale     ("Detail UV Scale",         Range(0.1, 10)) = 1.5
        _DetailSpeed     ("Detail Anim Speed",       Range(0, 1))    = 0.12
        _DetailStrength  ("Detail Normal Strength",  Range(0, 0.5))  = 0.08

        [Header(Sun Specular)]
        _SpecularColor   ("Specular Color",          Color) = (1, 0.92, 0.7, 1)
        _SpecularPower   ("Specular Power",          Range(10, 500))  = 150
        _SpecularIntensity ("Specular Intensity",    Range(0, 3))    = 1.0

        [Header(Interactive Ripples)]
        _RippleSpeed     ("Ripple Expand Speed",     Range(0.5, 10)) = 3.0
        _RippleFrequency ("Ripple Ring Frequency",   Range(5, 50))   = 18.0
        _RippleAmplitude ("Ripple Height",           Range(0, 0.05)) = 0.012
        _RippleFadeDuration ("Ripple Lifetime (s)",  Range(0.5, 5))  = 2.0
        _RippleWidth     ("Ripple Ring Width",       Range(0.05, 2)) = 0.4

        [Header(Rendering)]
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull Mode", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderType"     = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue"          = "Transparent"
        }

        Pass
        {
            Name "WaterGround"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex   WaterVert
            #pragma fragment WaterFrag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // Must match WaterRippleManager.maxRipples
            #define MAX_RIPPLES 10

            // =========================================================
            //  SRP-Batcher CBUFFER  (scalar / vector properties)
            // =========================================================
            CBUFFER_START(UnityPerMaterial)
                half4 _ShallowColor;
                half4 _DeepColor;
                half4 _HorizonColor;
                half  _ReflectionStr;
                half  _FresnelPower;
                half  _FresnelBias;

                half  _WaveAmplitude;
                half  _WaveFrequency;
                half  _WaveSpeed;

                half  _DetailScale;
                half  _DetailSpeed;
                half  _DetailStrength;

                half4 _SpecularColor;
                half  _SpecularPower;
                half  _SpecularIntensity;

                half  _RippleSpeed;
                half  _RippleFrequency;
                half  _RippleAmplitude;
                half  _RippleFadeDuration;
                half  _RippleWidth;
            CBUFFER_END

            // Ripple array  — written each frame by WaterRippleManager.cs
            // .xy = world XZ centre   .z = spawn Time.time   .w = strength
            float4 _RippleData[MAX_RIPPLES];
            int    _RippleCount;

            // =========================================================
            //  I/O Structs
            // =========================================================
            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float2 uv         : TEXCOORD2;
                half   fogFactor  : TEXCOORD3;
                float3 viewDirWS  : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // =========================================================
            //  AMBIENT WAVES  — 3-layer sine producing height + analytic normal
            // =========================================================
            float waveHeight(float2 xz, float t)
            {
                float f = _WaveFrequency;
                float s = _WaveSpeed;
                float a = _WaveAmplitude;
                float h  = sin(xz.x * f + xz.y * f * 0.7 + t * s)        * a;
                       h += sin(xz.x * f * 0.8 - xz.y * f * 1.1
                                + t * s * 0.9 + 1.7)                      * a * 0.5;
                       h += sin(xz.x * f * 1.6 + xz.y * f * 1.4
                                + t * s * 1.3 + 3.2)                      * a * 0.25;
                return h;
            }

            float3 waveNormal(float2 xz, float t)
            {
                float f = _WaveFrequency;
                float s = _WaveSpeed;
                float a = _WaveAmplitude;
                float dhdx = 0.0, dhdz = 0.0;

                float p1 = xz.x * f + xz.y * f * 0.7 + t * s;
                float c1 = cos(p1);
                dhdx += c1 * f         * a;
                dhdz += c1 * f * 0.7   * a;

                float p2 = xz.x * f * 0.8 - xz.y * f * 1.1 + t * s * 0.9 + 1.7;
                float c2 = cos(p2);
                dhdx += c2 * f * 0.8   * a * 0.5;
                dhdz += c2 * (-f * 1.1) * a * 0.5;

                float p3 = xz.x * f * 1.6 + xz.y * f * 1.4 + t * s * 1.3 + 3.2;
                float c3 = cos(p3);
                dhdx += c3 * f * 1.6   * a * 0.25;
                dhdz += c3 * f * 1.4   * a * 0.25;

                return normalize(float3(-dhdx, 1.0, -dhdz));
            }

            // =========================================================
            //  INTERACTIVE RIPPLES  — ring-shaped expanding waves
            // =========================================================
            struct RippleResult
            {
                float  height;
                float2 gradient;   // dh/dx , dh/dz
            };

            RippleResult computeRipples(float2 xz, float t)
            {
                RippleResult r = (RippleResult)0;

                for (int i = 0; i < MAX_RIPPLES; i++)
                {
                    float4 d   = _RippleData[i];
                    float  str = d.w;
                    if (str < 0.001) continue;

                    float elapsed = t - d.z;
                    if (elapsed < 0.0 || elapsed > _RippleFadeDuration) continue;

                    float2 delta = xz - d.xy;
                    float  dist  = max(length(delta), 0.001);
                    float  rad   = elapsed * _RippleSpeed;
                    float  rd    = dist - rad;

                    // Cheap triangular ring mask (squared for smoothness)
                    float mask = max(0.0, 1.0 - abs(rd) / _RippleWidth);
                    mask *= mask;

                    // Time fade — quadratic ease-out
                    float fade = 1.0 - saturate(elapsed / _RippleFadeDuration);
                    fade *= fade;

                    float coeff = mask * fade * str * _RippleAmplitude;
                    float sArg  = rd * _RippleFrequency;

                    r.height   += sin(sArg) * coeff;
                    // Analytical gradient for normal perturbation
                    float gMag  = cos(sArg) * _RippleFrequency * coeff;
                    r.gradient += gMag * (delta / dist);
                }
                return r;
            }

            // =========================================================
            //  SURFACE DETAIL  — 3-layer sine gives fine-scale normals
            // =========================================================
            float3 detailNormal(float2 xz, float t)
            {
                float2 uv = xz * _DetailScale;
                float  ts = t  * _DetailSpeed;
                float dhdx = 0.0, dhdz = 0.0;

                float v1 = uv.x * 3.7 + uv.y * 2.9 + ts * 1.1;
                dhdx += cos(v1) *  3.7;
                dhdz += cos(v1) *  2.9;

                float v2 = uv.x * -2.3 + uv.y * 4.1 + ts * 0.8 + 2.0;
                dhdx += cos(v2) * -2.3 * 0.6;
                dhdz += cos(v2) *  4.1 * 0.6;

                float v3 = uv.x * 5.1 + uv.y * -3.5 + ts * 1.4 + 4.5;
                dhdx += cos(v3) *  5.1 * 0.3;
                dhdz += cos(v3) * -3.5 * 0.3;

                return normalize(float3(-dhdx * _DetailStrength,
                                         1.0,
                                        -dhdz * _DetailStrength));
            }

            // =========================================================
            //  VERTEX
            // =========================================================
            Varyings WaterVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                float3 posOS = IN.positionOS.xyz;
                float3 posWS = TransformObjectToWorld(posOS);

                // Gentle vertex-level wave displacement
                posOS.y += waveHeight(posWS.xz, _Time.y);

                OUT.positionCS = TransformObjectToHClip(posOS);
                OUT.positionWS = TransformObjectToWorld(posOS);
                OUT.normalWS   = TransformObjectToWorldNormal(IN.normalOS);
                OUT.uv         = IN.uv;
                OUT.fogFactor  = ComputeFogFactor(OUT.positionCS.z);
                OUT.viewDirWS  = GetWorldSpaceNormalizeViewDir(OUT.positionWS);

                return OUT;
            }

            // =========================================================
            //  FRAGMENT
            // =========================================================
            half4 WaterFrag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                float3 posWS   = IN.positionWS;
                float3 viewDir = normalize(IN.viewDirWS);

                // ---------- Combined normal ----------
                float3 nWave = waveNormal(posWS.xz, _Time.y);

                RippleResult rip = computeRipples(posWS.xz, _Time.y);
                float3 nRipple = normalize(float3(-rip.gradient.x, 1.0, -rip.gradient.y));

                float3 nDet = detailNormal(posWS.xz, _Time.y);

                // Reoriented-normal-mapping-lite blend
                float3 N = normalize(float3(
                    nWave.x + nRipple.x + nDet.x,
                    nWave.y * nRipple.y * nDet.y,
                    nWave.z + nRipple.z + nDet.z));

                // ---------- Fresnel ----------
                float NdotV   = saturate(dot(N, viewDir));
                float fresnel = _FresnelBias
                              + (1.0 - _FresnelBias) * pow(1.0 - NdotV, _FresnelPower);
                fresnel = saturate(fresnel);

                // ---------- Water colour ----------
                half3 waterCol = lerp(_ShallowColor.rgb, _DeepColor.rgb, fresnel * 0.35);
                half3 color    = lerp(waterCol, _HorizonColor.rgb, fresnel * _ReflectionStr);

                // ---------- Sun specular ----------
                Light  mainLight = GetMainLight();
                float3 H     = normalize(mainLight.direction + viewDir);
                float  NdotH = saturate(dot(N, H));
                float  spec  = pow(NdotH, _SpecularPower) * _SpecularIntensity;
                color += _SpecularColor.rgb * spec * mainLight.color;

                // ---------- Ripple glow (bright edges catch light) ----------
                float ripGlow = saturate(abs(rip.height) / max(_RippleAmplitude, 0.001)) * 0.25;
                color += _HorizonColor.rgb * ripGlow;

                // ---------- Fog ----------
                color = MixFog(color, IN.fogFactor);

                // ---------- Alpha ----------
                float alpha = lerp(_ShallowColor.a, _DeepColor.a, fresnel * 0.4);

                return half4(color, alpha);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Unlit"
}
