#pragma once

#include <cstdint>
#include <string>
#include <vector>

#include "geometry/grassmann.hpp"
#include "geometry/named_quantities.hpp"
#include "geometry/quaternion.hpp"
#include "geometry/r3_element.hpp"
#include "geometry/rigid_transformation.hpp"
#include "ksp_plugin/frames.hpp"
#include "ksp_plugin/identification.hpp"
#include "ksp_plugin/interface.hpp"
#include "ksp_plugin/part.hpp"
#include "ksp_plugin/plugin.hpp"
#include "quantities/named_quantities.hpp"
#include "quantities/quantities.hpp"
#include "quantities/si.hpp"

namespace principia {
namespace ksp_plugin {
namespace _batch_state_sync {

using namespace principia::geometry::_grassmann;
using namespace principia::geometry::_named_quantities;
using namespace principia::geometry::_quaternion;
using namespace principia::geometry::_r3_element;
using namespace principia::geometry::_rigid_transformation;
using namespace principia::interface;
using namespace principia::ksp_plugin::_frames;
using namespace principia::ksp_plugin::_identification;
using namespace principia::ksp_plugin::_part;
using namespace principia::ksp_plugin::_plugin;
using namespace principia::quantities::_named_quantities;
using namespace principia::quantities::_quantities;
using namespace principia::quantities::_si;

// Bitflags for PartBatchInsertState.flags
enum PartBatchFlags : uint32_t {
  PART_BATCH_FLAG_NONE = 0,
  PART_BATCH_FLAG_SOLID_ROCKET_MOTOR = 1 << 0,
  PART_BATCH_FLAG_HAS_INTRINSIC_TORQUE = 1 << 1,
  PART_BATCH_FLAG_HAS_INTRINSIC_FORCE = 1 << 2,
  PART_BATCH_FLAG_HAS_LEVER_FORCE = 1 << 3,
};

// 64-byte/8-byte aligned compact layout for batching loaded part insertion and force injection.
#pragma pack(push, 8)
struct PartBatchInsertState {
  uint32_t part_id;
  uint32_t flags;
  double mass_in_tonnes;
  XYZ centre_of_mass;
  XYZ moments_of_inertia_in_tonnes;
  WXYZ principal_axes_rotation;
  int32_t main_body_index;
  int32_t reserved;
  QP main_body_world_degrees_of_freedom;
  QP part_world_degrees_of_freedom;
  WXYZ part_rotation;
  XYZ part_angular_velocity;
  double delta_t;
  XYZ intrinsic_torque;
  XYZ intrinsic_force;
  XYZ lever_force;
  XYZ lever_arm;
  char vessel_guid[40];
  char part_name[64];
};

struct PartBatchApparentMotion {
  uint32_t part_id;
  uint32_t reserved;
  QP degrees_of_freedom;
  WXYZ rotation;
  XYZ angular_velocity;
};
#pragma pack(pop)

class BatchStateProcessor {
 public:
  static void ProcessBatchInsertOrKeepLoadedParts(
      Plugin* plugin,
      PartBatchInsertState const* states,
      int count) {
    if (plugin == nullptr || states == nullptr || count <= 0) {
      return;
    }
    for (int i = 0; i < count; ++i) {
      PartBatchInsertState const& s = states[i];
      InertiaTensor<RigidPart> const inertia_tensor_in_rigid_part =
          FromMomentsOfInertia(s.moments_of_inertia_in_tonnes,
                               s.principal_axes_rotation);

      plugin->InsertOrKeepLoadedPart(
          s.part_id,
          s.part_name,
          s.mass_in_tonnes * Tonne,
          FromXYZ<Position<EccentricPart>>(s.centre_of_mass),
          inertia_tensor_in_rigid_part,
          (s.flags & PART_BATCH_FLAG_SOLID_ROCKET_MOTOR) != 0,
          s.vessel_guid,
          s.main_body_index,
          FromQP<DegreesOfFreedom<World>>(s.main_body_world_degrees_of_freedom),
          MakePartRigidMotion(
              s.part_world_degrees_of_freedom,
              s.part_rotation,
              s.part_angular_velocity),
          s.delta_t * Second);

      if ((s.flags & PART_BATCH_FLAG_HAS_INTRINSIC_TORQUE) != 0) {
        plugin->ApplyPartIntrinsicTorque(
            s.part_id,
            Bivector<Torque, World>(FromXYZ(s.intrinsic_torque) *
                                    Kilo(Newton) * Metre * Radian));
      }
      if ((s.flags & PART_BATCH_FLAG_HAS_INTRINSIC_FORCE) != 0) {
        plugin->ApplyPartIntrinsicForce(
            s.part_id,
            Vector<Force, World>(FromXYZ(s.intrinsic_force) * Kilo(Newton)));
      }
      if ((s.flags & PART_BATCH_FLAG_HAS_LEVER_FORCE) != 0) {
        plugin->ApplyPartIntrinsicForceAtPosition(
            s.part_id,
            Vector<Force, World>(FromXYZ(s.lever_force) * Kilo(Newton)),
            Displacement<World>(FromXYZ(s.lever_arm) * Metre));
      }
    }
  }

  static void ProcessBatchPartSetApparentRigidMotion(
      Plugin* plugin,
      PartBatchApparentMotion const* states,
      int count) {
    if (plugin == nullptr || states == nullptr || count <= 0) {
      return;
    }
    for (int i = 0; i < count; ++i) {
      PartBatchApparentMotion const& s = states[i];
      plugin->SetPartApparentRigidMotion(
          s.part_id,
          MakePartApparentRigidMotion(
              s.degrees_of_freedom,
              s.rotation,
              s.angular_velocity));
    }
  }

  static void ProcessBatchPartGetActualRigidMotion(
      Plugin const* plugin,
      Origin const* origin,
      uint32_t const* part_ids,
      QPRW* motions_out,
      int count) {
    if (plugin == nullptr || origin == nullptr || part_ids == nullptr ||
        motions_out == nullptr || count <= 0) {
      return;
    }
    RigidMotion<Barycentric, World> const barycentric_to_world =
        plugin->BarycentricToWorld(
            origin->reference_part_is_unmoving,
            origin->reference_part_id,
            origin->reference_part_is_at_origin
                ? std::nullopt
                : std::make_optional(FromXYZ<Position<World>>(
                      origin->main_body_centre_in_world)));

    for (int i = 0; i < count; ++i) {
      RigidMotion<EccentricPart, World> const part_motion =
          plugin->GetPartActualMotion(part_ids[i], barycentric_to_world);
      DegreesOfFreedom<World> const part_dof =
          part_motion({EccentricPart::origin, EccentricPart::unmoving});
      Rotation<EccentricPart, World> const part_orientation =
          part_motion.orthogonal_map().AsRotation();
      AngularVelocity<World> const part_angular_velocity =
          part_motion.angular_velocity_of<EccentricPart>();
      motions_out[i] = {
          ToQP(part_dof),
          ToWXYZ(part_orientation.quaternion()),
          ToXYZ(part_angular_velocity.coordinates() / (Radian / Second))};
    }
  }
};

}  // namespace _batch_state_sync

namespace batch_state_sync {
using _batch_state_sync::PartBatchFlags;
using _batch_state_sync::PartBatchInsertState;
using _batch_state_sync::PartBatchApparentMotion;
using _batch_state_sync::BatchStateProcessor;
}  // namespace batch_state_sync

}  // namespace ksp_plugin
}  // namespace principia
