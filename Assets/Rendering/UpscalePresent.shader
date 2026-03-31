Shader "Hidden/RenderingSandbox/UpscalePresent"
{
    Properties
    {
        _SourceTexture("Source Texture", 2D) = "white" {}
        _HistoryTexture("History Texture", 2D) = "black" {}
        _SharpenStrength("Sharpen Strength", Range(0, 1.5)) = 0
        _HistoryWeight("History Weight", Range(0, 0.98)) = 0.85
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"

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

            sampler2D _SourceTexture;
            sampler2D _HistoryTexture;

            float4 _SourceTexelSize;
            float _SharpenStrength;
            float _TemporalEnabled;
            float _HasHistory;
            float _HistoryWeight;

            Varyings Vert(Attributes input)
            {
                Varyings output;
                // The presentation mesh is a normal Unity quad, so its object-space vertices
                // must be transformed into clip space. Passing positionOS through directly only
                // covers a small rectangle in the center of the screen.
                output.positionCS = UnityObjectToClipPos(input.positionOS);
                output.uv = input.uv;
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;

                // Sampling is how the GPU reads colors from the low-res source texture.
                // Point sampling returns one texel, while bilinear sampling blends nearby texels.
                float4 center = tex2D(_SourceTexture, uv);

                float4 currentFrame = center;

                // Sharpening adds back local contrast after bilinear smoothing. It can make
                // edges feel more detailed, but too much sharpening also creates halos.
                if (_SharpenStrength > 0.0001)
                {
                    float2 texel = _SourceTexelSize.xy;
                    float4 left = tex2D(_SourceTexture, uv + float2(-texel.x, 0));
                    float4 right = tex2D(_SourceTexture, uv + float2(texel.x, 0));
                    float4 up = tex2D(_SourceTexture, uv + float2(0, texel.y));
                    float4 down = tex2D(_SourceTexture, uv + float2(0, -texel.y));

                    float4 blurred = (center + left + right + up + down) * 0.2;
                    currentFrame = saturate(center + (center - blurred) * _SharpenStrength);
                }

                if (_TemporalEnabled <= 0.5 || _HasHistory <= 0.5)
                {
                    return currentFrame;
                }

                // Temporal accumulation blends the current frame with history from earlier
                // frames. That can stabilize flicker when the camera is still, but without
                // motion-aware reprojection the old image no longer lines up during movement.
                float4 history = tex2D(_HistoryTexture, uv);
                return lerp(currentFrame, history, _HistoryWeight);
            }
            ENDHLSL
        }
    }
}
