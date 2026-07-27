// The flood surface.
//
// Colour AND opacity both come from the vertex stream: shallow water is pale and
// see-through, deep water is the ocean's own colour and nearly opaque. That
// per-vertex alpha is what lets a puddle stay glassy while a metre of flood
// reads as solid, using one mesh and one draw call.
//
// Depth is not written, so the sheet never occludes itself where it folds over
// a bank, and it is queued after opaque geometry.
Shader "Habagat/WaterVertexColor"
{
    Properties
    {
        _Smoothness ("Smoothness", Range(0,1)) = 0.88
        _SpecStrength ("Specular Strength", Range(0,2)) = 0.6
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half _Smoothness;
                half _SpecStrength;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                half4  color      : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                half4  color      : COLOR;
                float  fogCoord   : TEXCOORD2;
            };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs pos = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs nrm = GetVertexNormalInputs(IN.normalOS);
                OUT.positionCS = pos.positionCS;
                OUT.positionWS = pos.positionWS;
                OUT.normalWS = nrm.normalWS;
                OUT.color = IN.color;
                OUT.fogCoord = ComputeFogFactor(pos.positionCS.z);
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                float3 N = normalize(IN.normalWS);
                float3 V = normalize(GetWorldSpaceViewDir(IN.positionWS));

                Light mainLight = GetMainLight(TransformWorldToShadowCoord(IN.positionWS));
                half3 lightColor = mainLight.color * mainLight.shadowAttenuation;

                half3 diffuse = LightingLambert(lightColor, mainLight.direction, N);
                half3 ambient = SampleSH(N);

                // A single broad highlight. Water this stylised does not want a
                // sharp mirror — it wants the sun smeared across the swell.
                float3 Hv = normalize(mainLight.direction + V);
                half spec = pow(saturate(dot(N, Hv)), exp2(_Smoothness * 10.0h) + 1.0h) * _SpecStrength;

                half3 col = IN.color.rgb * (diffuse + ambient) + lightColor * spec;
                col = MixFog(col, IN.fogCoord);

                // The highlight has to lift opacity too, or it appears to shine
                // through shallow water that is meant to be nearly invisible.
                half alpha = saturate(IN.color.a + spec * 0.35h);
                return half4(col, alpha);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Unlit"
}
