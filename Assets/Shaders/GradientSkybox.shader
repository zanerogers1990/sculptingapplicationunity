Shader "Custom/GradientSkybox"
{
    Properties
    {
        _ColorBottom("Bottom Color", Color) = (0.10, 0.11, 0.14, 1)
        _ColorTop("Top Color", Color) = (0.35, 0.42, 0.55, 1)
        _Bias("Gradient Bias", Range(0.2, 4)) = 1
    }

    // Written against the legacy CGPROGRAM/UnityCG.cginc path (rather than URP's
    // ShaderLibrary) since skyboxes have no "RenderPipeline" tag to steer which pipeline's
    // include search paths apply, and Core.hlsl wasn't resolvable in that untagged context.
    // UnityCG.cginc is always available and this is how Unity's own built-in gradient/
    // procedural skyboxes are written.
    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" }
        Cull Off
        ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 dir : TEXCOORD0;
            };

            fixed4 _ColorBottom;
            fixed4 _ColorTop;
            float _Bias;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.dir = normalize(v.vertex.xyz);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float t = saturate(i.dir.y * 0.5 + 0.5);
                t = pow(t, 1.0 / max(0.001, _Bias));
                return fixed4(lerp(_ColorBottom.rgb, _ColorTop.rgb, t), 1);
            }
            ENDCG
        }
    }
}
