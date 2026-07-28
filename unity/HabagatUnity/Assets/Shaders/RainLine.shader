// Rain streaks, drawn as GL_LINES.
//
// One mesh with line topology, exactly like the web build's THREE.LineSegments —
// 9000 individual objects would be unthinkable, and a particle system would not
// give the same taut vertical streak. Unlit and additive-ish so drops read against
// both the pale clear sky and the dark storm one without needing two materials.
Shader "Habagat/RainLine"
{
    Properties
    {
        _Color ("Color", Color) = (0.659, 0.816, 0.961, 1)
        _Opacity ("Opacity", Range(0,1)) = 0.65
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }

        Pass
        {
            Name "Rain"
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
                half _Opacity;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings   { float4 positionCS : SV_POSITION; };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                return half4(_Color.rgb, _Opacity);
            }
            ENDHLSL
        }
    }
}
