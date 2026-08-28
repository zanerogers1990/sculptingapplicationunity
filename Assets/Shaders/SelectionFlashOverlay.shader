Shader "Custom/SelectionFlashOverlay"
{
    Properties
    {
        _Color("Color", Color) = (0.3, 0.7, 1, 0.6)
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

        // Added as an extra material slot on the target's own Renderer (see
        // SelectionFlashEffect), so this pass re-renders the SAME mesh geometry a second time
        // over the top of its normal lit pass. ZTest LEqual (not Always, unlike
        // BrushPreviewOverlay) - this should respect normal depth like the surface it's
        // painted on, not punch through nearer geometry. Offset pushes it slightly toward the
        // camera so it doesn't z-fight with that first pass at identical depth.
        Pass
        {
            Name "SelectionFlash"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Offset -1, -1
            Cull Back

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
            CBUFFER_END

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                return _Color;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
