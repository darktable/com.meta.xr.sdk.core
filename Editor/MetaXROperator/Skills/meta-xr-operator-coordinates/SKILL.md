---
name: hz-meta-xr-operator-coordinates
description: Converts between Unity, OpenXR, and tracking-origin coordinates so AI agents can position the head and controllers correctly in Meta VR and Horizon OS apps, covering tracking origin matching, Unity-to-OpenXR conversion, controller positioning, and aiming a controller ray at UI elements.
allowed-tools:
  - Bash(metavr:*)
  - Bash(hzdb:*)
---

# Meta XR Operator Coordinates & Movement

Coordinate math for moving the VR head/camera, positioning controllers, and aiming at objects at runtime. The Unity app must have an OVRCameraRig in the scene.

For aiming **grabbed objects** (guns, tools, etc.), see the **hz-meta-xr-operator-grabbed-objects** skill, which covers offset calibration and precise aiming. For hand tracking, see the **hz-meta-xr-operator-hand-tracking** skill.

## Key Concept: Tracking Origin

`unity_get_world_pose` returns a `tracking_origin` field that tells you which OpenXR reference space matches Unity's coordinate system. **Always use `tracking_origin.OpenXR` as the `base_space` parameter** when setting poses — this eliminates manual Y-offset hacks.

| OVR Setting | OpenXR base_space | Description |
|---|---|---|
| EyeLevel | `local` | Origin at headset eye level |
| FloorLevel | `local_floor` | Origin at floor level |
| Stage | `stage` | Origin at stage/room center |

Re-query `unity_get_world_pose` if the scene or project changes — the tracking origin may differ.

## World-to-OpenXR Position Formula

Convert relative to the **tracking-space origin**, not the eye — `CenterEyeAnchor` only equals the origin at recenter, so subtracting the eye drifts once the head moves and the placement misses.

```
origin = unity_get_world_pose("OVRCameraRig/TrackingSpace")   // identity rotation in local/EyeLevel

openxr_x =   (target_world_x - origin_x)
openxr_y =   (target_world_y - origin_y)
openxr_z = -( target_world_z - origin_z)
```

Use `tracking_origin.OpenXR` as `base_space`. Aim **directions** (`target_world - source_world`, Z negated) are frame-independent — the origin choice doesn't affect them.

## Coordinate Conversion (Unity ↔ OpenXR)

| Component | Conversion |
|-----------|-----------|
| Position X | Same |
| Position Y | Same |
| Position Z | **Negate** |
| Quaternion X | **Negate** |
| Quaternion Y | **Negate** |
| Quaternion Z | Same |
| Quaternion W | Same |

## Steps: Face an Object (Head)

1. **Get positions** (parallel): `unity_get_world_pose("TargetObject")` + `unity_get_world_pose("CenterEyeAnchor")`
2. **Direction in Unity space**: `math_sub(target_pos, eye_pos)`
3. **Convert to OpenXR**: negate Z component
4. **Build quaternion**: `math_build_quat(front_direction: [dir.x, dir.y, dir.z])` — include Y to look up/down, or zero Y for horizontal view
5. **Set head pose**: `openxr_set_head_pose(orientation, base_space: tracking_origin.OpenXR)`
6. **Verify**: `openxr_capture_composited_image()`

## Steps: Position a Controller at a Target

**IMPORTANT: Head movement shifts controller world positions.** Always: face the target **first**, then requery all positions before computing the controller pose. Using stale pre-head-movement positions will place the controller in the wrong location.

1. **Face the target** using the head steps above — the target must be visible to the camera, just as a real user would look at what they're interacting with
2. **Requery** `unity_get_world_pose("TargetObject")` (the tracking-space origin is constant, so it never needs re-querying)
3. **Apply the origin-based formula** above to compute the controller position in OpenXR
4. **Set controller pose**: `openxr_set_controller_pose(hand, position, base_space: tracking_origin.OpenXR)`
5. **Verify**: `unity_get_world_pose("OVRCameraRig/TrackingSpace/RightHandAnchor")` to confirm placement

## Grip vs Aim Pose

- **Grip pose** (`pose_type: grip`, default) — where the hand holds the controller. **This drives the UI ray** in Meta interaction stacks (see below), and is also used for positioning/grabbing.
- **Aim pose** (`pose_type: aim`) — the OpenXR pointer pose. While most Meta OVR ray components do not use this, other apps could, so make sure to set both grip and aim.

