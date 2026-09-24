Shader "Custom/MoldOverlay"
{
    // One shader for every overlay the mold tools draw: the parting sheet, the model tinted by
    // which half it lands in, the block's wire outline, and the pin/socket/channel previews.
    // They differ only in depth behaviour and culling, so those are properties rather than four
    // near-identical shaders - which also means a single material instance per overlay object
    // and no shader variants to keep in step.
    Properties
    {
        _Tint("Tint", Color) = (1, 1, 1, 1)
        _Alpha("Alpha", Range(0, 1)) = 1

        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest("ZTest", Float) = 4   // LEqual
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull", Float) = 2            // Back
        [Enum(Off, 0, On, 1)] _ZWrite("ZWrite", Float) = 0
        _OffsetFactor("Depth offset factor", Float) = 0
        _OffsetUnits("Depth offset units", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }
        LOD 100

        Pass
        {
            Name "MoldOverlay"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite [_ZWrite]
            ZTest [_ZTest]
            Cull [_Cull]
            Offset [_OffsetFactor], [_OffsetUnits]

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                half4 color       : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                float3 viewWS     : TEXCOORD1;
                half4 color       : COLOR;
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _Tint;
                half _Alpha;
                float _ZTest;
                float _Cull;
                float _ZWrite;
                float _OffsetFactor;
                float _OffsetUnits;
            CBUFFER_END

            Varyings Vert(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(positionWS);
                // Not normalized here: the line overlays carry a zero normal on purpose (see
                // Frag), and normalizing would turn that into a NaN rather than a zero.
                output.normalWS = mul((float3x3)UNITY_MATRIX_M, input.normalOS);
                output.viewWS = GetWorldSpaceViewDir(positionWS);
                output.color = input.color;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                // Headlight shading rather than scene lighting: these are overlays that have to
                // stay readable whatever the studio lights are doing, and a sheet lit by the
                // scene's key light goes black exactly when it is facing away from it - which is
                // half the time while the user orbits around the parting surface.
                float lenSq = dot(input.normalWS, input.normalWS);
                half shade = 1.0h;
                if (lenSq > 1e-6)
                {
                    float3 n = input.normalWS * rsqrt(lenSq);
                    float3 v = normalize(input.viewWS);
                    shade = (half)(0.55 + 0.45 * saturate(abs(dot(n, v))));
                }

                half4 c = input.color * _Tint;
                return half4(c.rgb * shade, c.a * _Alpha);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
