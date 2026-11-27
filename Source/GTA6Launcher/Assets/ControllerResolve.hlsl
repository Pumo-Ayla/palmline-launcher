// Each destination pixel gathers nine positions from the higher-resolution
// model surface. WPF supplies premultiplied color, including the silhouette.
sampler2D model : register(s0);
float2 outputPixel : register(c0);

float4 main(float2 uv : TEXCOORD0) : COLOR
{
    // A compact tent softens thin diagonal strokes as the viewpoint moves.
    // The centre sample has the most weight to preserve the button lettering.
    float2 stepUV = outputPixel * 0.55;
    float4 sum = 0;
    [unroll] for (int y = -1; y <= 1; ++y)
        [unroll] for (int x = -1; x <= 1; ++x)
            sum += tex2D(model, uv + float2(x,y) * stepUV) * (2-abs(x)) * (2-abs(y));
    return sum / 16.0;
}
