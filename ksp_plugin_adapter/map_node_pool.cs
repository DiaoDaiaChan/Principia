using System;
using System.Collections.Generic;
using KSP.Localization;

namespace principia {
namespace ksp_plugin_adapter {

internal class MapNodePool {
  // We render at most 64 markers of one type and one provenance (e.g., at
  // most 64 perilunes for the prediction of the active vessel).  This is
  // more than is readable, and keeps the size of the map node pool under
  // control.
  public const int MaxNodesPerProvenance = 64;

  public enum NodeSource {
    Prediction,
    FlightPlan,
  }

  public struct Provenance : IEquatable<Provenance> {
    public string vessel_guid;
    public NodeSource source;
    public MapObject.ObjectType type;

    public Provenance(string vessel_guid,
                      NodeSource source,
                      MapObject.ObjectType type) {
      this.vessel_guid = vessel_guid;
      this.source = source;
      this.type = type;
    }

    public override int GetHashCode() {
      unchecked {
        int hash = (vessel_guid != null ? vessel_guid.GetHashCode() : 0);
        hash = (hash * 397) ^ (int)source;
        hash = (hash * 397) ^ (int)type;
        return hash;
      }
    }
    public override bool Equals(object other) =>
        other is Provenance p && Equals(p);
    public bool Equals(Provenance other) => vessel_guid == other.vessel_guid &&
                                            source == other.source &&
                                            type == other.type;
  }

  public readonly struct VisibilityModifiers {
    public readonly bool show_unpinned;
    public readonly bool can_hover;

    public VisibilityModifiers(bool show_unpinned, bool can_hover) {
      this.show_unpinned = show_unpinned;
      this.can_hover = can_hover;
    }
}

  public class SingleProvenancePool {
    public int nodes_used;
    public int active_nodes;
    public bool was_refreshed;
    public List<KSP.UI.Screens.Mapview.MapNode> nodes =
        new List<KSP.UI.Screens.Mapview.MapNode>(MaxNodesPerProvenance);
  }

  public MapNodePool(PrincipiaPluginAdapter adapter,
                     Func<VisibilityModifiers> visibility_modifiers) {
    adapter_ = adapter;
    nodes_ = new Dictionary<Provenance, SingleProvenancePool>();
    properties_ =
        new Dictionary<KSP.UI.Screens.Mapview.MapNode, MapNodeProperties>();
    visibility_modifiers_ = visibility_modifiers;
  }

  public void Clear() {
    foreach (var provenance_pool in nodes_) {
      foreach (var node in provenance_pool.Value.nodes) {
        if (node != null && node.gameObject != null) {
          node.Terminate();
        }
      }
    }
    nodes_ = new Dictionary<Provenance, SingleProvenancePool>();
    properties_ =
        new Dictionary<KSP.UI.Screens.Mapview.MapNode, MapNodeProperties>();
  }

  private bool IsNodeCulled(KSP.UI.Screens.Mapview.MapNode node, MapNodeProperties properties) {
    if (node == null || properties == null || properties.payload == null) {
      return false;
    }
    // Never cull pinned or hovered nodes
    if (node.Pinned || node.Hover) {
      return false;
    }
    UnityEngine.Camera camera = PlanetariumCamera.Camera;
    if (camera == null) {
      return false;
    }
    UnityEngine.Vector3 scaled_space_pos =
        (UnityEngine.Vector3)ScaledSpace.LocalToScaledSpace(properties.payload.GetWorldPosition());
    UnityEngine.Vector3 viewport_pos = camera.WorldToViewportPoint(scaled_space_pos);

    // Behind camera check: z <= 0
    if (viewport_pos.z <= 0f) {
      return true;
    }
    // Off-screen viewport check with safety margin to prevent premature clipping of label UI
    const float viewport_margin = 0.15f;
    const float viewport_min = -viewport_margin;
    const float viewport_max = 1.0f + viewport_margin;
    if (viewport_pos.x < viewport_min || viewport_pos.x > viewport_max ||
        viewport_pos.y < viewport_min || viewport_pos.y > viewport_max) {
      return true;
    }
    return false;
  }

