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

        // Lure plastic: translucent soft plastic with glitter flakes suspended in it (see
        // PlasticShade). Driven by LurePlasticPresets through SculptMaterialController; a matcap
        // still wins over it, since a matcap replaces lighting and this is all about lighting.
        _PlasticEnabled("Lure Plastic Enabled", Float) = 0
        _PlasticThinColor("Plastic Thin Color", Color) = (0.8,0.6,0.2,1)
        _PlasticThickColor("Plastic Thick Color", Color) = (0.3,0.25,0.1,1)
        _PlasticDepth("Plastic Absorption Depth (world)", Float) = 0.1
        _PlasticTransmission("Plastic Transmission", Range(0,3)) = 1
        _PlasticGloss("Plastic Gloss", Range(0,1)) = 0.8
        _FlakeColorA("Flake Color A (a = weight)", Color) = (0.2,0.5,1,1)
        _FlakeColorB("Flake Color B (a = weight)", Color) = (0.2,0.5,1,0)
        _FlakeColorC("Flake Color C (a = weight)", Color) = (0.2,0.5,1,0)
        _FlakeCellSize("Flake Cell Size (world)", Float) = 0.03
        _FlakeDensity("Flake Density", Range(0,1)) = 0.4
        _FlakeShape("Flake Shape (0 hex, 1 square)", Range(0,1)) = 0
        _FlakeTilt("Flake Tilt", Range(0,2)) = 0.35
        _FlakeSparkle("Flake Sparkle", Range(0,4)) = 1
        _MicroFlakeColor("Micro Flake Color", Color) = (0,0,0,1)
        _MicroFlakeCellSize("Micro Flake Cell Size (world)", Float) = 0.01
        _MicroFlakeDensity("Micro Flake Density", Range(0,1)) = 0.3

        // Aged metal: a bare metal with a coat over it (rust, verdigris, paint) that collects in
        // recesses and wears off raised edges, plus a dark wash pooled in the deepest recesses
        // (see MetalShade). Driven by MetalFinishPresets through SculptMaterialController.
        _MetalEnabled("Aged Metal Enabled", Float) = 0
        _MetalColor("Bare Metal Color", Color) = (0.3,0.31,0.34,1)
        _MetalSmoothness("Bare Metal Smoothness", Range(0,1)) = 0.55
        _MetalMetallic("Bare Metal Metallic", Range(0,1)) = 0.85
        _CoatColorA("Coat Color A", Color) = (0.66,0.4,0.12,1)
        _CoatColorB("Coat Color B", Color) = (0.36,0.17,0.06,1)
        _CoatMetallic("Coat Metallic", Range(0,1)) = 0
        _CoatSmoothness("Coat Smoothness", Range(0,1)) = 0.2
        _WashColor("Wash Color", Color) = (0.09,0.05,0.03,1)
        _MetalWash("Wash Strength", Range(0,1)) = 0.7
        _MetalCoverage("Coat Coverage", Range(0,1.5)) = 0.6
        _MetalEdgeWear("Edge Wear", Range(0,1.5)) = 0.8
        _MetalPatchSize("Coat Patch Size (world)", Float) = 0.2
        _MetalPatternOffset("Coat Pattern Offset (patch widths)", Vector) = (0,0,0,0)
        _MetalPatchContrast("Coat Patch Contrast", Range(0.5,10)) = 4
        _MetalPatchAmount("Coat Patchiness", Range(0,1)) = 1
        _MetalCoatCurvature("Coat Follows Recesses", Range(0,3)) = 0.4
        _MetalGrainSize("Grain Size (world)", Float) = 0.01
        _MetalGrain("Grain Strength", Range(0,1)) = 0.4
        _MetalDetail("Detail Contrast", Range(0.1,4)) = 1

        // Sculptor's oil clay (Chavant / Monster Clay / plasteline): a satin, slightly waxy
        // surface with a thin wet highlight on top, a little light bleeding past the shadow line,
        // darker and richer in the recesses (see ClayShade). Driven by ClayPresets through
        // SculptMaterialController.
        _ClayEnabled("Clay Enabled", Float) = 0
        _ClayColor("Clay Color", Color) = (0.55,0.28,0.16,1)
        _ClayRecessColor("Clay Recess Color", Color) = (0.25,0.1,0.05,1)
        _ClayScatterColor("Clay Scatter Color", Color) = (0.9,0.35,0.18,1)
        _ClaySmoothness("Clay Sheen Smoothness", Range(0,1)) = 0.45
        _ClayWetness("Clay Wet Highlight", Range(0,3)) = 0.5
        _ClayWetPower("Clay Wet Highlight Sharpness", Float) = 300
        _ClaySubsurface("Clay Subsurface", Range(0,3)) = 0.5
        _ClayRecess("Clay Recess Depth", Range(0,3)) = 0.7
        _ClayRidge("Clay Ridge Burnish", Range(0,2)) = 0.3
        _ClayMottle("Clay Mottle", Range(0,1)) = 0.08
        _ClayMottleSize("Clay Mottle Size (world)", Float) = 0.2
        _ClayGrain("Clay Grain", Range(0,3)) = 0.3
        _ClayGrainSize("Clay Grain Size (world)", Float) = 0.006
        _ClayDetail("Clay Detail Contrast", Range(0.1,4)) = 1

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
                half _PlasticEnabled;
                half4 _PlasticThinColor;
                half4 _PlasticThickColor;
                float _PlasticDepth;
                half _PlasticTransmission;
                half _PlasticGloss;
                half4 _FlakeColorA;
                half4 _FlakeColorB;
                half4 _FlakeColorC;
                float _FlakeCellSize;
                half _FlakeDensity;
                half _FlakeShape;
                half _FlakeTilt;
                half _FlakeSparkle;
                half4 _MicroFlakeColor;
                float _MicroFlakeCellSize;
                half _MicroFlakeDensity;
                half _MetalEnabled;
                half4 _MetalColor;
                half _MetalSmoothness;
                half _MetalMetallic;
                half4 _CoatColorA;
                half4 _CoatColorB;
                half _CoatMetallic;
                half _CoatSmoothness;
                half4 _WashColor;
                half _MetalWash;
                half _MetalCoverage;
                half _MetalEdgeWear;
                float _MetalPatchSize;
                float4 _MetalPatternOffset;
                half _MetalPatchContrast;
                half _MetalPatchAmount;
                half _MetalCoatCurvature;
                float _MetalGrainSize;
                half _MetalGrain;
                half _MetalDetail;
                half _ClayEnabled;
                half4 _ClayColor;
                half4 _ClayRecessColor;
                half4 _ClayScatterColor;
                half _ClaySmoothness;
                half _ClayWetness;
                float _ClayWetPower;
                half _ClaySubsurface;
                half _ClayRecess;
                half _ClayRidge;
                half _ClayMottle;
                float _ClayMottleSize;
                half _ClayGrain;
                float _ClayGrainSize;
                half _ClayDetail;
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

            // Bumps normalWS by the gradient of value noise at p (in noise space), `strength` deep.
            float3 PerturbNormalAt(float3 normalWS, float3 p, float strength)
            {
                float e = 0.05;
                float h0 = ValueNoise3D(p);
                float hx = ValueNoise3D(p + float3(e, 0, 0));
                float hy = ValueNoise3D(p + float3(0, e, 0));
                float hz = ValueNoise3D(p + float3(0, 0, e));
                float3 grad = float3(hx - h0, hy - h0, hz - h0) / e;

                float3 n = normalize(normalWS);
                float3 tangentialGrad = grad - n * dot(grad, n);
                return normalize(n - tangentialGrad * strength);
            }

            float3 PerturbNormal(float3 normalWS, float3 positionOS)
            {
                return PerturbNormalAt(normalWS, positionOS * _NormalNoiseScale, _NormalStrength);
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

            InputData BuildInputData(Varyings input, float3 normalWS)
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
                return inputData;
            }

            half4 PhysicallyShade(InputData inputData, half3 albedo, half metallic, half smoothness,
                                  half occlusion = 1.0h)
            {
                SurfaceData surfaceData;
                surfaceData.albedo = albedo;
                surfaceData.specular = half3(0, 0, 0);
                surfaceData.metallic = metallic;
                surfaceData.smoothness = smoothness;
                surfaceData.normalTS = half3(0, 0, 1);
                surfaceData.emission = half3(0, 0, 0);
                surfaceData.occlusion = occlusion;
                surfaceData.alpha = 1.0;
                surfaceData.clearCoatMask = 0.0;
                surfaceData.clearCoatSmoothness = 1.0;

                return UniversalFragmentPBR(inputData, surfaceData);
            }

            // ---- Lure plastic --------------------------------------------------------------
            //
            // Soft-plastic fishing lures are a coloured, translucent plastisol with glitter cast
            // into it. Three things make one read as that rather than as painted clay, and each
            // has a piece below:
            //   1. Thin parts (claws, tails) are lighter and more saturated than the thick body,
            //      because less plastic absorbs the light passing through - PlasticThickness.
            //   2. Light shines THROUGH the thin parts - the transmission term.
            //   3. The flakes sit at different depths, catch the light individually, and dim
            //      through the plastic above them - TraceFlakes + the per-flake lighting.

            // Written by PlasticThicknessPass (ScreenCavityFeature): eye depth of the nearest BACK
            // face under each pixel, 0 = none. Back minus this fragment's own eye depth is how much
            // plastic the view ray crosses.
            TEXTURE2D_FLOAT(_SculptPlasticBackDepth);
            float4 _SculptPlasticParams;   // x = buffer valid for this camera

            // Rotates the flake lattice off the model's axes, so rows of flakes never line up with
            // a symmetry plane or a primitive's edges. Orthonormal, so the inverse is the transpose.
            static const float3x3 FlakeGridRotation = float3x3(
                 0.36, 0.48, -0.80,
                -0.80, 0.60,  0.00,
                 0.48, 0.64,  0.60);

            float3 FlakeHash33(float3 p)
            {
                p = frac(p * float3(0.1031, 0.1030, 0.0973));
                p += dot(p, p.yxz + 33.33);
                return frac((p.xxy + p.yxx) * p.zyx);
            }

            struct FlakeHit
            {
                float depth;       // world distance below the surface, along the refracted ray
                half coverage;     // 0..1, antialiased
                half3 color;
                float3 normalWS;
            };

            // Glitter is a flat platelet (hex or square) suspended in the plastic, lying roughly
            // along the skin the way molding leaves it, with some random tilt. Each lattice cell
            // holds at most one, sized and placed so it stays inside its cell - which lets a short
            // march along the refracted view ray test only the cell it is standing in. The ray
            // hits the platelet's PLANE rather than the surface slicing through it, so a flake
            // shows its whole face at its true depth and slides with parallax as the model turns.
            half FlakeLodFade(float footprintInCells)
            {
                return saturate(2.0 - 2.5 * footprintInCells);
            }

            // What the flakes a layer's LOD fade has removed still contribute on average: their
            // colour, faintly. Mixed into the plastic so glitter too small to draw still tints it -
            // kept well under the flakes' real coverage, because a flat tint of a bright flake
            // colour over dark plastic reads far stronger than the same colour broken into specks.
            half3 FadedFlakeTint(half3 body, half3 flakeColor, float footprint, float cellSize, half density)
            {
                if (cellSize <= 1e-6)
                    return body;
                half gone = 1.0h - FlakeLodFade(footprint / cellSize);
                return lerp(body, flakeColor, gone * density * 0.12h);
            }

            FlakeHit TraceFlakes(float3 originOS, float3 dirOS, float3 normalOS, float3 objectScale, float footprint,
                                 float cellSize, half density, float seed, bool palette)
            {
                FlakeHit hit;
                hit.depth = 1e20;
                hit.coverage = 0;
                hit.color = half3(0, 0, 0);
                hit.normalWS = float3(0, 1, 0);
                if (density <= 0.001 || cellSize <= 1e-6)
                    return hit;

                float3 o = mul(FlakeGridRotation, originOS) / cellSize;
                float3 d = mul(FlakeGridRotation, dirOS);
                float3 n = mul(FlakeGridRotation, normalOS);
                float fp = max(footprint / cellSize, 1e-4);
                // Once a pixel spans more than about half a cell the individual flakes can't be
                // resolved, and what's left is the lattice beating against the pixel grid - a
                // visible checker/moire at a distance. Fade them out there, leaving the plastic.
                half lodFade = FlakeLodFade(fp);
                if (lodFade <= 0.0)
                    return hit;

                float bestT = 1e20;
                [unroll]
                for (int i = 0; i < 5; i++)
                {
                    float3 cell = floor(o + d * (0.4 * i + 0.05));
                    float3 h0 = FlakeHash33(cell + seed);
                    if (h0.x > density) continue;

                    float radius = lerp(0.22, 0.45, h0.y);
                    float3 h1 = FlakeHash33(cell + seed + 19.19);
                    float3 centre = cell + radius + h1 * (1.0 - 2.0 * radius);
                    float3 h2 = FlakeHash33(cell + seed + 47.47);
                    float3 nf = normalize(n + (h2 * 2.0 - 1.0) * _FlakeTilt);

                    float denom = dot(d, nf);
                    if (abs(denom) < 0.05) continue;
                    float t = dot(centre - o, nf) / denom;
                    if (t < 0.0 || t >= bestT) continue;

                    float3 q = o + d * t - centre;
                    float3 ta = normalize(cross(nf, abs(nf.y) < 0.95 ? float3(0, 1, 0) : float3(1, 0, 0)));
                    float3 tb = cross(nf, ta);
                    float s, c;
                    sincos(FlakeHash33(cell + seed + 83.83).x * 6.2831853, s, c);
                    float2 uv = float2(dot(q, ta), dot(q, tb));
                    float2 a = abs(float2(uv.x * c - uv.y * s, uv.x * s + uv.y * c));
                    float hexDist = max(a.x * 0.8660254 + a.y * 0.5, a.y);
                    float squareDist = max(a.x, a.y);
                    float dist = lerp(hexDist, squareDist, _FlakeShape);

                    // Edge antialiasing over one pixel's footprint, and a flake smaller than a
                    // pixel faded to roughly its share of it rather than drawn as a full speck -
                    // otherwise zooming out turns the glitter into crawling noise.
                    half coverage = saturate((radius - dist) / fp + 0.5) * saturate(2.0 * radius / fp) * lodFade;
                    if (coverage <= 0.01) continue;

                    bestT = t;
                    hit.depth = t * cellSize;
                    hit.coverage = coverage;
                    // The lattice lives in scaled object space, where only the rotation is left to undo.
                    hit.normalWS = TransformObjectToWorldDir(mul(nf, FlakeGridRotation) / objectScale);
                    if (palette)
                    {
                        // Colour weights ride in the alpha channels.
                        half total = max(_FlakeColorA.a + _FlakeColorB.a + _FlakeColorC.a, 1e-4);
                        half pick = h0.z * total;
                        hit.color = pick < _FlakeColorA.a ? _FlakeColorA.rgb
                                  : pick < _FlakeColorA.a + _FlakeColorB.a ? _FlakeColorB.rgb
                                  : _FlakeColorC.rgb;
                    }
                    else
                    {
                        hit.color = _MicroFlakeColor.rgb;
                    }
                }
                return hit;
            }

            struct PlasticLighting
            {
                half3 diffuseA;
                half3 glintA;
                half3 diffuseB;
                half3 glintB;
                half3 backLight;
                half3 scatter;
            };

            // A glitter platelet is nearly a mirror: a tight, normalised Blinn-Phong lobe, so each
            // flake is either catching a light or not - that on/off is the sparkle as the view turns.
            half FlakeGlint(float3 nf, float3 L, float3 V)
            {
                float3 h = normalize(L + V);
                return (half)(pow(saturate(dot(nf, h)), 160.0) * 8.0);
            }

            void AccumulatePlasticLight(Light light, float3 N, float3 V, float3 nA, float3 nB,
                                        inout PlasticLighting acc)
            {
                half3 radiance = light.color * light.distanceAttenuation;
                half3 shadowed = radiance * light.shadowAttenuation;
                float3 L = light.direction;
                acc.diffuseA += shadowed * saturate(dot(nA, L));
                acc.glintA   += shadowed * FlakeGlint(nA, L, V);
                acc.diffuseB += shadowed * saturate(dot(nB, L));
                // Pepper flakes are specks, not mirrors - a full-strength glint on each turns the
                // whole surface into white noise.
                acc.glintB   += shadowed * FlakeGlint(nB, L, V) * 0.35;
                // Light entering the far side and leaving toward the eye. Deliberately unshadowed:
                // the shadow map reports this point as in the model's own shadow, and that is
                // exactly the light that is passing through it.
                float3 through = normalize(L + N * 0.35);
                acc.backLight += radiance * pow(saturate(dot(V, -through)), 3.0);
                // Light scattered inside the plastic reaches past the shadow line: the extra a
                // wrapped Lambert gets over the plain one. This is what makes the whole body read
                // as semi-translucent rather than painted, even where it's too thick to see into.
                half ndl = dot(N, L);
                half wrapped = saturate((ndl + 0.6) / 1.6);
                acc.scatter += radiance * lerp(1.0h, light.shadowAttenuation, 0.5h) * max(wrapped - saturate(ndl), 0.0h);
            }

            // How much plastic the view ray crosses at this pixel, in world units.
            float PlasticThickness(Varyings input, float depthScale)
            {
                // No buffer for this camera (or nothing behind): treat it as solid body.
                float thickness = depthScale * 4.0;
                if (_SculptPlasticParams.x > 0.5)
                {
                    float back = LOAD_TEXTURE2D(_SculptPlasticBackDepth, uint2(input.positionCS.xy)).r;
                    float front = -TransformWorldToView(input.positionWS).z;
                    // A back face IN FRONT of this surface means an open or intersecting mesh -
                    // the difference means nothing there, so keep the solid fallback.
                    if (back > 0.0 && back >= front - depthScale * 0.02)
                        thickness = max(back - front, 0.0);
                }
                return thickness;
            }

            half3 FlakeRadiance(InputData inputData, half3 color, float3 n, half3 diffuse, half3 glint)
            {
                float3 V = inputData.viewDirectionWS;
                half3 env = GlossyEnvironmentReflection(reflect(-V, n), inputData.positionWS, 0.3h, 1.0h,
                                                        inputData.normalizedScreenSpaceUV);
                return color * (diffuse + SampleSH(n)) * 0.7
                     + color * env * 0.6
                     + lerp(color, half3(1, 1, 1), 0.3) * glint * _FlakeSparkle;
            }

            // Colour left after light crosses `distance` of plastic twice (down to a flake and back
            // up). The thick colour is what the plastic absorbs down to over one absorption depth.
            half3 PlasticTransmittance(float distance, float depthScale)
            {
                return pow(max(_PlasticThickColor.rgb, 1e-3), 2.0 * distance / depthScale);
            }

            // Lengths of the model matrix's columns: the object's scale along each of its own axes.
            float3 ObjectAxisScale()
            {
                return float3(
                    length(float3(UNITY_MATRIX_M[0].x, UNITY_MATRIX_M[1].x, UNITY_MATRIX_M[2].x)),
                    length(float3(UNITY_MATRIX_M[0].y, UNITY_MATRIX_M[1].y, UNITY_MATRIX_M[2].y)),
                    length(float3(UNITY_MATRIX_M[0].z, UNITY_MATRIX_M[1].z, UNITY_MATRIX_M[2].z)));
            }

            float3 FaceViewer(float3 n, float3 V)
            {
                return dot(n, V) < 0.0 ? -n : n;
            }

            half4 PlasticShade(Varyings input, float3 normalWS, float3 smoothNormalWS, float3 positionOS)
            {
                InputData inputData = BuildInputData(input, normalWS);
                float3 V = inputData.viewDirectionWS;
                float depthScale = max(_PlasticDepth, 1e-5);

                float thickness = PlasticThickness(input, depthScale);
                half throughFraction = exp(-thickness / depthScale);
                half3 body = lerp(_PlasticThickColor.rgb, _PlasticThinColor.rgb, throughFraction);

                // Flakes live in the object's own space scaled to world size, so they stay put on
                // the model as it moves and keep one size across differently scaled objects. The
                // smooth normal steers them even under Flat Shading, so a flake doesn't break
                // along triangle edges.
                // Per-axis scale, so a squashed object gets round flakes of the same size rather
                // than squashed or shrunken ones.
                float3 objectScale = ObjectAxisScale();
                float3 originOS = positionOS * objectScale;
                float3 dirOS = normalize(TransformWorldToObjectDir(refract(-V, smoothNormalWS, 1.0 / 1.5), false) * objectScale);
                float3 surfaceOS = normalize(TransformWorldToObjectNormal(smoothNormalWS, false) / objectScale);
                float footprint = length(fwidth(originOS));
                FlakeHit big = TraceFlakes(originOS, dirOS, surfaceOS, objectScale, footprint,
                                           _FlakeCellSize, _FlakeDensity, 0.0, true);
                FlakeHit micro = TraceFlakes(originOS, dirOS, surfaceOS, objectScale, footprint,
                                             _MicroFlakeCellSize, _MicroFlakeDensity, 131.0, false);
                half3 paletteMean = (_FlakeColorA.rgb * _FlakeColorA.a + _FlakeColorB.rgb * _FlakeColorB.a
                                   + _FlakeColorC.rgb * _FlakeColorC.a)
                                  / max(_FlakeColorA.a + _FlakeColorB.a + _FlakeColorC.a, 1e-4h);
                body = FadedFlakeTint(body, paletteMean, footprint, _FlakeCellSize, _FlakeDensity);
                body = FadedFlakeTint(body, _MicroFlakeColor.rgb, footprint, _MicroFlakeCellSize, _MicroFlakeDensity);
                float3 nBig = FaceViewer(big.normalWS, V);
                float3 nMicro = FaceViewer(micro.normalWS, V);

                PlasticLighting acc = (PlasticLighting)0;
                Light mainLight = GetMainLight(inputData.shadowCoord, inputData.positionWS, inputData.shadowMask);
                AccumulatePlasticLight(mainLight, normalWS, V, nBig, nMicro, acc);
                #if defined(_ADDITIONAL_LIGHTS)
                uint pixelLightCount = GetAdditionalLightsCount();
                #if USE_CLUSTER_LIGHT_LOOP
                [loop] for (uint lightIndex = 0; lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); lightIndex++)
                {
                    CLUSTER_LIGHT_LOOP_SUBTRACTIVE_LIGHT_CHECK
                    Light light = GetAdditionalLight(lightIndex, inputData.positionWS, inputData.shadowMask);
                    AccumulatePlasticLight(light, normalWS, V, nBig, nMicro, acc);
                }
                #endif
                LIGHT_LOOP_BEGIN(pixelLightCount)
                    Light light = GetAdditionalLight(lightIndex, inputData.positionWS, inputData.shadowMask);
                    AccumulatePlasticLight(light, normalWS, V, nBig, nMicro, acc);
                LIGHT_LOOP_END
                #endif

                // Front-to-back: whichever flake is shallower covers the other.
                half wBig = big.coverage;
                half wMicro = micro.coverage;
                if (micro.depth < big.depth) wBig *= 1.0 - wMicro;
                else                          wMicro *= 1.0 - wBig;
                half3 tBig = PlasticTransmittance(big.depth, depthScale);
                half3 tMicro = PlasticTransmittance(micro.depth, depthScale);

                half3 flakes = wBig * tBig * FlakeRadiance(inputData, big.color, nBig, acc.diffuseA, acc.glintA)
                             + wMicro * tMicro * FlakeRadiance(inputData, micro.color, nMicro, acc.diffuseB, acc.glintB);
                // A flake blocks the plastic behind it, by as much of it as shows through.
                half occluded = saturate(wBig * Luminance(tBig) + wMicro * Luminance(tMicro));

                half4 color = PhysicallyShade(inputData, body * (1.0 - occluded), 0.0, _PlasticGloss);
                half3 glow = _PlasticThinColor.rgb * throughFraction
                           * (acc.backLight + SampleSH(-normalWS) * 0.6) * _PlasticTransmission;
                // Scattered light leaves tinted by the plastic it wandered through - between the
                // surface colour and the thin colour, nearer the surface colour so a thick body keeps its depth.
                half3 scatter = acc.scatter * lerp(body, _PlasticThinColor.rgb, 0.3h) * _PlasticTransmission * 0.5h;
                color.rgb += flakes + (glow + scatter) * (1.0 - occluded);
                return color;
            }

            // ---- Aged metal ----------------------------------------------------------------
            //
            // Antiqued, rusted, verdigris and washed finishes on cast or painted sculpture are all
            // the same three layers, and what sells them is WHERE each layer ends up:
            //   1. Bare metal, polished bright on the raised edges that get handled or dry-brushed.
            //   2. A coat (rust, verdigris, paint, black oxide) in broad patches, thickest in recesses.
            //   3. A dark wash pooled in the deepest recesses.

            // -1 (deep recess) .. +1 (sharp ridge), from the mesh's own curvature: SculptableMesh
            // writes a soft-saturated, one-ring-smoothed mean curvature into vertex colour .b
            // (0.5 = the model's average, higher = more concave). Measured on the geometry rather
            // than on screen, so the wash stays exactly where it is as the camera orbits or zooms -
            // as real paint does - and follows the sculpt live as strokes recompute it.
            // `detail` rescales it before re-saturating: how much relief counts as a groove.
            half SurfaceConvexity(Varyings input, half detail)
            {
                half s = 1.0h - 2.0h * (half)input.color.b;
                // Undo the soft saturation (s = x / (1 + |x|)), scale, and apply it again.
                half x = s / max(1.0h - abs(s), 0.02h) * detail;
                return x / (1.0h + abs(x));
            }

            half MetalConvexity(Varyings input)
            {
                return SurfaceConvexity(input, _MetalDetail);
            }

            // Three octaves of value noise, stretched to roughly fill 0..1 - plain value-noise fBm
            // piles up around 0.5, which would give every patch the same soft edge.
            float MetalFbm(float3 p)
            {
                float sum = ValueNoise3D(p) * 0.5;
                sum += ValueNoise3D(p * 2.03 + 17.1) * 0.25;
                sum += ValueNoise3D(p * 4.11 + 31.7) * 0.125;
                return saturate((sum / 0.875 - 0.5) * 1.8 + 0.5);
            }

            half4 MetalShade(Varyings input, float3 normalWS, float3 positionOS)
            {
                // Pattern in the object's own space at world size, like the lure flakes: it stays on
                // the model as it moves and keeps one size across differently scaled objects.
                float3 p = positionOS * ObjectAxisScale();
                float footprint = length(fwidth(p));

                half cv = MetalConvexity(input);
                // The offset slides the model through the noise, which moves every blotch at once.
                float3 patchP = p / max(_MetalPatchSize, 1e-5) + _MetalPatternOffset.xyz;
                half patches = (half)MetalFbm(patchP);
                half tint = (half)MetalFbm(patchP * 2.7 + 41.7);
                // Grain finer than a pixel is only shimmer - fade it out as it gets there.
                float grainSize = max(_MetalGrainSize, 1e-6);
                half grainLod = saturate(1.5h - (half)(footprint / grainSize));
                half grain = (half)ValueNoise3D(p / grainSize + 7.3);

                // Worn to bare metal along the ridges, broken up by the patches so it isn't a
                // perfect outline around every edge.
                half wear = saturate(smoothstep(0.1h, 0.55h, cv) * _MetalEdgeWear * (0.55h + 0.9h * patches));
                // The coat covers wherever the patch noise sits under the coverage level, and
                // recesses hold more of it than raised areas. Patchiness and Follows Recesses set
                // the balance: rust is mostly patches, a blackened antique almost purely recesses.
                half coatLevel = _MetalCoverage - cv * _MetalCoatCurvature;
                half patchLevel = lerp(0.5h, patches, _MetalPatchAmount);
                half coat = saturate((coatLevel - patchLevel) * _MetalPatchContrast + 0.5h) * (1.0h - wear);
                half wash = smoothstep(0.03h, 0.6h, -cv) * _MetalWash;

                half3 coatColor = lerp(_CoatColorA.rgb, _CoatColorB.rgb,
                                       saturate((tint - 0.5h) * 2.5h + 0.5h - cv * 0.5h));
                coatColor *= lerp(1.0h, lerp(0.7h, 1.2h, grain), _MetalGrain * grainLod);
                half3 metalColor = _MetalColor.rgb * lerp(0.88h, 1.08h, tint);

                half3 albedo = lerp(metalColor, coatColor, coat);
                half metallic = lerp(_MetalMetallic, _CoatMetallic, coat);
                half smoothness = lerp(_MetalSmoothness, _CoatSmoothness, coat);
                albedo = lerp(albedo, _WashColor.rgb, wash);
                metallic *= 1.0h - wash;
                smoothness *= 1.0h - 0.35h * wash;

                // Pitted where coated, much less on the polished metal.
                half bump = _MetalGrain * grainLod * lerp(0.25h, 1.0h, coat);
                if (bump > 0.001h)
                    normalWS = PerturbNormalAt(normalWS, p / grainSize, bump * 0.6h);

                return PhysicallyShade(BuildInputData(input, normalWS), albedo, metallic, smoothness,
                                       1.0h - 0.5h * wash);
            }

            // ---- Sculptor's clay -----------------------------------------------------------
            //
            // Oil-based modelling clay (Chavant, Monster Clay, plasteline) under studio light
            // reads as clay rather than grey plastic through four things:
            //   1. A satin sheen - broad and soft, the waxy binder - from the ordinary PBR lobe.
            //   2. A thin wet highlight riding on top of it: the greasy film that makes worked clay
            //      glint along every tool stroke. Its own sharp lobe, lit on the grain-bumped normal
            //      so it breaks up the way a worked surface does instead of sitting there as a dot.
            //   3. Light bleeding a little past the shadow line, tinted - strongly for terracotta,
            //      barely at all for grey - the wrapped-Lambert excess, as in the lure plastic.
            //   4. Recesses darker AND richer in colour than the flats; raised forms a touch lighter
            //      and glossier where tools and fingers burnish them.

            struct ClayLighting
            {
                half3 wet;
                half3 scatter;
            };

            void AccumulateClayLight(Light light, float3 N, float3 V, inout ClayLighting acc)
            {
                half3 radiance = light.color * light.distanceAttenuation;
                float3 L = light.direction;
                half ndl = dot(N, L);
                // Normalised Blinn-Phong, Schlick fresnel from a dielectric's 4%: sharp enough to
                // read as a film of wet, energy-kept so the sharpness dial doesn't change brightness.
                float3 H = normalize(L + V);
                float spec = pow(saturate(dot(N, H)), _ClayWetPower) * (_ClayWetPower + 8.0) / 8.0;
                half fresnel = 0.04h + 0.96h * pow(1.0h - saturate(dot(V, H)), 5.0h);
                acc.wet += radiance * light.shadowAttenuation * ((half)spec * fresnel * saturate(ndl));
                // Scattered light gets a little past the terminator and softens self-shadowing;
                // half-shadowed only, like the plastic's, since it's light travelling inside.
                half wrapped = saturate((ndl + 0.5h) / 1.5h);
                acc.scatter += radiance * lerp(1.0h, light.shadowAttenuation, 0.6h) * max(wrapped - saturate(ndl), 0.0h);
            }

            half4 ClayShade(Varyings input, float3 normalWS, float3 positionOS)
            {
                // Object space at world size, like the metal's pattern: stays on the model, one
                // size across differently scaled objects.
                float3 p = positionOS * ObjectAxisScale();
                float footprint = length(fwidth(p));
                half cv = SurfaceConvexity(input, _ClayDetail);

                // Clay is never one flat colour - batches mix unevenly and worked areas smear
                // together - so a broad, faint drift in tone.
                half mottle = (half)MetalFbm(p / max(_ClayMottleSize, 1e-5));
                half3 albedo = _ClayColor.rgb * (1.0h + (mottle - 0.5h) * 2.0h * _ClayMottle);

                half recess = saturate(smoothstep(0.0h, 0.45h, -cv) * _ClayRecess);
                half ridge = smoothstep(0.1h, 0.6h, cv) * _ClayRidge;
                albedo = lerp(albedo, _ClayRecessColor.rgb, recess);
                albedo *= 1.0h + 0.25h * ridge;
                half smoothness = saturate(_ClaySmoothness + 0.15h * ridge - 0.2h * recess);

                // Fine grit in the clay body, faded out before it gets smaller than a pixel.
                float grainSize = max(_ClayGrainSize, 1e-6);
                half grain = _ClayGrain * saturate(1.5h - (half)(footprint / grainSize));
                if (grain > 0.001h)
                {
                    normalWS = PerturbNormalAt(normalWS, p / grainSize, grain * 0.12h);
                    half speck = (half)ValueNoise3D(p / grainSize * 1.7 + 11.3);
                    albedo *= lerp(1.0h, lerp(0.9h, 1.06h, speck), saturate(grain));
                }

                InputData inputData = BuildInputData(input, normalWS);
                half4 color = PhysicallyShade(inputData, albedo, 0.0h, smoothness, 1.0h - 0.6h * recess);

                float3 V = inputData.viewDirectionWS;
                ClayLighting acc = (ClayLighting)0;
                Light mainLight = GetMainLight(inputData.shadowCoord, inputData.positionWS, inputData.shadowMask);
                AccumulateClayLight(mainLight, normalWS, V, acc);
                #if defined(_ADDITIONAL_LIGHTS)
                uint pixelLightCount = GetAdditionalLightsCount();
                #if USE_CLUSTER_LIGHT_LOOP
                [loop] for (uint lightIndex = 0; lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); lightIndex++)
                {
                    CLUSTER_LIGHT_LOOP_SUBTRACTIVE_LIGHT_CHECK
                    Light light = GetAdditionalLight(lightIndex, inputData.positionWS, inputData.shadowMask);
                    AccumulateClayLight(light, normalWS, V, acc);
                }
                #endif
                LIGHT_LOOP_BEGIN(pixelLightCount)
                    Light light = GetAdditionalLight(lightIndex, inputData.positionWS, inputData.shadowMask);
                    AccumulateClayLight(light, normalWS, V, acc);
                LIGHT_LOOP_END
                #endif

                // The wet film thins out in the recesses - that's where the tools didn't drag it.
                color.rgb += acc.wet * _ClayWetness * (1.0h - 0.7h * recess)
                           + acc.scatter * _ClayScatterColor.rgb * _ClaySubsurface;
                // Recesses see less of the lights as well as less sky: occlusion above only
                // reaches the ambient term, which leaves a crease under a key light looking flat.
                color.rgb *= 1.0h - 0.35h * recess;
                return color;
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
                else if (_PlasticEnabled > 0.5)
                    litColor = PlasticShade(input, normalWS, normalize(input.normalWS), positionOS);
                else if (_MetalEnabled > 0.5)
                    litColor = MetalShade(input, normalWS, positionOS);
                else if (_ClayEnabled > 0.5)
                    litColor = ClayShade(input, normalWS, positionOS);
                else
                    litColor = PhysicallyShade(BuildInputData(input, normalWS), _BaseColor.rgb, _Metallic, _Smoothness);

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

        // Feeds PlasticThicknessPass: eye depth of the nearest BACK face, so the lure plastic can
        // tell a thin claw from a thick body by how much plastic the view ray crosses. Only drawn
        // while a lure plastic is showing.
        Pass
        {
            Name "SculptPlasticBackDepth"
            Tags { "LightMode" = "SculptPlasticBackDepth" }
            Cull Front
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex BackDepthVertex
            #pragma fragment BackDepthFragment
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings BackDepthVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = posInputs.positionCS;
                output.positionWS = posInputs.positionWS;
                return output;
            }

            float BackDepthFragment(Varyings input) : SV_Target
            {
                return -TransformWorldToView(input.positionWS).z;
            }
            ENDHLSL
        }

        UsePass "Universal Render Pipeline/Lit/SHADOWCASTER"
        UsePass "Universal Render Pipeline/Lit/DEPTHONLY"
        UsePass "Universal Render Pipeline/Lit/DEPTHNORMALS"
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
