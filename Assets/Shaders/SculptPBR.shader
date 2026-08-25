Shader "Custom/SculptPBR"
{
    Properties
    {
        _BaseColor("Base Color", Color) = (0.65,0.65,0.68,1)
        _Metallic("Metallic", Range(0,1)) = 0.0
        _Smoothness("Smoothness", Range(0,1)) = 0.4

        _NormalStrength("Normal Detail Strength", Range(0,2)) = 0.3
        _NormalNoiseScale("Normal Detail Scale", Range(1,300)) = 60
        _FlatShading("Flat Shading", Float) = 0

        _CavityEnabled("Cavity Enabled", Float) = 1
        _RecessColor("Recess Color", Color) = (0.12,0.10,0.09,1)
        _PeakColor("Peak Color", Color) = (1.0,0.96,0.86,1)
        _CavityIntensity("Cavity Intensity", Range(0,2)) = 1.0
        _CavityRange("Cavity Range", Range(0.05,0.6)) = 0.25

        // Darker grey rather than a saturated color - matches ZBrush/Blender/Mudbox's
        // convention of shading masked areas toward grey/black instead of tinting them a
        // color, so the mask overlay doesn't read as "painted" onto the surface.
        _MaskTintColor("Mask Tint Color", Color) = (0.1,0.1,0.1,1)
        _MaskTintStrength("Mask Tint Strength", Range(0,1)) = 0.6

        // Mirrored URP Lit properties (unused by the forward pass below) so the
        // ShadowCaster/DepthOnly/DepthNormals passes reused via UsePass compile - those
        // passes' shared HLSL reads these by name.
        [HideInInspector] _BaseMap("Albedo", 2D) = "white" {}
        [HideInInspector] _Cutoff("Alpha Cutoff", Range(0,1)) = 0.5
        [HideInInspector] _Surface("__surface", Float) = 0.0
        [HideInInspector] _AlphaClip("__clip", Float) = 0.0
        [HideInInspector] _Cull("__cull", Float) = 2.0
        [HideInInspector] _ZWrite("__zw", Float) = 1.0
        [HideInInspector] _SrcBlend("__src", Float) = 1.0
        [HideInInspector] _DstBlend("__dst", Float) = 0.0
        [HideInInspector] _SrcBlendAlpha("__srcA", Float) = 1.0
        [HideInInspector] _DstBlendAlpha("__dstA", Float) = 0.0
        [HideInInspector] _AlphaToMask("__a2c", Float) = 0.0
        [HideInInspector] _ReceiveShadows("Receive Shadows", Float) = 1.0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "Queue" = "Geometry"
        }
        LOD 300

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.5

            #pragma vertex SculptPBRVertex
            #pragma fragment SculptPBRFragment

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 color      : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float4 color      : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half _Metallic;
                half _Smoothness;
                half _NormalStrength;
                half _NormalNoiseScale;
                half _FlatShading;
                half _CavityEnabled;
                half4 _RecessColor;
                half4 _PeakColor;
                half _CavityIntensity;
                half _CavityRange;
                half4 _MaskTintColor;
                half _MaskTintStrength;
            CBUFFER_END

            // Compact hashed value noise for a small tangent/UV-independent surface
            // micro-bump (see _NormalStrength). The mesh's spherical remesh UVs get badly
            // distorted near the poles (see MeshRemesher), so an ordinary UV-mapped normal
            // map would smear there - sampling directly from object-space position instead
            // sidesteps that entirely.
            float Hash13(float3 p)
            {
                p = frac(p * 0.1031);
                p += dot(p, p.yzx + 33.33);
                return frac((p.x + p.y) * p.z);
            }

            float ValueNoise3D(float3 p)
            {
                float3 i = floor(p);
                float3 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);

                float n000 = Hash13(i + float3(0, 0, 0));
                float n100 = Hash13(i + float3(1, 0, 0));
                float n010 = Hash13(i + float3(0, 1, 0));
                float n110 = Hash13(i + float3(1, 1, 0));
                float n001 = Hash13(i + float3(0, 0, 1));
                float n101 = Hash13(i + float3(1, 0, 1));
                float n011 = Hash13(i + float3(0, 1, 1));
                float n111 = Hash13(i + float3(1, 1, 1));

                float nx00 = lerp(n000, n100, f.x);
                float nx10 = lerp(n010, n110, f.x);
                float nx01 = lerp(n001, n101, f.x);
                float nx11 = lerp(n011, n111, f.x);
                float nxy0 = lerp(nx00, nx10, f.y);
                float nxy1 = lerp(nx01, nx11, f.y);
                return lerp(nxy0, nxy1, f.z);
            }

            float3 PerturbNormal(float3 normalWS, float3 positionOS)
            {
                float3 p = positionOS * _NormalNoiseScale;
                float e = 0.05;
                float h0 = ValueNoise3D(p);
                float hx = ValueNoise3D(p + float3(e, 0, 0));
                float hy = ValueNoise3D(p + float3(0, e, 0));
                float hz = ValueNoise3D(p + float3(0, 0, e));
                float3 grad = float3(hx - h0, hy - h0, hz - h0) / e;

                float3 n = normalize(normalWS);
                float3 tangentialGrad = grad - n * dot(grad, n);
                return normalize(n - tangentialGrad * _NormalStrength);
            }

            Varyings SculptPBRVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS);

                output.positionCS = posInputs.positionCS;
                output.positionWS = posInputs.positionWS;
                output.normalWS = normalInputs.normalWS;
                output.color = input.color;
                return output;
            }

            half4 SculptPBRFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // "Shade Flat" (Blender's term): derive a per-triangle normal from the
                // screen-space derivatives of the interpolated world position instead of
                // using the mesh's own (averaged-per-vertex, always-smooth) normalWS. This
                // needs no mesh changes - SculptableMesh's brush/adjacency/mask/cavity system
                // all depends on triangles sharing vertices, so splitting the mesh into
                // unwelded per-face vertices (the "real" way engines usually do flat shading)
                // would mean duplicating that entire per-vertex data model. Deriving flat
                // normals in the fragment stage sidesteps that: same vertex buffer, same
                // adjacency, just a different normal for lighting. ddx/ddy give two edge
                // vectors of the visible triangle; their cross product is that triangle's
                // face normal. Sign is arbitrary depending on winding/derivative direction,
                // so it's flipped to agree with the smooth normal's general direction rather
                // than trusting a fixed cross-product order.
                float3 positionOS = TransformWorldToObject(input.positionWS);
                float3 smoothNormalWS = normalize(input.normalWS);
                float3 normalWS;
                if (_FlatShading > 0.5)
                {
                    float3 flatNormalWS = normalize(cross(ddy(input.positionWS), ddx(input.positionWS)));
                    normalWS = dot(flatNormalWS, smoothNormalWS) < 0.0 ? -flatNormalWS : flatNormalWS;
                }
                else
                {
                    normalWS = smoothNormalWS;
                }
                if (_NormalStrength > 0.0001)
                    normalWS = PerturbNormal(normalWS, positionOS);

                half3 albedo = _BaseColor.rgb;
                if (_CavityEnabled > 0.5)
                {
                    // vertex color .r: 0 = convex/peak, 0.5 = flat, 1 = concave/recess
                    // (written by SculptableMesh.RecomputeCavity after every stroke)
                    half cavity = input.color.r;
                    half recessT = smoothstep(0.5, 0.5 + _CavityRange, cavity);
                    // HLSL's smoothstep is documented as undefined when the first argument
                    // is greater than the second, so this ramps the same "low edge" -> "0.5"
                    // range and inverts the result rather than calling smoothstep(0.5, 0.5 -
                    // _CavityRange, ...) directly.
                    half peakT = 1.0 - smoothstep(0.5 - _CavityRange, 0.5, cavity);
                    albedo = lerp(albedo, _RecessColor.rgb, saturate(recessT * _CavityIntensity));
                    albedo = lerp(albedo, _PeakColor.rgb, saturate(peakT * _CavityIntensity));
                }

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.positionCS = input.positionCS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                inputData.fogCoord = 0;
                inputData.vertexLighting = half3(0, 0, 0);
                inputData.bakedGI = SampleSHPixel(half3(0, 0, 0), normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                SurfaceData surfaceData;
                surfaceData.albedo = albedo;
                surfaceData.specular = half3(0, 0, 0);
                surfaceData.metallic = _Metallic;
                surfaceData.smoothness = _Smoothness;
                surfaceData.normalTS = half3(0, 0, 1);
                surfaceData.emission = half3(0, 0, 0);
                surfaceData.occlusion = 1.0;
                surfaceData.alpha = 1.0;
                surfaceData.clearCoatMask = 0.0;
                surfaceData.clearCoatSmoothness = 1.0;

                half4 litColor = UniversalFragmentPBR(inputData, surfaceData);

                // vertex color .g: 0 = unmasked, 1 = fully protected (written by
                // SculptableMesh.PaintMask/RecomputeCavityAt). Darkens the fully-lit result
                // (diffuse + specular together) rather than just the albedo fed INTO lighting -
                // darkening albedo alone left a masked patch sitting under a specular highlight
                // looking just as bright as before, since specular reflectance on a
                // non-metallic surface is nearly independent of albedo. Post-lighting darkening
                // matches how ZBrush/Mudbox actually render a mask overlay: a uniformly darker
                // grey regardless of what's lighting that patch.
                half mask = input.color.g;
                litColor.rgb = lerp(litColor.rgb, litColor.rgb * _MaskTintColor.rgb, saturate(mask * _MaskTintStrength));
                return litColor;
            }
            ENDHLSL
        }

        UsePass "Universal Render Pipeline/Lit/SHADOWCASTER"
        UsePass "Universal Render Pipeline/Lit/DEPTHONLY"
        UsePass "Universal Render Pipeline/Lit/DEPTHNORMALS"
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
