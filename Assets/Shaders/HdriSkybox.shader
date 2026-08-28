Shader "Custom/HdriSkybox"
{
    Properties
    {
        _HdriTex("HDRI (equirectangular)", 2D) = "grey" {}
        _Rotation("Rotation", Range(0, 360)) = 0
        _Exposure("Exposure", Range(0, 8)) = 1
        _Tint("Tint", Color) = (1, 1, 1, 1)
    }

    // Deliberately NOT Unity's built-in Skybox/Panoramic: that shader only ends up in a
    // player build if it is added to Graphics Settings' "Always Included Shaders", and a
    // skybox picked at runtime has no asset reference to pull it in. A shader living in
    // Assets/Shaders is always built. Written against the legacy CGPROGRAM/UnityCG.cginc
    // path for the same reason GradientSkybox.shader is - a skybox has no "RenderPipeline"
    // tag to steer which pipeline's include search paths apply.
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
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _HdriTex;
            float _Rotation;
            float _Exposure;
            fixed4 _Tint;

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 dir : TEXCOORD0;
            };

            // Yaw applied to the sampling direction rather than to the mesh, so the skybox
            // spins without touching the camera or any transform - and so the same angle can
            // be handed to the CPU-side ambient/reflection bake and agree exactly.
            float3 RotateAboutY(float3 v, float degrees)
            {
                float a = degrees * UNITY_PI / 180.0;
                float s, c;
                sincos(a, s, c);
                return float3(c * v.x - s * v.z, v.y, s * v.x + c * v.z);
            }

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                // A rotation is linear, so rotating the direction here and interpolating is
                // identical to rotating per fragment, and cheaper.
                o.dir = RotateAboutY(v.vertex.xyz, _Rotation);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 d = normalize(i.dir);

                float2 uv;
                uv.x = atan2(d.z, d.x) * (1.0 / (2.0 * UNITY_PI)) + 0.5;
                uv.y = asin(clamp(d.y, -1.0, 1.0)) * (1.0 / UNITY_PI) + 0.5;

                // u wraps from 1 back to 0 along one vertical line, which makes the hardware's
                // screen-space derivative there enormous and collapses that column to the
                // smallest mip - a hard bright/dark seam. Unwrapping the derivative into
                // [-0.5, 0.5] before an explicit-gradient fetch removes it. (A plain tex2D
                // shows the seam; sampling at LOD 0 removes it but aliases the horizon.)
                float2 ddxUV = ddx(uv);
                float2 ddyUV = ddy(uv);
                ddxUV.x = frac(ddxUV.x + 0.5) - 0.5;
                ddyUV.x = frac(ddyUV.x + 0.5) - 0.5;

                half3 col = tex2Dgrad(_HdriTex, uv, ddxUV, ddyUV).rgb;
                return fixed4(col * _Exposure * _Tint.rgb, 1);
            }
            ENDCG
        }
    }

    Fallback Off
}
