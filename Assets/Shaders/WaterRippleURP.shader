Shader "Custom/WaterRipple"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.86, 0.96, 1.0, 0.85)
        _Progress ("Progress", Range(0, 1)) = 0
        _RingWidth ("Ring Width", Range(0.01, 0.45)) = 0.14
        _EdgeSoftness ("Edge Softness", Range(0.001, 0.15)) = 0.03
        _MaxRadius ("Max Radius", Range(0.05, 0.75)) = 0.48
        _FadePower ("Fade Power", Range(0.2, 4.0)) = 1.35
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "WaterRipple"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half _Progress;
                half _RingWidth;
                half _EdgeSoftness;
                half _MaxRadius;
                half _FadePower;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                float progress = saturate(_Progress);
                float2 centered = IN.uv * 2.0 - 1.0;
                float dist = length(centered);

                float radius = progress * _MaxRadius;
                float ringWidth = max(_RingWidth, 0.001);
                float softness = max(_EdgeSoftness, 0.0001);
                float innerRadius = max(radius - ringWidth, 0.0);

                float outerMask = 1.0 - smoothstep(radius, radius + softness, dist);
                float innerMask = smoothstep(innerRadius - softness, innerRadius + softness, dist);
                float ringMask = saturate(outerMask * innerMask);

                float timeFade = pow(1.0 - progress, _FadePower);
                float alpha = ringMask * _BaseColor.a * timeFade;

                return half4(_BaseColor.rgb, alpha);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
