---
name: hz-meta-xr-operator
description: Drives Meta VR and Horizon OS XR apps from an AI agent via the Meta XR Operator OpenXR API layer and MCP tools, including setup, head-pose control, controller input, and runtime verification.
allowed-tools:
  - Bash(metavr:*)
  - Bash(hzdb:*)
---

# Meta XR Operator Usage

Meta XR Operator is an OpenXR API Layer that gives AI agents the ability to perceive, understand, and manipulate VR/AR applications at runtime via MCP tools.

**Key constraint:** Meta XR Operator tools are ONLY available when the VR/XR application is actively running. Tools will return errors if the app is not active.

**Tool list varies:** Available tools depend on the running app and hardware, so the full list only exists while an app is running. Most agents pick up new tools automatically, but Muse Code does not -- if tools are missing (e.g. unity scene info tools, hardware-specific tools like gaze, or requested features that aren't in your existing list), a workaround to get the full tools list without requiring a restart is to call the in-app layer directly over HTTP MCP at `http://127.0.0.1:8720/mcp`: POST `initialize` (protocol version e.g. `2025-06-18`; the server names a supported version if it rejects yours), capture the `Mcp-Session-Id` response header, POST `notifications/initialized`, then `tools/list` / `tools/call` with that header set. Send `Accept: application/json, text/event-stream` on every call. Troubleshooting: `Session not initialized` means the `notifications/initialized` POST was skipped — re-send it on the same session; `Invalid JSON` means shell-quoting corruption — send the body from a file (`curl --data-binary @file`).

## Setup

Meta XR Operator is available as tools within an MCP server. If the tools are unavailable, prompt the user to do one of the following:

- If using Unity, follow steps within the Meta XR Core SDK's AI Tools Setup window to install the XR Operator MCP Server
- Download the standalone Meta XR Operator package from Meta Developer Center, which contains the XR Operator MCP Server
- Install Meta VR CLI v1.3.0+ from the Meta Quest Developer Hub (MQDH) or the Meta Developer Center, then install Meta XR Operator as a tool within the Meta VR CLI. The Meta XR Operator tools will then be federated and exposed directly within Meta VR CLI's MCP server. Confirm the proxy is healthy with `metavr xroperator status` before driving a session.

In Unity, prerequisites are the **Meta XR Core SDK** package and **Meta XR Simulator**; complete setup via the **Meta Project Setup Tool** / **Meta XR Settings** window and check Meta XR Operator's status in the toolbar under **Meta**. These skills ship in the Meta XR Core SDK at `Editor/MetaXROperator/Skills`.

## When to Use

Use Meta XR Operator to **interact with a running XR application at runtime**:

- **Iteration** — Launch the app, verify features, interact, then exit to make changes.
- **Debugging** — Reproduce and investigate runtime bugs by inspecting poses, scene state, and visuals.
- **Test Automation** — Programmatically verify features. See the **hz-meta-xr-operator-unity-test-mechanics** skill for more details.

The app can run in **Meta XR Simulator** or on a **physical device**. With Meta XR Simulator, no headset or adb connection is needed. For physical headsets, see **hz-meta-xr-operator-unity-meta-quest**.

## Orientation & Navigation

- **Check for a scene-specific skill first.** Scenes sometimes ship their own skill in or near the scene's location. Before starting, take a brief moment to locate the scene and see if such a skill exists.
- **Start by orienting yourself.** Use `openxr_get_head_pose` + `openxr_capture_composited_image` to see where you are.
- **Use data before vision.** Query scene data (`get_scene_root_objects`, `get_children`, `get_world_pose`) to understand layout via coordinates, then confirm visually with screenshots.
- **Don't guess positions** — always use `get_world_pose` for both target and rig. Check positions relative to the camera rig, not world origin.
- **If you can't find something visually**, step back to get a wider field of view. If still not found after 2-3 screenshots, use data tools to get coordinates and navigate there directly.

For detailed coordinate math and controller positioning, see **hz-meta-xr-operator-coordinates**. For aiming grabbed objects (guns, tools), see **hz-meta-xr-operator-grabbed-objects**.

## Verification

- **Verify both via data AND visually.** Confirm results using coordinate/scene-data tools AND compositor screenshots.
- **Take screenshots after key interactions** to verify visual state matches expectations.
- **Check for visual artifacts** (aliasing, noise) by capturing screenshots from multiple angles.

## Input Simulation

XR Operator provides tools for AI agents to fully drive and inject simulated inputs for a variety of input methods without requiring physical devices, controllers, or hands.

- **Smooth movements:** Use the `duration` parameter on `openxr_set_head_pose` and `openxr_set_controller_pose` for realistic motion.
- **Controller input values:** Buttons are 0/1, Trigger/Grip are 0.0–1.0, Thumbstick is -1.0 to 1.0 on each axis.
- **Hold inputs across frames** — setting an input to 1 and immediately to 0 may be missed by `Update()`.
- For information about simulating hand tracking and gaze, see the **hz-meta-xr-operator-hand-tracking** skill.

Note: **You drive simulated devices — nothing needs to be "active" first.** Every pose and input you set is injected into the app, which observes it exactly as if it came from real hardware. Invalid, untracked, or non-active inputs for controllers and hands might be normal on a fresh session - not errors. Just set a controller or hand pose, and the respective input should become active. Only if the app still doesn't respond *after* you drive input should you suspect project config.

## Common Pitfalls

- **Tools only work when the app is running.** Connection errors mean the app may have stopped.
- **Coordinate systems matter.** OpenXR is right-handed (-Z forward). Unity is left-handed (Z forward). See the **hz-meta-xr-operator-coordinates** skill for conversion.