## Aiming a Controller Ray at UI Elements

**One method for every stack.** The UI ray is cast from a **controller-attached transform**, not the OpenXR aim pose — so **drive the grip** and run a short measure-and-correct loop that absorbs the (stack-specific, unknown) grip→ray offset. Same for Core SDK, ISDK, and XRI; only the transform path differs:

| Stack | Ray-origin transform (its forward *is* the ray) | Reticle (live hit point) |
|---|---|---|
| Core SDK (`OVRRaycaster`/`OVRInputModule`) | `OVRCameraRig/TrackingSpace/RightHandAnchor/RightControllerAnchor/OVRControllerPrefab` | `…/OVRControllerPrefab/OVRRayHelper/Cursor` |
| Meta Interaction SDK (ISDK) | `…/ControllerRayInteractor/ControllerPointerPose` (`RayInteractor._rayOrigin`) | `…/ControllerRayInteractor/Visuals/RaycasterCursorVisual` |
| Unity XRI | the XR Ray Interactor transform | its reticle (may also honor the aim pose) |

(Left hand: swap `Right`→`Left` / `…InteractorsRight`→`…InteractorsLeft`.)

**Steps** — use `tracking_origin.OpenXR` as `base_space` throughout; converges in 1–2 passes:

**First placement:** if the controller was never placed (inactive/untracked), first set a rough controller pose in front of the head — there is no measurable ray until you do. Use the euler-forward form for that pass (the reticle is off-surface); the first correction may be large since the grip→ray offset is still unknown.

1. **Target**: `unity_find_canvases` → `unity_find_interactables` → `unity_get_world_pose` the element.
2. **Read the ray-origin transform** → origin `O` + forward `Rdir`. From its Unity euler (roll-independent): `Rdir = (sin(yaw)·cos(pitch), −sin(pitch), cos(yaw)·cos(pitch))` — or, once the ray is on a surface, `Rdir = normalize(reticle − O)`.
3. **Solve** in OpenXR (negate Z on all positions/directions): `D = normalize(convertZ(target) − convertZ(O))`; `ΔR = fromToRotation(convertZ(Rdir) → D)` = `[axis·sin(θ/2), cos(θ/2)]`, `axis = normalize(Rdir_xr × D)`, `θ = acos(Rdir_xr · D)`.
4. **Rotate the grip**: `newGrip = math_multiply_quat(ΔR, currentGrip)`, then set it (`pose_type: grip`). **Premultiply the full quaternion — do NOT rebuild the grip from a forward vector (loses roll, breaks the offset).** Optionally set `pose_type: aim` to `math_build_quat(D)` for XRI.
5. **Refine**: rotating the grip also shifts `O`, so re-read the transform and repeat 2–4 until the reticle is on target (often one-shot at range).
6. **Click**: verify `reticle ≈ target` (the reticle is valid only while on a surface — its cursor deactivates off-surface), then pull the **Trigger** across frames for standard "click" interactions. Note that other interactions may require different kinds of input (i.e. dragging a slider).

## Tips

- **Smooth movement**: `duration_seconds: 1` for rotations, `1.5-2` for position+rotation, `0` for instant.
- **Never guess positions**: always use `unity_get_world_pose` for both target and eye.
- **Hold inputs across frames** — setting an input to 1 and immediately to 0 may be missed by `Update()`.
- **Confirm the math visually** — after a head move or controller placement on hardware runs, pull `metavr capture screenshot` to confirm the computed pose matches what's rendered.

## Common Pitfalls

1. **Aiming at targets not in view** — always turn the head to face the target first. Aiming at something behind the camera produces unrealistic and unreliable results.
2. **Using stale positions after head movement** — see the controller positioning section above.
3. **Using wrong base_space** — must match `tracking_origin.OpenXR` from `unity_get_world_pose`. Using a mismatched space produces incorrect Y offsets.
4. **Forgetting to negate Z** — Unity Z forward vs OpenXR -Z forward.
5. **Using world position directly as OpenXR position** — must subtract the tracking-space origin first (not the eye).
6. **Not verifying with unity_get_world_pose** — always read back the anchor's world position to confirm.
