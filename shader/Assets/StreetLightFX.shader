Shader "Hidden/RealisticNight/StreetLightFX"
{
    // Per-lamp LIGHT VOLUMES for URP 2022.3. Each lamp draws its pool as a camera-facing quad (procedural,
    // instanced from _LampsBuf); the fragment reconstructs the real surface from the depth buffer and adds
    // that lamp's att*N.L light into a full-res light buffer. A composite then blends the light onto the
    // scene with a "preserve" control: 0 = additive (floods the surface with the lamp colour), 1 =
    // multiplicative (brightens each surface while keeping its OWN colour). No downsample -> no flicker;
    // far lamps ~free (footprint-bound), so the whole map lights cheaply.
    // FLOATING ORIGIN: lamp positions are RAW GLOBAL; the reconstructed ground has _RN_OriginPos subtracted
    // so both are in raw-global (origin-invariant) space and the pools never desync on a recenter.
    // SELF-CONTAINED (no Blit.hlsl -> TEXTURE2D_X fails). See reference-custom-shader-guide.
    Properties
    {
        _Intensity ("Intensity", Float) = 3
        _Range ("Range (m)", Float) = 30
        _FalloffK ("Falloff K", Float) = 0.02
        _NdotL ("Surface Shading", Float) = 0.7
        _Color ("Light Color", Color) = (1.0, 0.8, 0.45, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        ZWrite Off ZTest Always Cull Off

        HLSLINCLUDE
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"

            TEXTURE2D(_BlitTexture);
            float4 _BlitScaleBias;
            TEXTURE2D(_CameraDepthTexture);
            TEXTURE2D(_VolLight);        // full-res volume light (composite input)

            float4 _ZBufferParams;
            float4x4 _RN_InvVP;             // inverse(GPUProj * view), current frame
            float4 _RN_OriginPos;          // Datum.origin world pos -> reconstructed ground - this = RAW GLOBAL
            float4x4 _RN_VP;               // GPUProj * view (forward), volume vertex transform
            float4 _RN_CamRight, _RN_CamUp; // camera basis in world (billboard the volume quad)
            float4 _RN_CamPos;             // camera world position (decide fullscreen vs tight quad)
            float4 _ProjectionParams;      // .x = -1 when the projection is Y-flipped (screen-UV helper)
            float4 _ScreenParams;
            StructuredBuffer<float4> _LampsBuf; // xyz = RAW GLOBAL pos, w = warm(0=LED,1=sodium). No cbuffer cap.
            int _LampCount;
            float _Intensity, _Range, _FalloffK, _NdotL;
            float _IntensityWarm; // separate ground brightness for YELLOW/sodium lamps (white reads brighter)
            float _AtmosRange;    // haze: ground pools fade with camera->lamp distance (shared with the orbs)
            float4 _Color;      // LED white (city roads)
            float4 _ColorWarm;  // sodium yellow (main + rural roads); per-lamp blend via _LampsBuf[i].w
            float _Preserve;    // 0 = additive (floods colour), 1 = multiplicative (keeps surface colour)

            // Fullscreen triangle (for the composite pass).
            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 texcoord : TEXCOORD0; };
            Varyings Vert(Attributes input)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                float2 uv = GetFullScreenTriangleTexCoord(input.vertexID);
                o.texcoord = uv * _BlitScaleBias.xy + _BlitScaleBias.zw;
                return o;
            }

            // Classic ComputeScreenPos: screen UV for sampling _CameraDepthTexture (Y flip via _ProjectionParams.x).
            float4 ScreenPos(float4 pos)
            {
                float4 o = pos * 0.5;
                o.xy = float2(o.x, o.y * _ProjectionParams.x) + o.w;
                o.zw = pos.zw;
                return o;
            }
        ENDHLSL

        // Pass 0 — LIGHT VOLUMES -> full-res light buffer (additive; overlapping pools accumulate). Drawn
        // procedurally as 6 verts x N lamp instances; each is a camera-facing quad sized to the light range.
        Pass
        {
            Name "RN_StreetLight_Volume"
            Blend One One
            HLSLPROGRAM
            #pragma vertex VertVol
            #pragma fragment FragVol
            #pragma target 4.5

            struct VolAttr { uint vid : SV_VertexID; uint iid : SV_InstanceID; };
            struct VolVary { float4 positionCS : SV_POSITION; float4 screenPos : TEXCOORD0; float3 headRaw : TEXCOORD1; float warm : TEXCOORD2; float fade : TEXCOORD3; };

            VolVary VertVol(VolAttr input)
            {
                VolVary o;
                float4 lamp = _LampsBuf[input.iid];
                float3 headRaw = lamp.xyz;                          // RAW GLOBAL lamp position
                float3 headUnity = headRaw + _RN_OriginPos.xyz;     // -> Unity world, only to place the quad
                float2 corners[6] = { float2(-1,-1), float2(1,-1), float2(1,1), float2(-1,-1), float2(1,1), float2(-1,1) };
                float2 c = corners[input.vid];
                // If the camera is within the lamp's reach, its pool can wrap around/behind us -> a camera-
                // facing quad AT the lamp would clip away when the lamp goes off-screen and the light would
                // vanish (the bug). In that case cover the WHOLE screen (the fragment attenuates per-pixel).
                // Distant lamps keep the tight quad (cheap, footprint-bound).
                if (distance(headUnity, _RN_CamPos.xyz) < _Range * 1.2)
                {
                    o.positionCS = float4(c, 0.5, 1.0);             // fullscreen clip-space quad
                }
                else
                {
                    float3 wpos = headUnity + (_RN_CamRight.xyz * c.x + _RN_CamUp.xyz * c.y) * (_Range * 1.15);
                    o.positionCS = mul(_RN_VP, float4(wpos, 1.0));
                }
                o.screenPos = ScreenPos(o.positionCS);
                o.headRaw = headRaw;
                o.warm = lamp.w;
                o.fade = exp(-distance(headUnity, _RN_CamPos.xyz) / max(1.0, _AtmosRange)); // atmospheric haze
                return o;
            }

            half4 FragVol(VolVary i) : SV_Target
            {
                float2 uv = i.screenPos.xy / i.screenPos.w;
                float raw = SAMPLE_TEXTURE2D(_CameraDepthTexture, sampler_PointClamp, uv).r;
                if (Linear01Depth(raw, _ZBufferParams) > 0.999) return half4(0, 0, 0, 0); // sky -> no light
                float3 surf = ComputeWorldSpacePosition(uv, raw, _RN_InvVP) - _RN_OriginPos.xyz; // raw global
                float3 N = normalize(cross(ddy(surf), ddx(surf)));  // full-res -> stable

                float3 L = i.headRaw - surf;
                float d2 = dot(L, L);
                float d = sqrt(d2);
                float invR = 1.0 / max(1.0, _Range);
                float win = saturate(1.0 - d * invR);
                float att = (win * win) / (1.0 + d2 * _FalloffK);
                float ndl = lerp(1.0, saturate(dot(N, L / max(d, 1e-3))), saturate(_NdotL));
                float warm = saturate(i.warm);
                float3 col = lerp(_Color.rgb, _ColorWarm.rgb, warm);
                float intensity = lerp(_Intensity, _IntensityWarm, warm); // separate white vs yellow brightness
                return half4(att * ndl * col * intensity * i.fade, 1.0);
            }
            ENDHLSL
        }

        // Pass 1 — COMPOSITE the light onto the scene. _BlitTexture = scene copy, _VolLight = the light.
        // preserve 0 -> scene + light (additive; at low Surface Shading this washes surfaces toward the lamp
        // colour). preserve 1 -> scene + light*scene (multiplicative; brightens while KEEPING each surface's
        // own colour, so asset colours aren't overwritten even at high Intensity).
        Pass
        {
            Name "RN_StreetLight_Composite"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragComposite
            #pragma target 4.5

            half4 FragComposite(Varyings input) : SV_Target
            {
                float3 scene = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.texcoord).rgb;
                float3 light = SAMPLE_TEXTURE2D(_VolLight, sampler_LinearClamp, input.texcoord).rgb;
                float3 mixv = lerp(float3(1.0, 1.0, 1.0), scene, saturate(_Preserve));
                return half4(scene + light * mixv, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
