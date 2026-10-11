// Qing Island's own shader implementation. Architectural references and
// upstream license notices: ../third-party/GLASS-SOURCES.md.
cbuffer Settings : register(b0) {
    float4 region;       // padded backdrop area, physical screen coordinates
    float4 outputRect;   // selected DXGI output, virtual desktop coordinates
    float4 panel;        // capsule rectangle, physical screen coordinates
    float4 config;       // corner radius, scale, Gaussian sigma, liquid flag
    float4 tint;         // RGB, tint amount
    float4 mouse;        // local pointer X/Y, present flag, DXGI rotation
    float4 viewport;     // target texture dimensions
};
Texture2D scene : register(t0);
Texture2D blurred : register(t1);
Texture2D ink : register(t2);
SamplerState linearClamp : register(s0);
RWBuffer<uint> changeFlag : register(u0);

float4 Vertex(uint id : SV_VertexID) : SV_Position {
    float2 p = float2((id << 1) & 2, id & 2);
    return float4(p * float2(2, -2) + float2(-1, 1), 0, 1);
}
float2 desktopUV(float2 screen) {
    float2 uv = (screen - outputRect.xy) / outputRect.zw;
    // Desktop Duplication textures are unrotated, unlike screen coordinates.
    if (mouse.w == 2) return float2(uv.y, 1 - uv.x);
    if (mouse.w == 3) return 1 - uv;
    if (mouse.w == 4) return float2(1 - uv.y, uv.x);
    return uv;
}
[numthreads(16, 16, 1)]
void Compare(uint3 id : SV_DispatchThreadID) {
    if (id.x >= region.z || id.y >= region.w) return;
    float2 uv = desktopUV(region.xy + id.xy + 0.5);
    float3 oldColor = scene.SampleLevel(linearClamp, uv, 0).rgb;
    float3 newColor = blurred.SampleLevel(linearClamp, uv, 0).rgb;
    if (any(abs(oldColor - newColor) > 0.001)) InterlockedOr(changeFlag[0], 1);
}
static const float weights[5] = {0.204164, 0.180174, 0.123832, 0.066282, 0.027630};
float4 Horizontal(float4 position : SV_Position) : SV_Target {
    float2 screen = region.xy + position.xy / viewport.xy * region.zw;
    float3 c = scene.SampleLevel(linearClamp, desktopUV(screen), 0).rgb * weights[0];
    [unroll] for (int i = 1; i <= 4; ++i) {
        float2 step = float2(i * config.z * 0.5, 0);
        c += (scene.SampleLevel(linearClamp, desktopUV(screen + step), 0).rgb
            + scene.SampleLevel(linearClamp, desktopUV(screen - step), 0).rgb) * weights[i];
    }
    return float4(c, 1);
}
float4 Vertical(float4 position : SV_Position) : SV_Target {
    float2 uv = position.xy / viewport.xy;
    float3 c = scene.SampleLevel(linearClamp, uv, 0).rgb * weights[0];
    [unroll] for (int i = 1; i <= 4; ++i) {
        float2 step = float2(0, i * config.z * 0.5 / region.w);
        c += (scene.SampleLevel(linearClamp, uv + step, 0).rgb
            + scene.SampleLevel(linearClamp, uv - step, 0).rgb) * weights[i];
    }
    return float4(c, 1);
}
float roundedDistance(float2 p, float2 halfSize, float radius) {
    float2 q = abs(p) - halfSize + radius;
    return length(max(q, 0)) + min(max(q.x, q.y), 0) - radius;
}
float3 glassSample(float2 local) {
    float2 screen = panel.xy + local;
    return blurred.SampleLevel(linearClamp, (screen - region.xy) / region.zw, 0).rgb;
}
float4 Glass(float4 position : SV_Position) : SV_Target {
    float2 local = position.xy;
    float liquid = config.w;
    float inset = liquid * 2 * config.y;
    float2 halfSize = panel.zw * 0.5 - inset;
    float2 p = local - panel.zw * 0.5;
    float radius = max(1, min(config.x - inset, min(halfSize.x, halfSize.y)));
    float sd = roundedDistance(p, halfSize, radius);
    float coverage = saturate(0.5 - sd);
    if (coverage == 0) return 0;
    float depth = max(0, -sd);
    float2 q = abs(p) - halfSize + radius;
    float2 n = max(q, 0);
    if (dot(n, n) < 0.0001) n = q.x > q.y ? float2(1, 0) : float2(0, 1);
    n = normalize(n) * sign(p + 0.00001);
    float bezel = min(radius, 13 * config.y);
    // A continuous lens band, zero displacement at both ends. Refraction
    // bends real pixels rather than merely drawing a coloured highlight.
    float t = saturate(depth / max(bezel, 1));
    float bump = sin(t * 3.14159265);
    float2 displacement = n * bump * bump * min(5 * config.y, bezel * 0.3) * liquid;
    float2 face = local - p * 0.012 * liquid + displacement;
    float3 c = glassSample(face);
    if (liquid > 0) {
        float2 aberration = displacement * 0.045;
        c.r = glassSample(face + aberration).r;
        c.b = glassSample(face - aberration).b;
    }
    float luma = dot(c, float3(0.2126, 0.7152, 0.0722));
    c = saturate(lerp(luma.xxx, c, liquid > 0 ? 1.07 : 1.12));
    c = lerp(c, tint.rgb, tint.a);
    float2 light = normalize(float2(-0.55, -0.84));
    if (mouse.z > 0) light = normalize(lerp(light, normalize(mouse.xy - panel.zw * 0.5 + 0.00001), 0.65));
    float rim = pow(1 - t, 2) * liquid;
    c = saturate(c + rim * (0.11 * max(0, dot(n, light)) - 0.035 * max(0, -dot(n, light))));
    float edge = 1 - smoothstep(0, 1.1 * config.y, depth);
    c = lerp(c, 1, edge * (liquid > 0 ? 0.28 : 0.13));
    float4 text = ink.SampleLevel(linearClamp, local / panel.zw, 0);
    // Adapt neutral glyphs to the local backdrop, preserving coloured status
    // accents. Neither the glyph texture nor its coverage passes through blur.
    float3 straight = text.rgb / max(text.a, 0.0001);
    if (text.a > 0.5 && max(straight.r, max(straight.g, straight.b))
            - min(straight.r, min(straight.g, straight.b)) < 0.22) {
        float darkInk = smoothstep(0.43, 0.60, dot(c, float3(0.2126, 0.7152, 0.0722)));
        text.rgb = lerp(0.97, 0.06, darkInk) * text.a;
    }
    c = text.rgb + c * (1 - text.a);
    return float4(c * coverage, coverage);
}
