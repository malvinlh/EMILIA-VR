Shader "MiSide/ToonWater"
{
    Properties
    {
        [Header(Colors)]
        _ShallowColor ("Shallow Color", Color) = (0.45, 0.78, 0.88, 0.85)
        _DeepColor ("Deep Color", Color) = (0.18, 0.38, 0.58, 0.85)
        _ColorGradientScale ("Color Gradient Scale", Range(0, 2)) = 1.0
        _ColorGradientOffset ("Color Gradient Offset", Range(-1, 1)) = 0.0

        [Header(Foam)]
        _FoamTex ("Foam Texture", 2D) = "white" {}
        _FoamColor ("Foam Color", Color) = (1, 1, 1, 0.5)
        _FoamSpeed ("Foam Scroll Speed", Range(0, 0.5)) = 0.08
        _FoamScale ("Foam UV Scale", Range(0.1, 5)) = 1.0
        _FoamIntensity ("Foam Intensity", Range(0, 1)) = 0.3

        [Header(Waves)]
        _WaveAmplitude ("Wave Amplitude", Range(0, 0.2)) = 0.03
        _WaveFrequency ("Wave Frequency", Range(0.5, 10)) = 2.0
        _WaveSpeed ("Wave Speed", Range(0, 5)) = 1.5

        [Header(Rendering)]
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull Mode", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
        }

        // =====================================================
        // FORWARD PASS (Unlit Transparent Water)
        // =====================================================
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex WaterVert
            #pragma fragment WaterFrag

            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4  _ShallowColor;
                half4  _DeepColor;
                half   _ColorGradientScale;
                half   _ColorGradientOffset;
                float4 _FoamTex_ST;
                half4  _FoamColor;
                half   _FoamSpeed;
                half   _FoamScale;
                half   _FoamIntensity;
                half   _WaveAmplitude;
                half   _WaveFrequency;
                half   _WaveSpeed;
            CBUFFER_END

            TEXTURE2D(_FoamTex); SAMPLER(sampler_FoamTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                half   fogFactor  : TEXCOORD1;
                float2 worldXZ    : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings WaterVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                float3 posOS = IN.positionOS.xyz;

                // Simple sine-based wave displacement on Y axis
                float3 posWS = TransformObjectToWorld(posOS);
                float waveInput = posWS.x * _WaveFrequency + posWS.z * _WaveFrequency * 0.7
                                + _Time.y * _WaveSpeed;
                float wave = sin(waveInput) * _WaveAmplitude;
                float wave2 = sin(waveInput * 1.3 + 2.1) * _WaveAmplitude * 0.5;
                posOS.y += wave + wave2;

                OUT.positionCS = TransformObjectToHClip(posOS);
                OUT.uv = IN.uv;
                OUT.fogFactor = ComputeFogFactor(OUT.positionCS.z);
                OUT.worldXZ = posWS.xz;

                return OUT;
            }

            half4 WaterFrag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                // Two-color gradient based on UV.y
                float gradientT = saturate(IN.uv.y * _ColorGradientScale + _ColorGradientOffset);
                half4 waterColor = lerp(_DeepColor, _ShallowColor, gradientT);

                // Scrolling foam overlay
                float2 foamUV = IN.worldXZ * _FoamScale + float2(_Time.y * _FoamSpeed, _Time.y * _FoamSpeed * 0.7);
                half4 foam = SAMPLE_TEXTURE2D(_FoamTex, sampler_FoamTex, foamUV);
                waterColor.rgb += foam.rgb * _FoamColor.rgb * _FoamIntensity * foam.a;

                // Apply fog
                waterColor.rgb = MixFog(waterColor.rgb, IN.fogFactor);

                return waterColor;
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Unlit"
}
