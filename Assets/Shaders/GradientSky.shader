Shader "Custom/GradientSky"
{
    Properties
    {
        _ColorBottom("Bottom Color", Color) = (0.10, 0.11, 0.14, 1)
        _ColorTop("Top Color", Color) = (0.35, 0.42, 0.55, 1)
        _Bias("Gradient Bias", Range(0.2, 4)) = 1
    }

    // A MESH version of GradientSkybox, drawn on a sphere that follows the camera. It exists
    // because RenderSettings.skybox is global and singular: when an HDRI is lighting the
    // scene, the skybox slot is spoken for by that HDRI (it is what feeds the ambient probe
    // and the default reflection), so a user who wants HDRI light but a plain coloured
    // backdrop cannot get the backdrop from the skybox slot. The camera is then set to
    // SolidColor - which makes URP skip the skybox pass entirely - and this dome supplies the
    // gradient instead. Its output matches GradientSkybox's math exactly so toggling the HDRI
    // on and off does not change how the background looks.
    //
    // ZTest Always + Queue Background: the dome is drawn first with no depth interaction at
    // all, so its physical radius never occludes or is occluded by the sculpt, and the depth
    // buffer is left cleared for the opaque pass that follows.
    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "IgnoreProjector" = "True" }
        Cull Off
        ZWrite Off
        ZTest Always

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
                // From the world position rather than the object-space position, so the dome
                // still reads correctly if it is ever rotated or off-centre from the camera.
                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.dir = worldPos - _WorldSpaceCameraPos;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 d = normalize(i.dir);
                float t = saturate(d.y * 0.5 + 0.5);
                t = pow(t, 1.0 / max(0.001, _Bias));
                return fixed4(lerp(_ColorBottom.rgb, _ColorTop.rgb, t), 1);
            }
            ENDCG
        }
    }

    Fallback Off
}
