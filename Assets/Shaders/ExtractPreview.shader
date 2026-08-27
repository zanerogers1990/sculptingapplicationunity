Shader "Custom/ExtractPreview"
{
    Properties
    {
        _Color("Color", Color) = (0.30, 0.75, 1.0, 0.45)
        _RimColor("Rim Color", Color) = (0.75, 0.95, 1.0, 1.0)
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

        // Unlike Custom/BrushPreviewOverlay (ZTest Always - a cursor that must never be
        // swallowed by the model), this one is DEPTH TESTED. An extract preview is a solid
        // object sitting on the surface, and the whole point of looking at it is judging how it
        // sits - drawing it through the body would make a plate on the far side look identical
        // to one on the near side.
        //
        // Cull Off + ZWrite Off: the shell is thin and translucent, so both its inner and outer
        // surfaces should show. Writing depth would let whichever face drew first hide the
        // other, which reads as random holes as the camera orbits.
        Pass
        {
            Name "ExtractPreview"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                float3 viewDirWS  : TEXCOORD1;
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half4 _RimColor;
            CBUFFER_END

            Varyings Vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs pos = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = pos.positionCS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.viewDirWS = GetWorldSpaceViewDir(pos.positionWS);
                return output;
            }

            // Deliberately unlit - a fixed key direction plus a fresnel rim rather than the
            // scene's real lights. The preview has to stay legible while the user is changing
            // the studio lighting (which lives two panels away and may well be dialled dark for
            // presentation), and a preview that disappears because the key light moved is worse
            // than one that doesn't match the final render's shading.
            half4 Frag(Varyings input, half facing : VFACE) : SV_Target
            {
                // Cull Off means back faces arrive with the normal pointing away from the
                // camera; flipping by VFACE keeps the shading and the rim correct on both.
                float3 n = normalize(input.normalWS) * (facing > 0 ? 1.0 : -1.0);
                float3 v = normalize(input.viewDirWS);

                float wrapped = saturate(dot(n, normalize(float3(0.4, 0.8, -0.5))) * 0.5 + 0.5);
                float fresnel = pow(1.0 - saturate(dot(n, v)), 2.0);

                half3 body = _Color.rgb * lerp(0.55, 1.0, wrapped);
                half3 col = lerp(body, _RimColor.rgb, fresnel * 0.8);
                // Edges-on areas go more opaque, which is what makes a translucent shell read as
                // having a silhouette instead of a vague haze.
                half alpha = saturate(_Color.a + fresnel * (1.0 - _Color.a));
                return half4(col, alpha);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
