Shader "MiSide/ToonSkyGradient"
{
    Properties
    {
        _TopColor ("Top Color", Color) = (0.65, 0.78, 0.92, 1)
        _HorizonColor ("Horizon Color", Color) = (1, 0.92, 0.82, 1)
        _BottomColor ("Bottom Color", Color) = (1, 0.85, 0.75, 1)
        _GradientOffset ("Gradient Offset", Range(-1, 1)) = 0.0
        _GradientScale ("Gradient Scale", Range(0.1, 5)) = 1.0
        _HorizonBandWidth ("Horizon Band Width", Range(0.01, 0.5)) = 0.15
        _HorizonHaze ("Horizon Haze", Range(0, 1)) = 0.3
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Background"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Background"
            "PreviewType" = "Skybox"
        }

        Pass
        {
            Name "SkyGradient"

            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex SkyVert
            #pragma fragment SkyFrag

            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _TopColor;
                half4 _HorizonColor;
                half4 _BottomColor;
                half  _GradientOffset;
                half  _GradientScale;
                half  _HorizonBandWidth;
                half  _HorizonHaze;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 viewDir    : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings SkyVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.viewDir = TransformObjectToWorld(IN.positionOS.xyz) - _WorldSpaceCameraPos.xyz;

                return OUT;
            }

            half4 SkyFrag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                float3 viewDir = normalize(IN.viewDir);

                // Base vertical gradient [0,1] with scale and offset
                float gradientT = viewDir.y * 0.5 + 0.5;
                gradientT = saturate(gradientT * _GradientScale + _GradientOffset);

                // 3-color gradient: bottom -> horizon -> top
                // Horizon band centered around gradientT = 0.5
                half horizonCenter = 0.5;
                half halfBand = _HorizonBandWidth;

                // Bottom to horizon blend
                half bottomToHorizon = smoothstep(
                    horizonCenter - halfBand * 2.0,
                    horizonCenter - halfBand * 0.5,
                    gradientT);

                // Horizon to top blend
                half horizonToTop = smoothstep(
                    horizonCenter + halfBand * 0.5,
                    horizonCenter + halfBand * 2.0,
                    gradientT);

                // Compose: start with bottom, blend to horizon, then to top
                half4 color = lerp(_BottomColor, _HorizonColor, bottomToHorizon);
                color = lerp(color, _TopColor, horizonToTop);

                // Atmospheric haze: soft glow near horizon
                half hazeAmount = exp(-abs(viewDir.y) * (4.0 / max(_HorizonBandWidth, 0.01)));
                color.rgb = lerp(color.rgb, _HorizonColor.rgb, hazeAmount * _HorizonHaze);

                return color;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
