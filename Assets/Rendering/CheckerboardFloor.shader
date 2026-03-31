Shader "RenderingSandbox/CheckerboardFloor"
{
    Properties
    {
        _LightColor("Light Color", Color) = (0.82, 0.82, 0.82, 1)
        _DarkColor("Dark Color", Color) = (0.28, 0.28, 0.28, 1)
        _CheckerScale("Checker Scale", Float) = 24
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        Cull Back
        ZWrite On

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            float4 _LightColor;
            float4 _DarkColor;
            float _CheckerScale;

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                // Procedural checkerboards split UV space into square cells. Multiplying the
                // UVs by _CheckerScale increases how many cells fit across the surface.
                float2 scaledUv = input.uv * _CheckerScale;
                float2 checkerCell = floor(scaledUv);

                // Adding the cell coordinates and taking modulo 2 alternates between even and
                // odd cells, which gives the familiar checker pattern without using a texture.
                float checkerIndex = fmod(checkerCell.x + checkerCell.y, 2.0);
                float4 checkerColor = checkerIndex < 1.0 ? _LightColor : _DarkColor;
                return checkerColor;
            }
            ENDHLSL
        }
    }
}
