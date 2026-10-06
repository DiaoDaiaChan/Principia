using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnityEngine;

namespace principia.ksp_plugin_adapter {

/// <summary>
/// Continuous polynomial slice for instantaneous O(1) algebraic kinematics evaluation.
/// Matches the memory layout of C++ DensePolynomialSlice exactly (224 bytes, 8-byte aligned).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct DensePolynomialSlice {
  public double t_start;
  public double t_end;

  // 5th-order Hermite position polynomial coefficients:
  // q(tau) = c0 + tau * (c1 + tau * (c2 + tau * (c3 + tau * (c4 + tau * c5))))
  public Vector3d c0;
  public Vector3d c1;
  public Vector3d c2;
  public Vector3d c3;
  public Vector3d c4;
  public Vector3d c5;

  // Attitude orientation quaternions at slice boundaries
  public UnityEngine.QuaternionD q_start;
  public UnityEngine.QuaternionD q_end;

  public bool IsValid => t_end > t_start;
  public double Duration => t_end - t_start;

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public void EvaluatePosition(double t, out Vector3d position) {
    double duration = t_end - t_start;
    if (duration <= 0.0) {
      position = c0;
      return;
    }
    double tau = (t - t_start) / duration;
    // Horner's method (5 FMAs per dimension)
    position = new Vector3d(
      c0.x + tau * (c1.x + tau * (c2.x + tau * (c3.x + tau * (c4.x + tau * c5.x)))),
      c0.y + tau * (c1.y + tau * (c2.y + tau * (c3.y + tau * (c4.y + tau * c5.y)))),
      c0.z + tau * (c1.z + tau * (c2.z + tau * (c3.z + tau * (c4.z + tau * c5.z))))
    );
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public void EvaluateVelocity(double t, out Vector3d velocity) {
    double duration = t_end - t_start;
    if (duration <= 0.0) {
      velocity = Vector3d.zero;
      return;
    }
    double tau = (t - t_start) / duration;
    double inv_duration = 1.0 / duration;
    // Analytical derivative of Horner's polynomial:
    // v(tau) = (1 / duration) * (c1 + tau * (2*c2 + tau * (3*c3 + tau * (4*c4 + tau * 5*c5))))
    velocity = new Vector3d(
      inv_duration * (c1.x + tau * (2.0 * c2.x + tau * (3.0 * c3.x + tau * (4.0 * c4.x + tau * (5.0 * c5.x))))),
      inv_duration * (c1.y + tau * (2.0 * c2.y + tau * (3.0 * c3.y + tau * (4.0 * c4.y + tau * (5.0 * c5.y))))),
      inv_duration * (c1.z + tau * (2.0 * c2.z + tau * (3.0 * c3.z + tau * (4.0 * c4.z + tau * (5.0 * c5.z)))))
    );
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public void EvaluateRotation(double t, out UnityEngine.QuaternionD rotation) {
    double duration = t_end - t_start;
    if (duration <= 0.0) {
      rotation = q_start;
      return;
    }
    double tau = Math.Max(0.0, Math.Min(1.0, (t - t_start) / duration));
    rotation = Slerp(q_start, q_end, tau);
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static UnityEngine.QuaternionD Slerp(
      in UnityEngine.QuaternionD p,
      in UnityEngine.QuaternionD q,
      double t) {
    double cos_half_theta = p.x * q.x + p.y * q.y + p.z * q.z + p.w * q.w;
    UnityEngine.QuaternionD target = q;
    if (cos_half_theta < 0.0) {
      target = new UnityEngine.QuaternionD(-q.x, -q.y, -q.z, -q.w);
      cos_half_theta = -cos_half_theta;
    }
    if (Math.Abs(cos_half_theta) >= 1.0) {
      return p;
    }
    if (cos_half_theta > 0.9995) {
      var result = new UnityEngine.QuaternionD(
          p.x + t * (target.x - p.x),
          p.y + t * (target.y - p.y),
          p.z + t * (target.z - p.z),
          p.w + t * (target.w - p.w));
      double norm = Math.Sqrt(result.x * result.x + result.y * result.y + result.z * result.z + result.w * result.w);
      if (norm > 0.0) {
        double inv_norm = 1.0 / norm;
        return new UnityEngine.QuaternionD(result.x * inv_norm, result.y * inv_norm, result.z * inv_norm, result.w * inv_norm);
      }
      return p;
    }
    double half_theta = Math.Acos(cos_half_theta);
    double sin_half_theta = Math.Sqrt(1.0 - cos_half_theta * cos_half_theta);
    if (Math.Abs(sin_half_theta) < 0.001) {
      return new UnityEngine.QuaternionD(
          0.5 * (p.x + target.x),
          0.5 * (p.y + target.y),
          0.5 * (p.z + target.z),
          0.5 * (p.w + target.w));
    }
    double ratio_a = Math.Sin((1.0 - t) * half_theta) / sin_half_theta;
    double ratio_b = Math.Sin(t * half_theta) / sin_half_theta;
    return new UnityEngine.QuaternionD(
        p.x * ratio_a + target.x * ratio_b,
        p.y * ratio_a + target.y * ratio_b,
        p.z * ratio_a + target.z * ratio_b,
        p.w * ratio_a + target.w * ratio_b);
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public void Evaluate(double t, out Vector3d position, out Vector3d velocity, out UnityEngine.QuaternionD rotation) {
    EvaluatePosition(t, out position);
    EvaluateVelocity(t, out velocity);
    EvaluateRotation(t, out rotation);
  }

  /// <summary>
  /// Constructs a cubic Hermite polynomial slice connecting two states with C1 continuity.
  /// </summary>
  public static DensePolynomialSlice FromEndpoints(
      double t0,
      double t1,
      Vector3d p0,
      Vector3d v0,
      Vector3d p1,
      Vector3d v1,
      UnityEngine.QuaternionD rot0,
      UnityEngine.QuaternionD rot1) {
    double dt = t1 - t0;
    Vector3d v0_scaled = v0 * dt;
    Vector3d v1_scaled = v1 * dt;
    Vector3d dp = p1 - p0;

    return new DensePolynomialSlice {
      t_start = t0,
      t_end = t1,
      c0 = p0,
      c1 = v0_scaled,
      c2 = 3.0 * dp - 2.0 * v0_scaled - v1_scaled,
      c3 = -2.0 * dp + v0_scaled + v1_scaled,
      c4 = Vector3d.zero,
      c5 = Vector3d.zero,
      q_start = rot0,
      q_end = rot1
    };
  }
}

}
