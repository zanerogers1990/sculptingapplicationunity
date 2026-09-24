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

        // Cavity has no material properties: it's the screen-space curvature driven by
        // ScreenCavityFeature's globals (see ScreenCavityFactor).

        // Matcap ("material capture", as in ZBrush/Blender/Nomad): a photo of a sphere shaded
        // exactly how the surface should look, indexed by the view-space normal. It replaces
        // the whole lit result rather than feeding into it - a matcap already has its lighting
        // baked in, so running it through UniversalFragmentPBR would light it twice.
        _MatcapEnabled("Matcap Enabled", Float) = 0
        _MatcapTex("Matcap", 2D) = "white" {}
        _MatcapIntensity("Matcap Intensity", Range(0,3)) = 1.0
        _MatcapTintStrength("Matcap Tint By Base Color", Range(0,1)) = 0.0

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

        // Shared by the forward pass and the cavity normals pass, so both see one
        // UnityPerMaterial layout.
        HLSLINCLUDE
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half _Metallic;
                half _Smoothness;
                half _NormalStrength;
                half _NormalNoiseScale;
                half _FlatShading;
                half4 _MaskTintColor;
                half _MaskTintStrength;
                half _MatcapEnabled;
                half _MatcapIntensity;
                half _MatcapTintStrength;
            CBUFFER_END

            // "Shade Flat" normal from the screen-space derivatives of the interpolated world
            // position - see the forward fragment's remarks. Flipped to agree with the smooth
            // normal because the cross product's sign depends on derivative direction.
            float3 SculptShadingNormalWS(float3 positionWS, float3 normalWS)
            {
                float3 smoothNormalWS = normalize(normalWS);
                if (_FlatShading < 0.5)
                    return smoothNormalWS;
                float3 flatNormalWS = normalize(cross(ddy(positionWS), ddx(positionWS)));
                return dot(flatNormalWS, smoothNormalWS) < 0.0 ? -flatNormalWS : flatNormalWS;
            }
        ENDHLSL

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

            TEXTURE2D(_MatcapTex);
            SAMPLER(sampler_MatcapTex);

            // Written by ScreenCavityFeature for the camera currently rendering. Globals, not
            // material properties - cavity is a viewport setting, like Blender's.
            TEXTURE2D(_SculptCavityNormals);   // xyz = view-space normal, w = eye depth (0 = empty)
            float4 _SculptCavityParams;        // x = on for this camera, y/z = ridge/valley control, w = offset (px)
            float4 _SculptCavityTexel;         // 1/width, 1/height, width, height

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

            // Workbench's soft clamp: linear for small curvature, easing into a ceiling of
            // 0.25 / control. The control is 0.5 / ridge^2 (or 0.7 / valley^2), so the Ridge and
            // Valley sliders raise the ceiling rather than scaling the whole response.
            float CavitySoftClamp(float curvature, float control)
            {
                if (curvature < 0.5 / control)
                    return curvature * (1.0 - curvature * control);
                return 0.25 / control;
            }

            // Blender Workbench's screen-space cavity (Viewport Shading > Cavity > Screen): a port
            // of curvature_compute in workbench_curvature_lib.glsl. The divergence of the
            // view-space normal field across the pixel's four neighbours says how fast the surface
            // turns on screen - positive over a ridge, negative in a valley - and becomes a
            // multiplier on the final colour: up to x(1 + ridge^2) on ridges, down to
            // x(1 - 0.71 valley^2) in valleys. Because it's measured in screen pixels, zooming in
            // makes the same crease read softer and zooming out makes fine detail pop - the look
            // Blender users expect, and why it needs no tuning per mesh density.
            //
            // Returns 1 (no change) when this camera has no cavity buffer, and at silhouettes:
            // Workbench rejects a pixel whose neighbours belong to another object (via its
            // object-id buffer); the nearest thing this buffer carries is a jump in eye depth,
            // which also covers the empty background (w = 0) around the model's outline.
            half ScreenCavityFactor(float4 positionCS)
            {
                if (_SculptCavityParams.x < 0.5)
                    return 1.0;

                float2 uv = GetNormalizedScreenSpaceUV(positionCS);
                float2 dx = float2(_SculptCavityTexel.x * _SculptCavityParams.w, 0.0);
                float2 dy = float2(0.0, _SculptCavityTexel.y * _SculptCavityParams.w);

                float4 centre = SAMPLE_TEXTURE2D_LOD(_SculptCavityNormals, sampler_LinearClamp, uv, 0);
                float4 up     = SAMPLE_TEXTURE2D_LOD(_SculptCavityNormals, sampler_LinearClamp, uv + dy, 0);
                float4 down   = SAMPLE_TEXTURE2D_LOD(_SculptCavityNormals, sampler_LinearClamp, uv - dy, 0);
                float4 right  = SAMPLE_TEXTURE2D_LOD(_SculptCavityNormals, sampler_LinearClamp, uv + dx, 0);
                float4 left   = SAMPLE_TEXTURE2D_LOD(_SculptCavityNormals, sampler_LinearClamp, uv - dx, 0);

                float4 neighbourDepth = float4(up.w, down.w, right.w, left.w);
                if (centre.w <= 0.0 || any(abs(neighbourDepth - centre.w) > centre.w * 0.05))
                    return 1.0;

                float normalDiff = (up.y - down.y) + (right.x - left.x);
                float curvature = normalDiff < 0.0
                    ? -2.0 * CavitySoftClamp(-normalDiff, _SculptCavityParams.z)
                    :  2.0 * CavitySoftClamp(normalDiff, _SculptCavityParams.y);
                return (half)clamp(1.0 + curvature, 0.0, 4.0);
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

            // Looks the shading up in the matcap by the normal in VIEW space, which is what
            // pins the lighting to the camera the way ZBrush/Blender/Nomad do it: orbit the
            // model and the highlight stays put on screen while the surface turns under it.
            // Deliberately the plain normal.xy mapping rather than a reflection-vector one -
            // matcaps are authored as an orthographic photo of a sphere, and that is the
            // mapping they are drawn to be read back with.
            //
            // Takes normalWS AFTER flat-shading and the procedural normal detail have had their
            // say, so faceting and surface grain read through a matcap exactly as they do under
            // real lights.
            half3 MatcapShade(float3 normalWS)
            {
                float3 normalVS = normalize(mul((float3x3)UNITY_MATRIX_V, normalWS));
                float2 matcapUV = normalVS.xy * 0.5 + 0.5;
                half3 color = SAMPLE_TEXTURE2D(_MatcapTex, sampler_MatcapTex, matcapUV).rgb * _MatcapIntensity;

                // Off by default: a matcap carries its own colour and multiplying the base colour
                // through it just fights that. Kept as a dial for the grey-clay case, where a
                // neutral matcap plus a tint is exactly what's wanted.
                color *= lerp(half3(1, 1, 1), _BaseColor.rgb, _MatcapTintStrength);
                return color;
            }

            half4 PhysicallyShade(Varyings input, float3 normalWS, half3 albedo)
            {
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

                return UniversalFragmentPBR(inputData, surfaceData);
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
                float3 normalWS = SculptShadingNormalWS(input.positionWS, input.normalWS);
                if (_NormalStrength > 0.0001)
                    normalWS = PerturbNormal(normalWS, positionOS);

                half4 litColor;
                if (_MatcapEnabled > 0.5)
                    litColor = half4(MatcapShade(normalWS), 1.0);
                else
                    litColor = PhysicallyShade(input, normalWS, _BaseColor.rgb);

                // Cavity multiplies the FINISHED colour, as Workbench composites it - over a
                // matcap's baked lighting or the lit PBR result alike - and before the mask tint,
                // so a masked crease still reads as a crease.
                litColor.rgb *= ScreenCavityFactor(input.positionCS);

                // vertex color .g: 0 = unmasked, 1 = fully protected (written by
                // SculptableMesh.PaintMask/RecomputeCavityAt). Darkens the fully-lit result
                // (diffuse + specular together) rather than just the albedo fed INTO lighting -
                // darkening albedo alone left a masked patch sitting under a specular highlight
                // looking just as bright as before, since specular reflectance on a
                // non-metallic surface is nearly independent of albedo. Post-lighting darkening
                // matches how ZBrush/Mudbox actually render a mask overlay: a uniformly darker
                // grey regardless of what's lighting that patch - and it lands the same way on
                // the matcap path, which has no lighting term to darken at all.
                half mask = input.color.g;
                litColor.rgb = lerp(litColor.rgb, litColor.rgb * _MaskTintColor.rgb, saturate(mask * _MaskTintStrength));
                return litColor;
            }
            ENDHLSL
        }

        // Feeds ScreenCavityFeature's buffer: view-space normal + eye depth, drawn before
        // opaques. Same vertex transform as ForwardLit so each forward fragment finds its own
        // surface at the centre of the buffer. The procedural normal detail is deliberately left
        // out - it's surface grain, and cavity would outline every speck of it.
        Pass
        {
            Name "SculptCavityNormals"
            Tags { "LightMode" = "SculptCavityNormals" }
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex CavityNormalsVertex
            #pragma fragment CavityNormalsFragment
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings CavityNormalsVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = posInputs.positionCS;
                output.positionWS = posInputs.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            float4 CavityNormalsFragment(Varyings input) : SV_Target
            {
                float3 normalWS = SculptShadingNormalWS(input.positionWS, input.normalWS);
                float3 normalVS = normalize(mul((float3x3)UNITY_MATRIX_V, normalWS));
                // Eye depth from the view-space position, not positionCS.w, so it's right under
                // an orthographic camera too.
                float eyeDepth = -TransformWorldToView(input.positionWS).z;
                return float4(normalVS, eyeDepth);
            }
            ENDHLSL
        }

        UsePass "Universal Render Pipeline/Lit/SHADOWCASTER"
        UsePass "Universal Render Pipeline/Lit/DEPTHONLY"
        UsePass "Universal Render Pipeline/Lit/DEPTHNORMALS"
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