  public void Update() {
    foreach (var provenance_pool in nodes_) {
      var pool = provenance_pool.Value;
      int active = pool.was_refreshed ? pool.nodes_used : pool.active_nodes;
      pool.active_nodes = active;
      pool.was_refreshed = false;
      pool.nodes_used = 0;
      for (int i = active; i < pool.nodes.Count; ++i) {
        var node = pool.nodes[i];
        if (properties_.TryGetValue(node, out var props) && props.visible) {
          props.visible = false;
          node.NodeUpdate();
        }
      }
      for (int i = 0; i < active; ++i) {
        var node = pool.nodes[i];
        if (node == null) {
          continue;
        }
        if (properties_.TryGetValue(node, out var props)) {
          if (IsNodeCulled(node, props)) {
            if (props.visible) {
              props.visible = false;
              node.NodeUpdate();
            }
            continue;
          }
          if (!props.visible) {
            props.visible = true;
          }
        }
        node.NodeUpdate();
      }
    }
  }

  public void RenderMarkers(
      IEnumerable<TQP> markers,
      Provenance provenance,
      ReferenceFrameSelector<PlottingFrameParameters> reference_frame) {
    PrepareToRenderMarkers(provenance,
                           reference_frame,
                           out UnityEngine.Color colour,
                           out MapObject associated_map_object);

    var pool = nodes_[provenance];
    CelestialBody anchor_body = associated_map_object?.celestialBody;
    Vessel anchor_vessel = associated_map_object?.vessel;

    foreach (TQP marker in markers) {
      if (pool.nodes_used == pool.nodes.Count) {
        pool.nodes.Add(MakePoolNode());
      }
      var map_node = pool.nodes[pool.nodes_used++];
      if (!properties_.TryGetValue(map_node, out var node_properties)) {
        node_properties = new MapNodeProperties();
        properties_[map_node] = node_properties;
      }
      node_properties.visible = true;
      node_properties.object_type = provenance.type;
      node_properties.colour = colour;
      node_properties.reference_frame = reference_frame;
      node_properties.source = provenance.source;
      node_properties.associated_map_object = associated_map_object;

      Vector3d world_pos = (Vector3d)marker.qp.q;
      Vector3d rel_offset = world_pos;
      bool has_rel = false;
      if (anchor_body != null) {
        rel_offset = world_pos - anchor_body.position;
        has_rel = true;
      } else if (anchor_vessel != null) {
        rel_offset = world_pos - anchor_vessel.GetWorldPos3D();
        has_rel = true;
      }

      if (node_properties.payload is LegacyMapNodePayload legacy_payload) {
        legacy_payload.time = marker.t;
        legacy_payload.world_position = world_pos;
        legacy_payload.relative_offset = rel_offset;
        legacy_payload.anchor_body = anchor_body;
        legacy_payload.anchor_vessel = anchor_vessel;
        legacy_payload.has_relative_offset = has_rel;
        legacy_payload.velocity = (Vector3d)marker.qp.p;
      } else {
        node_properties.payload = new LegacyMapNodePayload {
            time = marker.t,
            world_position = world_pos,
            relative_offset = rel_offset,
            anchor_body = anchor_body,
            anchor_vessel = anchor_vessel,
            has_relative_offset = has_rel,
            velocity = (Vector3d)marker.qp.p
        };
      }
    }
  }

