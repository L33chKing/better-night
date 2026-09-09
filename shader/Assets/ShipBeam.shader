Shader "Hidden/RealisticNight/ShipBeam"
{
    // Soft volumetric searchlight beam. Pass 1: shell (axial fade x edge fade x head-on boost).
    // Pass 2: interior fill on back faces, visible only to a camera inside the volume (hollow no more).
    Properties
    {
        _MainTex ("Beam", 2D) = "white" {}
        _Color ("Color (HDR)", Color) = (0.7, 0.8, 1, 1)
        _EdgePow ("Edge Softness", Float) = 2.0
        _AxialBoost ("Head-On Boost", Float) = 3.0
        _AxialPow ("Head-On Tightness", Float) = 6.0
        _ConeLen ("Cone Length (m)", Float) = 340.0
        _ConeBaseR ("Cone End Radius (m)", Float) = 26.0
        _ConeApexR ("Cone Start Radius (m)", Float) = 1.0
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Blend One OneMinusSrcAlpha        // premultiplied soft glow; HDR colour -> bloom
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "ShipBeamCore.cginc"

            half4 frag(v2f i) : SV_Target
            {
                half axial = tex2D(_MainTex, i.uv).a; // slow falloff + streaks (ship beam texture)
                float3 V = _WorldSpaceCameraPos.xyz - i.wpos;
                float vlen = max(length(V), 1e-3);
                float ndv = abs(dot(normalize(i.wnormal), V / vlen));
                half edge = pow(ndv, max(0.5, _EdgePow)); // grazing silhouette -> 0 (no hard rim, any angle)
                float downBarrel = abs(dot(V / vlen, normalize(i.bdir))); // 1 = sight-line down the beam
                half boost = pow(downBarrel, max(1.0, _AxialPow)); // air column integrates: bright head-on
                half4 c = half4(_Color.rgb, _Color.a) * i.color;
                half a = axial * edge * (1.0 + _AxialBoost * boost) * c.a;
                return half4(c.rgb * a, a);   // premultiplied
            }
            ENDCG
        }
        Pass // interior volume: back faces only, lit solely by the inside factor (invisible from outside)
        {
            Name "INTERIOR"
            Blend One OneMinusSrcAlpha
            ZWrite Off
            Cull Front
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragInterior
            #include "ShipBeamCore.cginc"

            half4 fragInterior(v2f i) : SV_Target
            {
                float3 look = normalize(i.wpos - _WorldSpaceCameraPos.xyz); // camera -> pixel
                float lookStart = dot(look, normalize(i.bdir)) * -0.5 + 0.5; // 1 = staring at the lens
                half axialProf = pow(1.0 - i.beam.y, 1.2); // air density along the throw (matches beam texture)
                half glow = i.beam.x * axialProf * (0.45 + 0.55 * lookStart);
                half a = glow * _Color.a;
                return half4(_Color.rgb * a, a); // premultiplied interior fill
            }
            ENDCG
        }
    }
    Fallback Off
}
