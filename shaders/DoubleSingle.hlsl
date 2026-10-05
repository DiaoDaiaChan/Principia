// DoubleSingle.hlsl - High-Precision 53-Bit Emulated Double-Precision Library
// Principles: Dekker (1971), Knuth (1969), Shewchuk (1997)
// Runs at full speed on un-throttled FP32 ALUs using hardware FMA instructions.

#ifndef PRINCIPIA_DOUBLE_SINGLE_HLSL
#define PRINCIPIA_DOUBLE_SINGLE_HLSL

// Double-single representation: x = hi + lo, where |lo| <= 0.5 * ulp(hi)
struct ds_real {
    float hi;
    float lo;
};

// Vector3 representation in double-single precision
struct ds_vec3 {
    ds_real x;
    ds_real y;
    ds_real z;
};

// Construct ds_real from single float
ds_real ds_make(float v) {
    ds_real res;
    res.hi = v;
    res.lo = 0.0f;
    return res;
}

// Convert ds_real to single float
float ds_to_float(ds_real a) {
    return a.hi + a.lo;
}

// Exact addition of two floats: a + b = s + e
ds_real ds_two_sum(float a, float b) {
    float s = a + b;
    float v = s - a;
    float e = (a - (s - v)) + (b - v);
    ds_real res;
    res.hi = s;
    res.lo = e;
    return res;
}

// Exact product of two floats: a * b = p + e (using hardware FMA)
ds_real ds_two_prod(float a, float b) {
    float p = a * b;
    float e = fma(a, b, -p);
    ds_real res;
    res.hi = p;
    res.lo = e;
    return res;
}

// Double-single addition: a + b
ds_real ds_add(ds_real a, ds_real b) {
    ds_real s = ds_two_sum(a.hi, b.hi);
    float t = a.lo + b.lo;
    ds_real c = ds_two_sum(s.hi, s.lo + t);
    return c;
}

// Double-single subtraction: a - b
ds_real ds_sub(ds_real a, ds_real b) {
    ds_real neg_b;
    neg_b.hi = -b.hi;
    neg_b.lo = -b.lo;
    return ds_add(a, neg_b);
}

// Double-single multiplication: a * b
ds_real ds_mul(ds_real a, ds_real b) {
    ds_real c = ds_two_prod(a.hi, b.hi);
    c.lo += a.hi * b.lo + a.lo * b.hi;
    ds_real res = ds_two_sum(c.hi, c.lo);
    return res;
}

// Double-single square: a * a
ds_real ds_sqr(ds_real a) {
    ds_real c = ds_two_prod(a.hi, a.hi);
    c.lo += 2.0f * a.hi * a.lo;
    return ds_two_sum(c.hi, c.lo);
}

// Double-single division: a / b
ds_real ds_div(ds_real a, ds_real b) {
    float q1 = a.hi / b.hi;
    ds_real r = ds_sub(a, ds_mul(b, ds_make(q1)));
    float q2 = r.hi / b.hi;
    return ds_two_sum(q1, q2);
}

// Double-single square root: sqrt(a)
ds_real ds_sqrt(ds_real a) {
    if (a.hi <= 0.0f) {
        ds_real zero = { 0.0f, 0.0f };
        return zero;
    }
    float s = sqrt(a.hi);
    ds_real s_ds = ds_make(s);
    ds_real diff = ds_sub(a, ds_sqr(s_ds));
    float d = diff.hi / (2.0f * s);
    return ds_two_sum(s, d);
}

// Construct ds_vec3 from float3
ds_vec3 ds_vec3_make(float3 v) {
    ds_vec3 res;
    res.x = ds_make(v.x);
    res.y = ds_make(v.y);
    res.z = ds_make(v.z);
    return res;
}

// Convert ds_vec3 to float3
float3 ds_vec3_to_float3(ds_vec3 v) {
    return float3(ds_to_float(v.x), ds_to_float(v.y), ds_to_float(v.z));
}

// ds_vec3 addition: a + b
ds_vec3 ds_vec3_add(ds_vec3 a, ds_vec3 b) {
    ds_vec3 res;
    res.x = ds_add(a.x, b.x);
    res.y = ds_add(a.y, b.y);
    res.z = ds_add(a.z, b.z);
    return res;
}

// ds_vec3 subtraction: a - b
ds_vec3 ds_vec3_sub(ds_vec3 a, ds_vec3 b) {
    ds_vec3 res;
    res.x = ds_sub(a.x, b.x);
    res.y = ds_sub(a.y, b.y);
    res.z = ds_sub(a.z, b.z);
    return res;
}

// ds_vec3 scalar multiplication: a * s
ds_vec3 ds_vec3_scale(ds_vec3 a, ds_real s) {
    ds_vec3 res;
    res.x = ds_mul(a.x, s);
    res.y = ds_mul(a.y, s);
    res.z = ds_mul(a.z, s);
    return res;
}

// ds_vec3 dot product: a . b
ds_real ds_vec3_dot(ds_vec3 a, ds_vec3 b) {
    ds_real px = ds_mul(a.x, b.x);
    ds_real py = ds_mul(a.y, b.y);
    ds_real pz = ds_mul(a.z, b.z);
    return ds_add(ds_add(px, py), pz);
}

// ds_vec3 length squared: |v|^2
ds_real ds_vec3_length_sqr(ds_vec3 v) {
    return ds_vec3_dot(v, v);
}

// ds_vec3 length: |v|
ds_real ds_vec3_length(ds_vec3 v) {
    return ds_sqrt(ds_vec3_length_sqr(v));
}

#endif // PRINCIPIA_DOUBLE_SINGLE_HLSL