  public void RenderNodes(
      IEnumerable<Node> markers,
      Provenance provenance,
      ReferenceFrameSelector<PlottingFrameParameters> reference_frame) {
    PrepareToRenderMarkers(provenance,
                           reference_frame,
                           out UnityEngine.Color colour,
                           out MapObject associated_map_object);

    var pool = nodes_[provenance];
    CelestialBody anchor_body = associated_map_object?.celestialBody;
    Vessel anchor_vessel = associated_map_object?.vessel;

    foreach (Node marker in markers) {
      if (pool.nodes_used == pool.nodes.Count) {
        pool.nodes.Add(MakePoolNode());
      }
      var map_node = pool.nodes[pool.nodes_used++];
      if (!properties_.TryGetValue(map_node, out var node_properties)) {
        node_properties = new MapNodeProperties();
        properties_[map_node] = node_properties;
      }
      node_properties.visible = true;
      node_properties.object_type = provenance.type;
      node_properties.colour = colour;
      node_properties.reference_frame = reference_frame;
      node_properties.source = provenance.source;
      node_properties.associated_map_object = associated_map_object;

      Vector3d world_pos = (Vector3d)marker.world_position;
      Vector3d rel_offset = world_pos;
      bool has_rel = false;
      if (anchor_body != null) {
        rel_offset = world_pos - anchor_body.position;
        has_rel = true;
      } else if (anchor_vessel != null) {
        rel_offset = world_pos - anchor_vessel.GetWorldPos3D();
        has_rel = true;
      }

      if (node_properties.payload is NodeMapNodePayload node_payload) {
        node_payload.time = marker.time;
        node_payload.world_position = world_pos;
        node_payload.relative_offset = rel_offset;
        node_payload.anchor_body = anchor_body;
        node_payload.anchor_vessel = anchor_vessel;
        node_payload.has_relative_offset = has_rel;
        node_payload.node = marker;
      } else {
        node_properties.payload = new NodeMapNodePayload {
            time = marker.time,
            world_position = world_pos,
            relative_offset = rel_offset,
            anchor_body = anchor_body,
            anchor_vessel = anchor_vessel,
            has_relative_offset = has_rel,
            node = marker
        };
      }
    }
  }

  private void PrepareToRenderMarkers(
      Provenance provenance,
      ReferenceFrameSelector<PlottingFrameParameters> reference_frame,
      out UnityEngine.Color colour,
      out MapObject associated_map_object) {
    if (!nodes_.TryGetValue(provenance, out var pool)) {
      pool = new SingleProvenancePool();
      nodes_.Add(provenance, pool);
    }
    pool.was_refreshed = true;
    pool.nodes_used = 0;
    switch (provenance.type) {
      case MapObject.ObjectType.Apoapsis:
      case MapObject.ObjectType.Periapsis:
        CelestialBody fixed_body = reference_frame.Centre();
        associated_map_object = fixed_body.MapObject;
        colour = fixed_body.orbit == null
                     ? XKCDColors.SunshineYellow
                     : fixed_body.orbitDriver.Renderer.nodeColor;
        break;
      case MapObject.ObjectType.ApproachIntersect:
        associated_map_object = reference_frame.target_vessel.mapObject;
        colour = XKCDColors.Chartreuse;
        break;
      case MapObject.ObjectType.AscendingNode:
      case MapObject.ObjectType.DescendingNode:
        if (reference_frame.Centre() == null) {
          // In two-body frames, if apsides are shown, they are shown with the
          // colour of the secondary (or in XKCD chartreuse if the secondary is
          // a vessel).
          // The nodes are with respect to the orbit of the secondary around the
          // primary. We show the nodes with the colour of the primary.
          CelestialBody primary = reference_frame.Primary();
          associated_map_object = primary.MapObject;
          colour = primary.orbit == null
                        ? XKCDColors.SunshineYellow
                        : primary.orbitDriver.Renderer.nodeColor;
        } else {
          // In one-body frames, the apsides are shown with the colour of the
          // body.
          // The nodes are with respect to the equator, rather than with respect
          // to an orbit. We show the nodes in a different (but arbitrary)
          // colour so that they can be distinguished easily.
          associated_map_object = reference_frame.Centre().MapObject;
          colour = XKCDColors.Chartreuse;
        }
        break;
      case MapObject.ObjectType.PatchTransition:
        associated_map_object = reference_frame.Centre().MapObject;
        colour = XKCDColors.Orange;
        break;
      default:
        throw Log.Fatal($"Unexpected type {provenance.type}");
    }
    colour.a = 1;
  }

