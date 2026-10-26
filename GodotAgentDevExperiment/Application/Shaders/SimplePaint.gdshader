shader_type spatial;
render_mode unshaded, cull_back, fog_disabled;

// Single-file Godot 4.7.2 Forward+ port of
// 3dTestScene_CppDx12/Source/SimplePaint/Material.cpp and SimplePaintCore.hlsli.
// Facing-lobe and anchored color-curve mathematical construction: Kevin Vu.
// Assign to a ShaderMaterial and use an orthographic Camera3D. View axes are
// +X right, +Y up, +Z toward the camera. Use finite, nonzero mesh normals whose
// interpolation cannot approach zero, and rigid/uniform-scale transforms.
//
// Inputs must follow the source's numerical contract. Inspector hints guide
// editing; they do not validate values set from code. The host must reject
// invalid inputs before setting uniforms. No input clamps or angle wrapping.
// Unlike the C++ adapter's binary64 precomputation, this self-contained shader
// constructs material coefficients in binary32 in vertex(), once per vertex.

/** Base color in sRGB. Each channel must be in [0.0009765625, 0.9990234375]. Godot decodes source_color to linear RGB; do not decode it again. */
uniform vec3 BaseColorSrgb : source_color = vec3(0.107, 0.223, 0.578);

/** Base color appears at tone 1 - Brightness. Increasing this moves the color anchor toward the dark end; it is not an RGB multiplier. */
uniform float Brightness : hint_range(0.0009765625, 0.9990234375, 0.0009765625) = 0.5;

/** Moves the facing maximum sideways. Exact zero selects symmetric facing and makes RotationDegrees irrelevant. */
uniform float Shift : hint_range(0.0, 0.9990234375, 0.0009765625) = 0.0;

/** Counterclockwise screen direction: 0 right, 90 up, 180 left, 270 down. Valid range is [0, 360); the hint maximum is the largest binary32 value below 360. */
uniform float RotationDegrees : hint_range(0.0, 359.9990234375, 0.0009765625) = 0.0;

/** Tone at the silhouette and throughout the back hemisphere. May exceed or equal LightPoint. Zero produces exact black. */
uniform float DarkPoint : hint_range(0.0, 0.9990234375, 0.0009765625) = 0.0;

/** Tone at the shifted facing maximum. May be below or equal DarkPoint. One produces exact white. */
uniform float LightPoint : hint_range(0.0009765625, 1.0, 0.0009765625) = 1.0;

// Use our own unnormalized varying instead of Godot's normalized NORMAL.
// With orthographic projection, smooth interpolation equals noperspective.
varying smooth vec3 PaintNormal;
varying flat float ShiftRatio;
varying flat vec3 NumeratorDark;
varying flat vec3 NumeratorLight;
varying flat vec3 ComplementDark;
varying flat vec3 ComplementLight;

void vertex() {
    vec3 ViewNormal = MODELVIEW_NORMAL_MATRIX * NORMAL;
    ShiftRatio = 1.0;
    if (Shift != 0.0) {
        float Angle = -RotationDegrees * (PI / 180.0);
        float Cosine = cos(Angle);
        float Sine = sin(Angle);
        ViewNormal.xy = vec2(
            ViewNormal.x * Cosine - ViewNormal.y * Sine,
            ViewNormal.x * Sine + ViewNormal.y * Cosine);
        ShiftRatio = sqrt((1.0 - Shift) / (1.0 + Shift));
    }
    PaintNormal = ViewNormal;

    // Godot supplies BaseColorSrgb in linear space through source_color.
    vec3 NumeratorCoefficient = BaseColorSrgb * Brightness;
    vec3 ComplementCoefficient = (vec3(1.0) - BaseColorSrgb) * (1.0 - Brightness);
    vec3 DarkNumerator = NumeratorCoefficient * DarkPoint;
    vec3 LightNumerator = NumeratorCoefficient * LightPoint;
    vec3 DarkComplement = ComplementCoefficient * (1.0 - DarkPoint);
    vec3 LightComplement = ComplementCoefficient * (1.0 - LightPoint);
    // Common per-channel scaling cancels, keeping one coefficient exactly one.
    vec3 CoefficientScale = max(max(DarkNumerator, LightNumerator),
        max(DarkComplement, LightComplement));
    NumeratorDark = DarkNumerator / CoefficientScale;
    NumeratorLight = LightNumerator / CoefficientScale;
    ComplementDark = DarkComplement / CoefficientScale;
    ComplementLight = LightComplement / CoefficientScale;
}

// Return nonnegative homogeneous (P, Q), with facing = P / (P + Q).
// Retaining Q independently preserves tiny complements near the highlight.
vec2 SimplePaintFacingWeights(vec3 ViewNormal, float MaterialShiftRatio) {
    // Handle the back hemisphere and both slice poles before any division.
    if (ViewNormal.z <= 0.0) {
        return vec2(0.0, 1.0);
    }

    float NormalLength = sqrt(dot(ViewNormal, ViewNormal));
    if (Shift == 0.0) {
        // L - z = (x*x + y*y) / (L + z), without cancellation.
        return vec2(ViewNormal.z,
            dot(ViewNormal.xy, ViewNormal.xy) / (NormalLength + ViewNormal.z));
    }

    // Scale before squaring so a tiny XZ slice survives near the Y poles.
    float SliceScale = max(abs(ViewNormal.x), ViewNormal.z);
    vec2 Slice = ViewNormal.xz / SliceScale;
    float SliceLength = sqrt(dot(Slice, Slice));
    float SliceRadius = SliceScale * SliceLength;
    // Two half-angle charts avoid subtracting nearly equal radius and abs(x).
    float FirstHalfAngle = MaterialShiftRatio *
        (ViewNormal.x >= 0.0 ? SliceLength + Slice.x : Slice.y);
    float SecondHalfAngle = ViewNormal.x >= 0.0 ? Slice.y : SliceLength - Slice.x;
    float SquareSum = FirstHalfAngle * FirstHalfAngle + SecondHalfAngle * SecondHalfAngle;
    float Difference = FirstHalfAngle - SecondHalfAngle;
    float FacingWeight = SliceRadius * (2.0 * FirstHalfAngle * SecondHalfAngle);
    // L - r = y*y / (L + r); both complementary contributions are nonnegative.
    float ComplementWeight = (ViewNormal.y * ViewNormal.y / (NormalLength + SliceRadius)) *
        SquareSum + SliceRadius * Difference * Difference;
    return vec2(FacingWeight, ComplementWeight);
}

void fragment() {
    vec2 Weights = SimplePaintFacingWeights(PaintNormal, ShiftRatio);
    vec3 Numerator = NumeratorDark * Weights.y + NumeratorLight * Weights.x;
    vec3 Complement = ComplementDark * Weights.y + ComplementLight * Weights.x;
    // Positive denominator for valid inputs. Only remove output roundoff.
    // Return linear RGB and let Godot perform the final display conversion.
    ALBEDO = clamp(Numerator / (Numerator + Complement), vec3(0.0), vec3(1.0));
    // Leave ALPHA unwritten to keep Godot's opaque pipeline (alpha is one).
}
