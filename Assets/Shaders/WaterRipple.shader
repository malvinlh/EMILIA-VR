Shader "Custom/WaterRipple"
{
    Properties
    {
        _Color ("Ripple Color", Color) = (1, 1, 1, 0.6)
        _RingWidth ("Ring Width", Range(0.01, 0.3)) = 0.08
        _Progress ("Progress", Range(0, 1)) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+1" "RenderPipeline" = "UniversalPipeline" }
        LOD 100

        Pass
        {
            Name "RippleForward"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float _RingWidth;
                float _Progress;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                // Center UV to [-0.5, 0.5]
                float2 centeredUV = input.uv - 0.5;
                float dist = length(centeredUV) * 2.0; // 0 at center, 1 at edge

                // Ring radius expands with progress
                float radius = _Progress;

                // Create a ring shape
                float ring = 1.0 - smoothstep(_RingWidth, _RingWidth * 2.0, abs(dist - radius));

                // Fade out as the ripple expands
                float fade = 1.0 - _Progress;

                // Discard fully transparent pixels
                float alpha = ring * fade * _Color.a;
                clip(alpha - 0.01);

                return half4(_Color.rgb, alpha);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