  private KSP.UI.Screens.Mapview.MapNode MakePoolNode() {
    var new_node = KSP.UI.Screens.Mapview.MapNode.Create(
        "apsis",
        // If we see this colour, something has gone wrong.
        XKCDColors.Pale,
        pixelSize : 32,
        hoverable : true,
        pinnable : true,
        blocksInput : true);
    new_node.OnClick +=
        (KSP.UI.Screens.Mapview.MapNode node, Mouse.Buttons buttons) => {
          if (buttons == Mouse.Buttons.Left) {
            MapNodeProperties properties = properties_[node];
            if (PlanetariumCamera.fetch.target !=
                properties.associated_map_object) {
              PlanetariumCamera.fetch.SetTarget(
                  properties.associated_map_object);
            }
          }
        };
    new_node.OnUpdateVisible += (KSP.UI.Screens.Mapview.MapNode node,
                                 KSP.UI.Screens.Mapview.MapNode.IconData
                                     icon) => {
      var visibility = visibility_modifiers_();
      icon.visible = properties_[node].visible &&
                     (visibility.show_unpinned || node.Pinned);
      icon.color = properties_[node].colour;
      node.Interactable = visibility.can_hover;
      // If the node shouldn't be interactable, then it cannot be actively
      // hovered. So, force remove the existing active hover state.
      // This is the same call used by the stock code.
      if (!node.Interactable && node.Hover) {
        node.OnPointerExit(null);
      }
    };
    new_node.OnUpdateType += (KSP.UI.Screens.Mapview.MapNode node,
                              KSP.UI.Screens.Mapview.MapNode.TypeData type) => {
      MapNodeProperties properties = properties_[node];
      type.oType = properties.object_type;
      switch (properties.object_type) {
        case MapObject.ObjectType.PatchTransition:
          type.pType = KSP.UI.Screens.Mapview.MapNode.PatchTransitionNodeType.
              Impact;
          break;
        case MapObject.ObjectType.ApproachIntersect:
          type.aType = KSP.UI.Screens.Mapview.MapNode.ApproachNodeType.
              CloseApproachOwn;
          break;
      }
    };
    new_node.OnUpdateCaption += (KSP.UI.Screens.Mapview.MapNode node,
                                 KSP.UI.Screens.Mapview.MapNode.CaptionData
                                     caption) => {
      // Fast path: In KSP MapView, captions are only displayed when the node
      // is pinned or hovered. Avoid expensive L10N formatting, string allocations,
      // and TextMeshProUGUI vertex regeneration when invisible!
      if (!node.Pinned && !node.Hover) {
        caption.Header = "";
        caption.captionLine1 = "";
        caption.captionLine2 = "";
        caption.captionLine3 = "";
        return;
      }
      var properties = properties_[node];
      string source;
      switch (properties.source) {
        case NodeSource.FlightPlan:
          source = L10N.CacheFormat("#Principia_MapNode_Planned");
          break;
        case NodeSource.Prediction:
          source = L10N.CacheFormat("#Principia_MapNode_Predicted");
          break;
        default:
          throw Log.Fatal($"Unexpected node source {properties.source}");
      }
      switch (properties.object_type) {
        case MapObject.ObjectType.Periapsis:
        case MapObject.ObjectType.Apoapsis: {
          var payload = properties.payload as LegacyMapNodePayload;
          CelestialBody celestial = properties.reference_frame.Centre();
          Vector3d position = payload.GetWorldPosition();
          double speed = payload.velocity.magnitude;
          caption.Header = L10N.CelestialString(
              properties.object_type == MapObject.ObjectType.Periapsis
                  ? "#Principia_MapNode_PeriapsisHeader"
                  : "#Principia_MapNode_ApoapsisHeader",
              new[]{celestial},
              source,
              celestial.GetAltitude(position).FormatN(0));
          caption.captionLine2 =
              L10N.CacheFormat("#Principia_MapNode_ApsisCaptionLine2",
                                speed.FormatN(0));
          break;
        }
        case MapObject.ObjectType.AscendingNode:
        case MapObject.ObjectType.DescendingNode: {
          string node_name =
              properties.object_type == MapObject.ObjectType.AscendingNode
                  ? L10N.CacheFormat("#Principia_MapNode_AscendingNode")
                  : L10N.CacheFormat("#Principia_MapNode_DescendingNode");
          string plane = properties.reference_frame.ReferencePlaneDescription();
          caption.Header = L10N.CacheFormat("#Principia_MapNode_NodeHeader",
                                            source,
                                            node_name,
                                            plane);
          var payload = properties.payload as NodeMapNodePayload;
          if (properties.reference_frame.Centre() == null) {
            caption.captionLine2 = L10N.CacheFormat(
                "#Principia_MapNode_NodeCaptionLine2WithoutAngle",
                payload.node.out_of_plane_velocity.FormatN(0));
          } else {
            caption.captionLine2 = L10N.CacheFormat(
                "#Principia_MapNode_NodeCaptionLine2WithAngle",
                payload.node.out_of_plane_velocity.FormatN(0),
                payload.node.apparent_inclination_in_degrees.FormatN(0));
          }
          break;
        }
        case MapObject.ObjectType.ApproachIntersect: {
          var payload = properties.payload as LegacyMapNodePayload;
          double separation =
              (properties.reference_frame.target_vessel.GetWorldPos3D() -
               payload.GetWorldPosition()).magnitude;
          double speed = payload.velocity.magnitude;
          caption.Header = L10N.CacheFormat("#Principia_MapNode_ApproachHeader",
                                            source,
                                            separation.FormatN(0));
          caption.captionLine2 = L10N.CacheFormat(
              "#Principia_MapNode_ApproachCaptionLine2",
              speed.FormatN(0));
          break;
        }
        case MapObject.ObjectType.PatchTransition: {
          var payload = properties.payload as LegacyMapNodePayload;
          CelestialBody celestial = properties.reference_frame.Centre();
          Vector3d position = payload.GetWorldPosition();
          double speed = payload.velocity.magnitude;
          caption.Header = L10N.CelestialString(
              "#Principia_MapNode_ImpactHeader",
              new[]{ celestial },
              source,
              celestial.GetAltitude(position).FormatN(0));
          caption.captionLine1 = "";
          caption.captionLine2 = L10N.CacheFormat(
              "#Principia_MapNode_ImpactCaptionLine2",
              speed.FormatN(0));
          break;
        }
      }
      caption.captionLine1 = "T" +
                             new PrincipiaTimeSpan(
                                     Planetarium.GetUniversalTime() -
                                     properties.payload.time).
                                 Format(with_leading_zeroes: false,
                                        with_seconds: true);
      // The font used by map nodes does not support the minus sign, so we
      // fall back to the hyphen-minus.
      string minus = Culture.culture.NumberFormat.NegativeSign;
      caption.Header = caption.Header?.Replace(minus, "-");
      caption.captionLine1 = caption.captionLine1?.Replace(minus, "-");
      caption.captionLine2 = caption.captionLine2?.Replace(minus, "-");
      caption.captionLine3 = caption.captionLine3?.Replace(minus, "-");
    };
    new_node.OnUpdatePosition += (KSP.UI.Screens.Mapview.MapNode node) => {
      if (properties_.TryGetValue(node, out var props) && props.payload != null) {
        return ScaledSpace.LocalToScaledSpace(props.payload.GetWorldPosition());
      }
      return UnityEngine.Vector3.zero;
    };
    return new_node;
  }

