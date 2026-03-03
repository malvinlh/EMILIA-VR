Shader "MiSide/ToonSkyGradient"
{
    Properties
    {
        _TopColor ("Top Color", Color) = (0.65, 0.78, 0.92, 1)
        _BottomColor ("Bottom Color", Color) = (1, 0.85, 0.75, 1)
        _GradientOffset ("Gradient Offset", Range(-1, 1)) = 0.0
        _GradientScale ("Gradient Scale", Range(0.1, 5)) = 1.0
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
                half4 _BottomColor;
                half  _GradientOffset;
                half  _GradientScale;
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
                // View direction in world space for gradient computation
                OUT.viewDir = TransformObjectToWorld(IN.positionOS.xyz) - _WorldSpaceCameraPos.xyz;

                return OUT;
            }

            half4 SkyFrag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                // Normalize view direction and use Y component for vertical gradient
                float3 viewDir = normalize(IN.viewDir);
                // Map from [-1, 1] to [0, 1]
                float gradientT = viewDir.y * 0.5 + 0.5;
                // Apply scale and offset
                gradientT = saturate(gradientT * _GradientScale + _GradientOffset);

                half4 color = lerp(_BottomColor, _TopColor, gradientT);
                return color;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
