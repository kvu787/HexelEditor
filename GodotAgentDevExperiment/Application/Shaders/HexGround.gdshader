shader_type spatial;
render_mode unshaded, cull_disabled, shadows_disabled, ambient_light_disabled, fog_disabled;

// The whole ground is one plane. Each pixel works out which pointy-top hex cell it lies in and reads that
// cell's color from a texture, so painting a cell is writing one texel. The formulas match
// Experiment.Core.HexGeometry: axial (q, r) from the plane's X and Z, cube rounding, then odd-r offset
// coordinates (column, row) to address the texture.
// source_color: the texels are sRGB colors as a person picked them, so the fetch decodes them to linear and
// the output encodes them back, and the cell shows the color that was painted.
uniform sampler2D cells : source_color, filter_nearest, repeat_disable;
uniform vec2 grid_size = vec2(48.0, 40.0);
uniform float cell_radius = 1.0;
uniform vec4 paper : source_color = vec4(0.86, 0.86, 0.86, 1.0);
uniform vec4 edge : source_color = vec4(0.70, 0.70, 0.70, 1.0);
uniform float edge_width = 0.04;

varying vec3 world_position;

void vertex() {
    world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

vec2 round_axial(vec2 fractional) {
    float q = fractional.x;
    float r = fractional.y;
    float s = -q - r;
    float rq = round(q);
    float rr = round(r);
    float rs = round(s);
    float dq = abs(rq - q);
    float dr = abs(rr - r);
    float ds = abs(rs - s);
    if (dq > dr && dq > ds) {
        rq = -rr - rs;
    } else if (dr > ds) {
        rr = -rq - rs;
    }
    return vec2(rq, rr);
}

void fragment() {
    vec2 p = world_position.xz;
    float q = (sqrt(3.0) / 3.0 * p.x - p.y / 3.0) / cell_radius;
    float r = (2.0 / 3.0 * p.y) / cell_radius;
    vec2 axial = round_axial(vec2(q, r));
    float row = axial.y;
    float parity = mod(row, 2.0);
    float column = axial.x + (row - parity) / 2.0;

    vec2 center = vec2(cell_radius * sqrt(3.0) * (axial.x + axial.y / 2.0), cell_radius * 1.5 * axial.y);
    vec2 d = abs(p - center) / cell_radius;
    // Distance to the hexagon's edge, one at the edge, for a pointy-top cell.
    float hex_distance = max(d.x * sqrt(3.0) / 2.0 + d.y / 2.0, d.y);

    vec3 color = paper.rgb;
    if (column >= 0.0 && row >= 0.0 && column < grid_size.x && row < grid_size.y) {
        color = texelFetch(cells, ivec2(int(column), int(row)), 0).rgb;
    }
    if (hex_distance > 1.0 - edge_width) {
        color = edge.rgb;
    }
    ALBEDO = color;
}