  // The payload is computed by the C++ side.
  private abstract class MapNodePayload {
    public double time;
    public Vector3d world_position;
    public Vector3d relative_offset;
    public CelestialBody anchor_body;
    public Vessel anchor_vessel;
    public bool has_relative_offset;

    public Vector3d GetWorldPosition() {
      if (has_relative_offset) {
        if (anchor_body != null) {
          return anchor_body.position + relative_offset;
        }
        if (anchor_vessel != null) {
          return anchor_vessel.GetWorldPos3D() + relative_offset;
        }
      }
      return world_position;
    }
  }

  // TODO(phl): Move away from legacy payloads.
  private class LegacyMapNodePayload : MapNodePayload {
    // Velocity in the plotting frame.  Note that the handedness is
    // inconsistent with World; for practical purposes only the norm or
    // individual coordinates of this vector should be used here.
    public Vector3d velocity;
  }

  private class NodeMapNodePayload : MapNodePayload {
    public Node node;
  }

  private class MapNodeProperties {
    public bool visible;
    public MapObject.ObjectType object_type;
    public MapNodePayload payload;
    public UnityEngine.Color colour;
    public MapObject associated_map_object;
    public ReferenceFrameSelector<PlottingFrameParameters> reference_frame;
    public NodeSource source;
  }

  private IntPtr Plugin => adapter_.Plugin();

  private readonly PrincipiaPluginAdapter adapter_;
  private Dictionary<Provenance, SingleProvenancePool> nodes_;
  private Dictionary<KSP.UI.Screens.Mapview.MapNode, MapNodeProperties>
      properties_;
  private Func<VisibilityModifiers> visibility_modifiers_;
}

}  // namespace ksp_plugin_adapter
}  // namespace principia
