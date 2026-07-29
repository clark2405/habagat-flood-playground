// Gradient sky dome.
//
// Vertex-coloured and unlit, so it travels the exact same tone-mapping path as the
// fog colour does. That matters: the dome's bottom band IS the fog colour, and if
// the two went through different curves they would no longer match on screen and a
// hard horizon line would appear exactly where the terrain fades out.
//
// Rendered from the inside (Cull Front), with fog off — fogging the sky would tint
// it toward itself — and with depth writes off at the back of the opaque queue so
// it can never occlude anything.
Shader "Habagat/SkyDome"
{
    SubShader
    {
        Tags { "RenderType" = "Background" "RenderPipeline" = "UniversalPipeline" "Queue" = "Background" }

        Pass
        {
            Name "Sky"
            Tags { "LightMode" = "UniversalForward" }

            Cull Front
            ZWrite Off
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; half4 color : COLOR; };
            struct Varyings   { float4 positionCS : SV_POSITION; half4 color : COLOR; };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.color = IN.color;
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target { return half4(IN.color.rgb, 1.0h); }
            ENDHLSL
        }
    }
}
