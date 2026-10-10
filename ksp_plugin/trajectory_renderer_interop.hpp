#pragma once
#include <cstdint>

namespace principia {
namespace ksp_plugin {
namespace _trajectory_renderer_interop {
namespace internal {

#pragma pack(push, 8)
struct TrajectoryVertex {
  float position[3];      // x, y, z (World coordinates relative to current camera anchor)
  float velocity[3];      // vx, vy, vz (velocity vector for shading)
  float colour[4];        // r, g, b, a (RGBA vertex colour)
  double time_seconds;    // Node timestamp (seconds since epoch)
};
#pragma pack(pop)

}  // namespace internal

using internal::TrajectoryVertex;

}  // namespace _trajectory_renderer_interop
}  // namespace ksp_plugin
}  // namespace principia
