Shader "Custom/BrushPreviewOverlay"
{
    Properties
    {
        _Color("Color", Color) = (0.3, 0.6, 1, 0.35)
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

        // ZTest Always + ZWrite Off: draws on top of everything already in the frame
        // regardless of the depth buffer, like ZBrush/Blender's brush cursor overlay - a
        // solid, depth-tested sphere would otherwise get swallowed by the sculpted mesh
        // whenever the preview's position lands even slightly behind its surface (which
        // happens easily while resizing, since the preview's fallback position is only an
        // approximation of the actual surface depth - see SculptController.UpdateBrushPreview).
        Pass
        {
            Name "Overlay"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
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
