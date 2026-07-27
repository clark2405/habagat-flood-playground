// Ground shader for terrain and the surrounding world.
//
// URP's own Lit shader ignores vertex colours, and the entire ground palette in
// this project lives in vertex colours — the elevation ramp plus the world-space
// tint. So the lighting model is reimplemented here, deliberately minimal:
// Lambert diffuse from the main light plus the ambient probe, which is what the
// web build's MeshStandardMaterial (roughness 0.85, metalness 0.05) effectively
// reduces to at this art direction.
//
// Vertex colours are treated as LINEAR, matching three.js, which assumes vertex
// colour attributes are already in the working colour space.
Shader "Habagat/VertexColorLit"
{
    Properties
    {
        _Tint ("Tint", Color) = (1,1,1,1)
        // Wetness darkens and slightly cools the ground during rain, the same
        // single value that drives it across the whole world in the web build.
        _Wetness ("Wetness", Range(0,1)) = 0
        // The outerland ring mesh is drawn double-sided (Cull Off). Its rings can
        // fold slightly where the warp is strongest, and a backface hole there
        // punches a window straight through the horizon.
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Tint;
                half _Wetness;
                float _Cull;
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
                half3 albedo = IN.color.rgb * _Tint.rgb;

                // Wet ground reads darker and very slightly cooler.
                albedo *= (1.0h - 0.15h * _Wetness);
                albedo.b *= (1.0h + 0.07h * _Wetness);

                float3 N = normalize(IN.normalWS);

                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                half3 lightColor = mainLight.color * (mainLight.shadowAttenuation * mainLight.distanceAttenuation);

                half3 diffuse = LightingLambert(lightColor, mainLight.direction, N);
                half3 ambient = SampleSH(N);

                half3 col = albedo * (diffuse + ambient);
                col = MixFog(col, IN.fogCoord);
                return half4(col, 1.0h);
            }
            ENDHLSL
        }

        // Reuse URP's own passes for everything that does not need vertex colour.
        UsePass "Universal Render Pipeline/Lit/ShadowCaster"
        UsePass "Universal Render Pipeline/Lit/DepthOnly"
        UsePass "Universal Render Pipeline/Lit/DepthNormals"
    }

    FallBack "Universal Render Pipeline/Lit"
}
